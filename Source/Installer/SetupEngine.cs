using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;
using Microsoft.Win32;

// The installer owns only the files enumerated here and in its validated manifest.
// User settings and the separately installed PawnIO driver are never removed.
internal static class SetupEngine
{
    public const string ProductVersion = "3.5.3";
#if SETUP_TEST
    public const string InstallDirectoryName = "MiniMonitor-SetupTest";
    public const string TaskPrefix = "MiniMonitor-SetupTest-";
    public const string UninstallKey = "MiniMonitor-SetupTest";
    public const string ShortcutName = "Mini Monitor Setup Test";
#else
    public const string InstallDirectoryName = "MiniMonitor";
    public const string TaskPrefix = "MiniMonitor-";
    public const string UninstallKey = "MiniMonitor";
    public const string ShortcutName = "Mini Monitor";
#endif
    public const string ManifestName = "installed-files.txt";
    public const string UninstallerName = "MiniMonitor.Uninstall.exe";
    public static string InstallRoot { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), InstallDirectoryName); } }
    public static string TaskName { get { using (WindowsIdentity id = WindowsIdentity.GetCurrent()) return TaskPrefix + id.User.Value; } }
    public static string RegistryPath { get { return @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + UninstallKey; } }
    public static string StartMenuShortcut { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), ShortcutName + ".lnk"); } }
    public static string DesktopShortcut { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), ShortcutName + ".lnk"); } }
    public static bool RestartRequired { get; private set; }
    static string InstalledExe { get { return Path.Combine(InstallRoot, "MiniMonitor.exe"); } }

    static readonly HashSet<string> PayloadNames = new HashSet<string>(new[] {
        "MiniMonitor.exe", "MiniMonitor.exe.config", "PANDUAN.txt", "THIRD-PARTY.txt", "Startup.ps1",
        "Jalankan-Admin.cmd", "Nonaktifkan-Startup.cmd", "Pasang-Otomatis.cmd", "Perbarui-Aplikasi.cmd",
        @"fps\PresentMon.exe", @"setup\PawnIO_setup.exe",
        @"sensors\BlackSharp.Core.dll", @"sensors\DiskInfoToolkit.dll", @"sensors\HidSharp.dll",
        @"sensors\LibreHardwareMonitorLib.dll", @"sensors\LICENSE-LibreHardwareMonitor.txt",
        @"sensors\RAMSPDToolkit-NDD.dll", @"sensors\System.Buffers.dll", @"sensors\System.Memory.dll",
        @"sensors\System.Numerics.Vectors.dll", @"sensors\System.Runtime.CompilerServices.Unsafe.dll",
        @"licenses\BlackSharp-LICENSE.txt", @"licenses\DiskInfoToolkit-LICENSE.txt", @"licenses\DotNet-LICENSE.txt",
        @"licenses\hidsharp-LICENSE.txt", @"licenses\PawnIO-COPYING.txt", @"licenses\PawnIO-Modules-COPYING.txt",
        @"licenses\PawnIO-README.md", @"licenses\PresentMon-LICENSE.txt", @"licenses\RAMSPDToolkit-LICENSE.txt"
    }, StringComparer.OrdinalIgnoreCase);

    public static bool IsInstalled() { return File.Exists(InstalledExe); }
    public static string GetInstalledVersion()
    {
        using (RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
        using (RegistryKey key = machine.OpenSubKey(RegistryPath))
            return key == null ? "" : Convert.ToString(key.GetValue("DisplayVersion", ""));
    }
    public static bool GetStartupEnabled()
    {
        using (TaskStore tasks = new TaskStore())
        {
            TaskState current = tasks.Read(TaskName);
            return current == null || current.Enabled;
        }
    }

    public static void Install(string payloadZipPath, bool startup, bool desktop, Action<string> status)
    {
        RestartRequired = false;
        RequireAdministrator();
        EnsureRoot();
        RejectDowngrade();
        RejectUnrelatedShortcut(StartMenuShortcut);
        if (desktop) RejectUnrelatedShortcut(DesktopShortcut);
        string temporary = NewTemporaryDirectory();
        bool keepBackup = false;
        string stage = Path.Combine(temporary, "stage");
        string backup = Path.Combine(temporary, "backup");
        Directory.CreateDirectory(stage);
        Directory.CreateDirectory(backup);
        try
        {
            Report(status, "Memeriksa dan menyiapkan berkas aplikasi...");
            List<string> names = ExtractPayload(payloadZipPath, stage);
            string self = Assembly.GetExecutingAssembly().Location;
            File.Copy(self, Path.Combine(stage, UninstallerName));
            names.Add(UninstallerName);
            names.Add(ManifestName);
            names.Sort(StringComparer.OrdinalIgnoreCase);
            File.WriteAllLines(Path.Combine(stage, ManifestName), names.ToArray(), new UTF8Encoding(false));

            Report(status, "Menunggu Mini Monitor dan pemulihan kipas selesai...");
            CloseApplication();
            List<string> previous = ReadManifest();
            HashSet<string> affected = new HashSet<string>(previous, StringComparer.OrdinalIgnoreCase);
            affected.UnionWith(names);
            using (TaskStore tasks = new TaskStore())
            {
                TaskState oldTask = tasks.Read(TaskName);
                if (oldTask != null && !TaskTargetsInstall(oldTask.Xml))
                    throw new InvalidOperationException("Nama tugas startup sudah digunakan oleh aplikasi lain: " + TaskName);
                RegistryState oldRegistry = ReadRegistry();
                FileState startLink = FileState.Capture(StartMenuShortcut);
                FileState desktopLink = FileState.Capture(DesktopShortcut);
                Dictionary<string, bool> existed = BackupFiles(affected, backup);
                List<string> touched = new List<string>();
                bool taskTouched = false, registryTouched = false, shortcutsTouched = false;
                try
                {
                    Report(status, "Memasang Mini Monitor " + ProductVersion + "...");
                    Directory.CreateDirectory(InstallRoot);
                    foreach (string name in names)
                    {
                        string target = SafePath(InstallRoot, name);
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        touched.Add(name);
                        File.Copy(SafePath(stage, name), target, true);
                    }
                    foreach (string old in previous)
                    {
                        if (names.Contains(old, StringComparer.OrdinalIgnoreCase)) continue;
                        touched.Add(old);
                        File.Delete(SafePath(InstallRoot, old));
                    }
                    taskTouched = true;
                    tasks.Register(TaskName, oldTask == null ? BuildTaskXml(startup) : UpdateTaskXml(oldTask.Xml, startup));
                    shortcutsTouched = true;
                    WriteShortcut(StartMenuShortcut);
                    if (desktop) WriteShortcut(DesktopShortcut);
                    else if (ShortcutTargetsInstall(DesktopShortcut)) File.Delete(DesktopShortcut);
                    registryTouched = true;
                    WriteRegistry(names);
#if SETUP_TEST
                    if (TestFailurePoint == "after-configuration") throw new IOException("Simulasi kegagalan setelah konfigurasi pemasangan.");
#endif
                }
                catch (Exception original)
                {
                    List<string> failures = new List<string>();
                    Rollback(delegate { RestoreFiles(touched, existed, backup); }, failures);
                    if (taskTouched) Rollback(delegate { tasks.Restore(TaskName, oldTask); }, failures);
                    if (registryTouched) Rollback(delegate { RestoreRegistry(oldRegistry); }, failures);
                    if (shortcutsTouched)
                    {
                        Rollback(delegate { startLink.Restore(); }, failures);
                        Rollback(delegate { desktopLink.Restore(); }, failures);
                    }
                    PruneOwnedDirectories();
                    if (failures.Count != 0)
                    {
                        keepBackup = true;
                        throw new InvalidOperationException(original.Message + "\r\nPemulihan belum lengkap: " + String.Join("; ", failures.ToArray()) + "\r\nCadangan: " + backup, original);
                    }
                    throw;
                }
            }
            Report(status, "Pemasangan selesai. Pengaturan pribadi tetap tersimpan.");
        }
        finally { if (!keepBackup) CleanupTemporary(temporary); }
    }

    public static void Uninstall(Action<string> status)
    {
        RestartRequired = false;
        RequireAdministrator();
        EnsureRoot();
        Report(status, "Menunggu Mini Monitor dan pemulihan kipas selesai...");
        CloseApplication();
        List<string> names = ReadManifest();
        if (names.Count == 0) throw new InvalidOperationException("Daftar berkas pemasangan tidak ditemukan. Pasang ulang sebelum menghapus aplikasi.");
        string temporary = NewTemporaryDirectory();
        bool keepBackup = false;
        string backup = Path.Combine(temporary, "backup");
        Directory.CreateDirectory(backup);
        try
        {
            using (TaskStore tasks = new TaskStore())
            {
                List<TaskState> oldTasks = tasks.ReadOwned();
                RegistryState oldRegistry = ReadRegistry();
                FileState startLink = FileState.Capture(StartMenuShortcut);
                FileState desktopLink = FileState.Capture(DesktopShortcut);
                Dictionary<string, bool> existed = BackupFiles(names, backup);
                List<string> touched = new List<string>();
                bool configTouched = false;
                string executing = Path.GetFullPath(Assembly.GetExecutingAssembly().Location);
                string selfToDelete = null;
                try
                {
                    Report(status, "Menghapus berkas Mini Monitor...");
                    foreach (string name in names)
                    {
                        string target = SafePath(InstallRoot, name);
                        if (String.Equals(target, executing, StringComparison.OrdinalIgnoreCase))
                        {
                            selfToDelete = target;
                            continue;
                        }
                        touched.Add(name);
                        File.Delete(target);
                    }
                    configTouched = true;
                    foreach (TaskState task in oldTasks) tasks.Delete(task.Name);
                    if (ShortcutTargetsInstall(StartMenuShortcut)) File.Delete(StartMenuShortcut);
                    if (ShortcutTargetsInstall(DesktopShortcut)) File.Delete(DesktopShortcut);
                    DeleteRegistry();
                    // Move the running uninstaller to a unique name before scheduling deletion.
                    // A reinstall before reboot must never inherit a pending delete for its path.
                    if (selfToDelete != null)
                    {
                        string retired = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "." + InstallDirectoryName + "-uninstall-" + Guid.NewGuid().ToString("N") + ".exe");
                        File.Move(selfToDelete, retired);
                        if (!MoveFileEx(retired, null, 4))
                        {
                            int error = Marshal.GetLastWin32Error();
                            File.Move(retired, selfToDelete);
                            throw new System.ComponentModel.Win32Exception(error, "Tidak dapat menjadwalkan penghapusan uninstaller.");
                        }
                        RestartRequired = true;
                    }
                }
                catch (Exception original)
                {
                    List<string> failures = new List<string>();
                    if (selfToDelete != null && !File.Exists(selfToDelete))
                        Rollback(delegate { File.Copy(SafePath(backup, UninstallerName), selfToDelete); }, failures);
                    Rollback(delegate { RestoreFiles(touched, existed, backup); }, failures);
                    if (configTouched)
                    {
                        foreach (TaskState task in oldTasks) { TaskState saved = task; Rollback(delegate { tasks.Restore(saved.Name, saved); }, failures); }
                        Rollback(delegate { RestoreRegistry(oldRegistry); }, failures);
                        Rollback(delegate { startLink.Restore(); }, failures);
                        Rollback(delegate { desktopLink.Restore(); }, failures);
                    }
                    if (failures.Count != 0)
                    {
                        keepBackup = true;
                        throw new InvalidOperationException(original.Message + "\r\nPemulihan belum lengkap: " + String.Join("; ", failures.ToArray()) + "\r\nCadangan: " + backup, original);
                    }
                    throw;
                }
            }
            PruneOwnedDirectories();
            Report(status, RestartRequired ? "Aplikasi dihapus. Berkas uninstaller akan dibersihkan setelah Windows dimulai ulang." : "Aplikasi dihapus. Pengaturan pribadi dan PawnIO tetap tersimpan.");
        }
        finally { if (!keepBackup) CleanupTemporary(temporary); }
    }

    static List<string> ExtractPayload(string zipPath, string stage)
    {
        List<string> names = new List<string>();
        HashSet<string> unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        using (ZipArchive zip = ZipFile.OpenRead(zipPath))
        {
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                string name = entry.FullName.Replace('/', '\\');
                if (name.StartsWith("MiniMonitor\\", StringComparison.OrdinalIgnoreCase)) name = name.Substring(12);
                if (name.Length == 0) continue;
                // Reject symlink entries, even when their destination name is allowlisted.
                if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                    throw new InvalidDataException("Arsip mengandung tautan berkas.");
                if (name.EndsWith("\\", StringComparison.Ordinal))
                {
                    string directory = name.TrimEnd('\\');
                    ValidateRelative(directory);
                    if (!PayloadNames.Any(delegate(string p) { return p.StartsWith(directory + "\\", StringComparison.OrdinalIgnoreCase); }))
                        throw new InvalidDataException("Folder tidak dikenal dalam arsip: " + name);
                    continue;
                }
                ValidateRelative(name);
                if (!PayloadNames.Contains(name) || !unique.Add(name))
                    throw new InvalidDataException("Berkas tidak dikenal atau duplikat dalam arsip: " + name);
                totalBytes += entry.Length;
                if (entry.Length < 0 || totalBytes > 256L * 1024 * 1024)
                    throw new InvalidDataException("Ukuran arsip melewati batas installer.");
                string target = SafePath(stage, name);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                using (Stream source = entry.Open())
                using (FileStream output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None)) source.CopyTo(output);
                names.Add(name);
            }
        }
        foreach (string required in PayloadNames)
            if (!unique.Contains(required)) throw new InvalidDataException("Berkas wajib tidak tersedia: " + required);
        return names;
    }

    static void ValidateRelative(string relative)
    {
        if (String.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.IndexOf(':') >= 0 || relative.IndexOf('/') >= 0)
            throw new InvalidDataException("Jalur arsip tidak valid.");
        foreach (string part in relative.Split('\\'))
            if (part.Length == 0 || part == "." || part == ".." || part.EndsWith(".") || part.EndsWith(" ") || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidDataException("Jalur arsip tidak valid: " + relative);
    }
    static string SafePath(string root, string relative)
    {
        ValidateRelative(relative);
        string fullRoot = Path.GetFullPath(root).TrimEnd('\\') + "\\";
        string path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Jalur keluar dari folder pemasangan.");
        EnsureNoReparse(path);
        return path;
    }
    static void EnsureNoReparse(string path)
    {
        string cursor = Path.GetFullPath(path);
        while (!String.IsNullOrEmpty(cursor))
        {
            if ((File.Exists(cursor) || Directory.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Folder/berkas pemasangan berupa tautan: " + cursor);
            cursor = Path.GetDirectoryName(cursor);
        }
    }
    static void EnsureRoot()
    {
        string expected = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), InstallDirectoryName));
        if (!String.Equals(Path.GetFullPath(InstallRoot), expected, StringComparison.OrdinalIgnoreCase)) throw new IOException("Folder pemasangan tidak valid.");
        EnsureNoReparse(InstallRoot);
    }
    static List<string> ReadManifest()
    {
        string file = SafePath(InstallRoot, ManifestName);
        if (!File.Exists(file)) return new List<string>();
        List<string> names = new List<string>();
        HashSet<string> unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in File.ReadAllLines(file))
        {
            string name = raw.Trim();
            ValidateRelative(name);
            if ((!PayloadNames.Contains(name) && name != UninstallerName && name != ManifestName) || !unique.Add(name))
                throw new InvalidDataException("Daftar berkas pemasangan tidak valid: " + name);
            SafePath(InstallRoot, name);
            names.Add(name);
        }
        if (!unique.Contains(ManifestName) || !unique.Contains("MiniMonitor.exe") || !unique.Contains(UninstallerName))
            throw new InvalidDataException("Daftar berkas pemasangan tidak lengkap.");
        return names;
    }

    static Dictionary<string, bool> BackupFiles(IEnumerable<string> names, string backup)
    {
        Dictionary<string, bool> existed = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in names)
        {
            string current = SafePath(InstallRoot, name);
            bool found = File.Exists(current);
            existed[name] = found;
            if (!found) continue;
            string target = SafePath(backup, name);
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            File.Copy(current, target);
        }
        return existed;
    }
    static void RestoreFiles(IEnumerable<string> touched, Dictionary<string, bool> existed, string backup)
    {
        List<Exception> failures = new List<Exception>();
        foreach (string name in touched.Distinct(StringComparer.OrdinalIgnoreCase).Reverse())
        {
            try
            {
                string target = SafePath(InstallRoot, name);
                if (existed[name])
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    File.Copy(SafePath(backup, name), target, true);
                }
                else File.Delete(target);
            }
            catch (Exception ex) { failures.Add(ex); }
        }
        if (failures.Count != 0) throw new AggregateException(failures);
    }
    static string NewTemporaryDirectory()
    {
        string parent = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        EnsureNoReparse(parent);
        string path = Path.Combine(parent, "." + InstallDirectoryName + "-setup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
    static void CleanupTemporary(string path)
    {
        // A failed rollback leaves its backup for recovery. Success removes only
        // the GUID directory created by this invocation, never the app directory.
        try
        {
            string parent = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)).TrimEnd('\\') + "\\";
            string full = Path.GetFullPath(path);
            if (!full.StartsWith(parent + "." + InstallDirectoryName + "-setup-", StringComparison.OrdinalIgnoreCase)) return;
            EnsureNoReparse(full);
            if (Directory.Exists(full)) Directory.Delete(full, true);
        }
        catch { }
    }
    static void PruneOwnedDirectories()
    {
        HashSet<string> dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in PayloadNames)
        {
            string directory = Path.GetDirectoryName(name);
            if (!String.IsNullOrEmpty(directory)) dirs.Add(directory);
        }
        foreach (string relative in dirs.OrderByDescending(delegate(string n) { return n.Length; }))
        {
            try { string folder = SafePath(InstallRoot, relative); if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder); }
            catch { }
        }
        try { if (Directory.Exists(InstallRoot) && !Directory.EnumerateFileSystemEntries(InstallRoot).Any()) Directory.Delete(InstallRoot); } catch { }
    }

    static void CloseApplication()
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        List<Process> initial = KnownProcesses();
        try
        {
            foreach (Process process in initial)
            {
                if (process.HasExited || IsFanHelper(process)) continue;
                process.CloseMainWindow();
            }
            foreach (Process process in initial)
            {
                if (process.HasExited || IsFanHelper(process)) continue;
                if (!process.WaitForExit(Math.Max(1, 25000 - (int)elapsed.ElapsedMilliseconds)))
                    throw new IOException("Mini Monitor belum selesai ditutup. Tutup aplikasi, lalu ulangi pemasangan.");
            }
        }
        finally { foreach (Process process in initial) process.Dispose(); }
        // A helper may be created during the first snapshot; rescan after UI exit.
        List<Process> remaining = KnownProcesses();
        try
        {
            foreach (Process process in remaining)
            {
                if (process.HasExited) continue;
                if (!IsFanHelper(process)) throw new IOException("Mini Monitor dibuka kembali. Tutup aplikasi, lalu ulangi.");
                if (!process.WaitForExit(Math.Max(1, 25000 - (int)elapsed.ElapsedMilliseconds)))
                    throw new IOException("Pemulihan kipas GPU belum selesai. Tunggu sebentar, lalu ulangi.");
            }
        }
        finally { foreach (Process process in remaining) process.Dispose(); }
        remaining = KnownProcesses();
        try { if (remaining.Count != 0) throw new IOException("Mini Monitor masih berjalan. Ulangi setelah aplikasi ditutup."); }
        finally { foreach (Process process in remaining) process.Dispose(); }
    }
    static List<Process> KnownProcesses()
    {
        List<Process> result = new List<Process>();
        try
        {
            foreach (Process process in Process.GetProcessesByName("MiniMonitor"))
            {
                try
                {
                    if (!process.HasExited && String.Equals(Path.GetFullPath(process.MainModule.FileName), InstalledExe, StringComparison.OrdinalIgnoreCase)) result.Add(process);
                    else process.Dispose();
                }
                catch { process.Dispose(); throw; }
            }
            return result;
        }
        catch { foreach (Process process in result) process.Dispose(); throw; }
    }
    static bool IsFanHelper(Process process)
    {
        if (process.HasExited) return false;
        using (ManagementObject item = new ManagementObject("Win32_Process.Handle='" + process.Id + "'"))
        {
            try
            {
                item.Get();
                string line = Convert.ToString(item["CommandLine"]);
                if (String.IsNullOrEmpty(line)) throw new IOException("Perintah proses Mini Monitor tidak dapat diperiksa. Tutup aplikasi, lalu ulangi.");
                return Regex.IsMatch(line, @"(?:^|\s)--fan-boost(?:\s|$)", RegexOptions.CultureInvariant);
            }
            catch { if (process.HasExited) return false; throw; }
        }
    }

    static string BuildTaskXml(bool enabled)
    {
        string sid;
        using (WindowsIdentity identity = WindowsIdentity.GetCurrent()) sid = identity.User.Value;
        string exe = System.Security.SecurityElement.Escape(InstalledExe);
        string root = System.Security.SecurityElement.Escape(InstallRoot);
        return "<?xml version=\"1.0\" encoding=\"UTF-16\"?><Task version=\"1.3\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">" +
            "<RegistrationInfo><Description>Mini Monitor: jam, durasi aplikasi, FPS dan sensor perangkat.</Description></RegistrationInfo>" +
            "<Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + sid + "</UserId></LogonTrigger></Triggers>" +
            "<Principals><Principal id=\"Author\"><UserId>" + sid + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>" +
            "<Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>" +
            "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><AllowHardTerminate>false</AllowHardTerminate><StartWhenAvailable>false</StartWhenAvailable>" +
            "<Enabled>" + (enabled ? "true" : "false") + "</Enabled><ExecutionTimeLimit>PT0S</ExecutionTimeLimit></Settings>" +
            "<Actions Context=\"Author\"><Exec><Command>" + exe + "</Command><Arguments>--startup</Arguments><WorkingDirectory>" + root + "</WorkingDirectory></Exec></Actions></Task>";
    }
    static string UpdateTaskXml(string original, bool enabled)
    {
        const string nsUri = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        XmlDocument document = new XmlDocument();
        document.XmlResolver = null;
        document.LoadXml(original);
        XmlNamespaceManager ns = new XmlNamespaceManager(document.NameTable);
        ns.AddNamespace("t", nsUri);
        XmlNode actions = document.SelectSingleNode("/t:Task/t:Actions", ns);
        if (actions == null) throw new InvalidDataException("Tugas startup tidak memiliki tindakan.");
        while (actions.FirstChild != null) actions.RemoveChild(actions.FirstChild);
        XmlElement exec = document.CreateElement("Exec", nsUri);
        foreach (string[] item in new[] { new[] { "Command", InstalledExe }, new[] { "Arguments", "--startup" }, new[] { "WorkingDirectory", InstallRoot } })
        {
            XmlElement element = document.CreateElement(item[0], nsUri);
            element.InnerText = item[1];
            exec.AppendChild(element);
        }
        actions.AppendChild(exec);
        XmlNode settings = document.SelectSingleNode("/t:Task/t:Settings", ns);
        if (settings == null) throw new InvalidDataException("Tugas startup tidak memiliki pengaturan.");
        XmlNode switchNode = settings.SelectSingleNode("t:Enabled", ns);
        if (switchNode == null) { switchNode = document.CreateElement("Enabled", nsUri); settings.AppendChild(switchNode); }
        switchNode.InnerText = enabled ? "true" : "false";
        return document.OuterXml;
    }
    static void RejectDowngrade()
    {
        if (!File.Exists(InstalledExe)) return;
        Version installed;
        string value = FileVersionInfo.GetVersionInfo(InstalledExe).FileVersion;
        if (Version.TryParse(value, out installed) && installed > new Version(ProductVersion + ".0"))
            throw new InvalidOperationException("Versi Mini Monitor yang terpasang lebih baru (" + value + "). Gunakan installer yang sama atau lebih baru.");
    }
#if SETUP_TEST
    // Test-only fault after every state category has changed, so integration
    // checks exercise rollback without locking files or changing production.
    public static string TestFailurePoint;
#endif
    static bool TaskTargetsInstall(string xml)
    {
        XmlDocument document = new XmlDocument();
        document.XmlResolver = null;
        document.LoadXml(xml);
        XmlNamespaceManager ns = new XmlNamespaceManager(document.NameTable);
        ns.AddNamespace("t", "http://schemas.microsoft.com/windows/2004/02/mit/task");
        XmlNodeList actions = document.SelectNodes("/t:Task/t:Actions/*", ns);
        if (actions == null || actions.Count != 1 || actions[0].LocalName != "Exec") return false;
        XmlNode command = actions[0].SelectSingleNode("t:Command", ns);
        if (command == null) return false;
        string value = Environment.ExpandEnvironmentVariables(command.InnerText.Trim().Trim('"'));
        try { return String.Equals(Path.GetFullPath(value), InstalledExe, StringComparison.OrdinalIgnoreCase); } catch { return false; }
    }
    sealed class TaskState { public string Name, Xml; public bool Enabled; }
    sealed class TaskStore : IDisposable
    {
        object service, folder;
        public TaskStore()
        {
            dynamic scheduler = service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
            scheduler.Connect();
            folder = scheduler.GetFolder("\\");
        }
        public TaskState Read(string name)
        {
            object task = null;
            try
            {
                dynamic found = task = ((dynamic)folder).GetTask(name);
                return new TaskState { Name = name, Xml = found.Xml, Enabled = found.Enabled };
            }
            catch (FileNotFoundException) { return null; }
            catch (COMException ex) { if (ex.ErrorCode == unchecked((int)0x80070002)) return null; throw; }
            finally { Release(task); }
        }
        public List<TaskState> ReadOwned()
        {
            List<TaskState> result = new List<TaskState>();
            object collection = null;
            try
            {
                dynamic all = collection = ((dynamic)folder).GetTasks(1);
                for (int i = 1; i <= (int)all.Count; i++)
                {
                    object item = null;
                    try
                    {
                        dynamic task = item = all[i];
                        string name = task.Name, xml = task.Xml;
                        if (name.StartsWith(TaskPrefix, StringComparison.OrdinalIgnoreCase) && TaskTargetsInstall(xml))
                            result.Add(new TaskState { Name = name, Xml = xml, Enabled = task.Enabled });
                    }
                    finally { Release(item); }
                }
            }
            finally { Release(collection); }
            return result;
        }
        public void Register(string name, string xml)
        {
            object registered = null;
            try { registered = ((dynamic)folder).RegisterTask(name, xml, 6, null, null, 3, null); }
            finally { Release(registered); }
        }
        public void Delete(string name)
        {
            try { ((dynamic)folder).DeleteTask(name, 0); }
            catch (FileNotFoundException) { }
            catch (COMException ex) { if (ex.ErrorCode != unchecked((int)0x80070002)) throw; }
        }
        public void Restore(string name, TaskState state)
        {
            if (state == null) { Delete(name); return; }
            Register(name, state.Xml);
            object task = null;
            try { dynamic current = task = ((dynamic)folder).GetTask(name); current.Enabled = state.Enabled; }
            finally { Release(task); }
        }
        public void Dispose() { Release(folder); Release(service); folder = service = null; }
    }

    static void WriteShortcut(string path)
    {
        EnsureNoReparse(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        object shell = null, shortcut = null;
        try
        {
            dynamic host = shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            dynamic link = shortcut = host.CreateShortcut(path);
            link.TargetPath = InstalledExe;
            link.Arguments = "";
            link.WorkingDirectory = InstallRoot;
            link.Description = "Mini Monitor — jam, sensor dan durasi aplikasi";
            link.IconLocation = InstalledExe + ",0";
            link.Save();
        }
        finally { Release(shortcut); Release(shell); }
    }
    static bool ShortcutTargetsInstall(string path)
    {
        if (!File.Exists(path)) return false;
        EnsureNoReparse(path);
        object shell = null, shortcut = null;
        try
        {
            dynamic host = shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            dynamic link = shortcut = host.CreateShortcut(path);
            return String.Equals(Path.GetFullPath((string)link.TargetPath), InstalledExe, StringComparison.OrdinalIgnoreCase);
        }
        finally { Release(shortcut); Release(shell); }
    }
    static void RejectUnrelatedShortcut(string path)
    {
        if (File.Exists(path) && !ShortcutTargetsInstall(path))
            throw new IOException("Shortcut bernama sama sudah digunakan oleh aplikasi lain. Ubah nama atau pindahkan shortcut tersebut sebelum memasang: " + path);
    }
    sealed class FileState
    {
        string path;
        byte[] bytes;
        public static FileState Capture(string path)
        {
            EnsureNoReparse(path);
            return new FileState { path = path, bytes = File.Exists(path) ? File.ReadAllBytes(path) : null };
        }
        public void Restore()
        {
            EnsureNoReparse(path);
            if (bytes == null) File.Delete(path);
            else { Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllBytes(path, bytes); }
        }
    }
    sealed class RegistryValue { public object Value; public RegistryValueKind Kind; }
    sealed class RegistryState
    {
        public Dictionary<string, RegistryValue> Values = new Dictionary<string, RegistryValue>();
        public Dictionary<string, RegistryState> Children = new Dictionary<string, RegistryState>();
        public static RegistryState Read(RegistryKey key)
        {
            if (key == null) return null;
            RegistryState state = new RegistryState();
            foreach (string name in key.GetValueNames()) state.Values[name] = new RegistryValue { Value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames), Kind = key.GetValueKind(name) };
            foreach (string name in key.GetSubKeyNames()) using (RegistryKey child = key.OpenSubKey(name)) state.Children[name] = Read(child);
            return state;
        }
        public void Write(RegistryKey key)
        {
            foreach (KeyValuePair<string, RegistryValue> value in Values) key.SetValue(value.Key, value.Value.Value, value.Value.Kind);
            foreach (KeyValuePair<string, RegistryState> child in Children) using (RegistryKey target = key.CreateSubKey(child.Key)) child.Value.Write(target);
        }
    }
    static RegistryState ReadRegistry()
    {
        using (RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
        using (RegistryKey key = machine.OpenSubKey(RegistryPath)) return RegistryState.Read(key);
    }
    static void DeleteRegistry()
    {
        using (RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)) machine.DeleteSubKeyTree(RegistryPath, false);
    }
    static void RestoreRegistry(RegistryState state)
    {
        DeleteRegistry();
        if (state == null) return;
        using (RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
        using (RegistryKey key = machine.CreateSubKey(RegistryPath)) state.Write(key);
    }
    static void WriteRegistry(IEnumerable<string> names)
    {
        using (RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
        using (RegistryKey key = machine.CreateSubKey(RegistryPath))
        {
            string uninstall = "\"" + Path.Combine(InstallRoot, UninstallerName) + "\" --uninstall";
            key.SetValue("DisplayName", ShortcutName);
            key.SetValue("DisplayVersion", ProductVersion);
            key.SetValue("Publisher", "Mini Monitor");
            key.SetValue("InstallLocation", InstallRoot);
            key.SetValue("DisplayIcon", InstalledExe + ",0");
            key.SetValue("UninstallString", uninstall);
            key.SetValue("InstallDate", DateTime.Today.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture));
            long bytes = names.Sum(delegate(string name) { return new FileInfo(SafePath(InstallRoot, name)).Length; });
            key.SetValue("EstimatedSize", (int)Math.Min(Int32.MaxValue, (bytes + 1023) / 1024), RegistryValueKind.DWord);
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
    }
    static void RequireAdministrator()
    {
        using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) throw new InvalidOperationException("Installer perlu dijalankan sebagai administrator.");
    }
    static void Report(Action<string> callback, string value) { if (callback != null) callback(value); }
    static void Rollback(Action action, List<string> failures) { try { action(); } catch (Exception ex) { failures.Add(ex.GetBaseException().Message); } }
    static void Release(object value) { if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool MoveFileEx(string existing, string replacement, int flags);
}

