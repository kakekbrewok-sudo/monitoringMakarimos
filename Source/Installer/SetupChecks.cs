using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;
using Microsoft.Win32;

// Build with /define:SETUP_TEST /main:SetupChecks. The production setup never
// calls this entry point. No application, driver, or GPU helper is launched.
internal static class SetupChecks
{
    const string TestDirectory = "MiniMonitor-SetupTest";
    const string TestShortcut = "Mini Monitor Setup Test.lnk";
    const string RegistryPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\MiniMonitor-SetupTest";
    static StreamWriter report;
    static int passed;
    static string taskName;

    static void Log(string value)
    {
        Console.WriteLine(value);
        if (report != null) report.WriteLine(value);
    }
    static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + description);
        passed++;
        Log("PASS: " + description);
    }
    static string Hash(Stream stream)
    {
        using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
    }
    static string HashFile(string path)
    {
        using (var stream = File.OpenRead(path)) return Hash(stream);
    }
    static void Release(object value)
    {
        if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }
    sealed class TaskState
    {
        public bool Enabled;
        public string Command, Arguments, WorkingDirectory, Principals, Triggers;
    }
    static TaskState ReadTask()
    {
        object service = null, folder = null, task = null;
        try
        {
            dynamic s = service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")); s.Connect();
            dynamic f = folder = s.GetFolder(@"\");
            dynamic t = task = f.GetTask(taskName);
            var xml = XDocument.Parse((string)t.Xml);
            Func<string, string> text = name => xml.Descendants().Where(e => e.Name.LocalName == name).Select(e => e.Value).FirstOrDefault();
            Func<string, string> section = name => xml.Descendants().Where(e => e.Name.LocalName == name).Select(e => e.ToString(SaveOptions.DisableFormatting)).FirstOrDefault();
            return new TaskState { Enabled = (bool)t.Enabled, Command = text("Command"), Arguments = text("Arguments"),
                WorkingDirectory = text("WorkingDirectory"), Principals = section("Principals"), Triggers = section("Triggers") };
        }
        catch (FileNotFoundException ex)
        {
            // The dynamic COM binder maps HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND)
            // to FileNotFoundException rather than COMException on Windows.
            if (ex.HResult == unchecked((int)0x80070002)) return null;
            throw;
        }
        catch (COMException ex)
        {
            if (ex.ErrorCode == unchecked((int)0x80070002) || ex.ErrorCode == unchecked((int)0x8004130F)) return null;
            throw;
        }
        finally { Release(task); Release(folder); Release(service); }
    }
    static void DisableTestTaskIfPresent()
    {
        object service = null, folder = null, task = null;
        try
        {
            dynamic s = service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")); s.Connect();
            dynamic f = folder = s.GetFolder(@"\"); dynamic t = task = f.GetTask(taskName); t.Enabled = false;
        }
        catch (FileNotFoundException ex)
        {
            if (ex.HResult != unchecked((int)0x80070002)) throw;
        }
        catch (COMException) { }
        finally { Release(task); Release(folder); Release(service); }
    }
    static Dictionary<string, string> PayloadHashes(string zipPath)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            foreach (var entry in zip.Entries)
            {
                if (String.IsNullOrEmpty(entry.Name)) continue;
                string relative = entry.FullName.Replace('/', '\\');
                if (relative.StartsWith("MiniMonitor\\", StringComparison.OrdinalIgnoreCase)) relative = relative.Substring(12);
                if (Path.IsPathRooted(relative) || relative.Split('\\').Any(part => part == "..")) throw new InvalidDataException("Unsafe payload test path");
                using (var stream = entry.Open()) files.Add(relative, Hash(stream));
            }
        }
        if (!files.ContainsKey("MiniMonitor.exe")) throw new InvalidDataException("Payload does not contain MiniMonitor.exe");
        return files;
    }
    static void CheckPayload(string installRoot, Dictionary<string, string> files, string description)
    {
        var mismatches = new List<string>();
        foreach (var pair in files)
        {
            string path = Path.Combine(installRoot, pair.Key);
            if (!File.Exists(path) || HashFile(path) != pair.Value) mismatches.Add(pair.Key);
        }
        Check(mismatches.Count == 0, description + (mismatches.Count == 0 ? " (" + files.Count + " files)" : ": " + String.Join(", ", mismatches)));
    }
    static bool RegistryExists()
    {
        using (var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
        using (var key = root.OpenSubKey(RegistryPath)) return key != null;
    }
    static string RegistryValue(string name)
    {
        using (var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
        using (var key = root.OpenSubKey(RegistryPath)) return key == null ? null : Convert.ToString(key.GetValue(name));
    }
    static Dictionary<string, string> DirectoryHashes(string root)
    {
        return Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(
            path => path.Substring(root.TrimEnd('\\').Length + 1), HashFile, StringComparer.OrdinalIgnoreCase);
    }
    static bool SameHashes(Dictionary<string, string> expected, Dictionary<string, string> actual)
    {
        return expected.Count == actual.Count && expected.All(pair => actual.ContainsKey(pair.Key) && actual[pair.Key] == pair.Value);
    }
    static bool SameTask(TaskState expected, TaskState actual)
    {
        return expected != null && actual != null && expected.Enabled == actual.Enabled && expected.Command == actual.Command &&
            expected.Arguments == actual.Arguments && expected.WorkingDirectory == actual.WorkingDirectory &&
            expected.Principals == actual.Principals && expected.Triggers == actual.Triggers;
    }
    static string RegistryFingerprint()
    {
        var result = new StringBuilder();
        using (var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
        using (var key = root.OpenSubKey(RegistryPath)) AppendRegistry(key, "", result);
        return result.ToString();
    }
    static void AppendRegistry(RegistryKey key, string prefix, StringBuilder result)
    {
        if (key == null) { result.Append("<missing>"); return; }
        foreach (string name in key.GetValueNames().OrderBy(value => value, StringComparer.Ordinal))
        {
            object value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            string text = value is byte[] ? Convert.ToBase64String((byte[])value) :
                value is string[] ? String.Join("\0", (string[])value) : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
            result.Append(prefix).Append(name).Append(':').Append(key.GetValueKind(name)).Append('=').Append(text).Append('\n');
        }
        foreach (string name in key.GetSubKeyNames().OrderBy(value => value, StringComparer.Ordinal))
            using (var child = key.OpenSubKey(name)) AppendRegistry(child, prefix + name + "\\", result);
    }
    static void RejectArchiveBeforeInstall(string archivePath, string entryName, string expectedMessage,
        string installRoot, string startShortcut, string desktopShortcut, string appSettings, string settingsHash)
    {
        using (var stream = File.Create(archivePath))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry(entryName).Open())) writer.Write("Deliberately invalid installer test payload.");
        bool rejected = false, reachedMutationPhase = false;
        try
        {
            SetupEngine.Install(archivePath, false, true, message => {
                Log("REJECT: " + message);
                if (message.StartsWith("Menunggu ", StringComparison.Ordinal) || message.StartsWith("Memasang ", StringComparison.Ordinal)) reachedMutationPhase = true;
            });
        }
        catch (InvalidDataException ex) { rejected = ex.Message.IndexOf(expectedMessage, StringComparison.OrdinalIgnoreCase) >= 0; }
        Check(rejected, "Invalid archive rejected for expected reason: " + Path.GetFileName(archivePath));
        Check(!reachedMutationPhase && !Directory.Exists(installRoot) && !RegistryExists() && ReadTask() == null &&
            !File.Exists(startShortcut) && !File.Exists(desktopShortcut) && HashFile(appSettings) == settingsHash,
            "Rejected archive leaves install location, registration, task, shortcuts and settings untouched");
    }
    static void CheckShortcut(string path, string expectedTarget)
    {
        Check(File.Exists(path), "Shortcut exists: " + Path.GetFileName(path));
        object shell = null, shortcut = null;
        try
        {
            dynamic s = shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            dynamic link = shortcut = s.CreateShortcut(path);
            Check(String.Equals((string)link.TargetPath, expectedTarget, StringComparison.OrdinalIgnoreCase), "Shortcut targets isolated test executable: " + Path.GetFileName(path));
        }
        finally { Release(shortcut); Release(shell); }
    }

    [STAThread]
    public static int Main(string[] args)
    {
#if !SETUP_TEST
        Console.Error.WriteLine("Refusing to run installer checks without SETUP_TEST.");
        return 2;
#else
        if (args.Length != 2) { Console.Error.WriteLine("Usage: SetupChecks.exe payload.zip report-directory"); return 2; }
        string zipPath = Path.GetFullPath(args[0]);
        string reportDirectory = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(reportDirectory);
        report = new StreamWriter(Path.Combine(reportDirectory, "setup-integration-checks.txt"), false, new UTF8Encoding(false));
        report.AutoFlush = true;
        string installRoot = Path.GetFullPath(SetupEngine.InstallRoot).TrimEnd('\\');
        string expectedRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), TestDirectory);
        string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), TestDirectory);
        string appSettings = Path.Combine(appData, "MiniMonitor.settings");
        string startShortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), TestShortcut);
        string desktopShortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), TestShortcut);
        bool attemptedInstall = false, createdAppData = false, completed = false;
        try
        {
            using (var identity = WindowsIdentity.GetCurrent())
            {
                taskName = "MiniMonitor-SetupTest-" + identity.User.Value;
                Check(new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator), "Elevated test process available");
            }
            Check(String.Equals(installRoot, expectedRoot, StringComparison.OrdinalIgnoreCase), "SETUP_TEST installation path is isolated from production");
            Check(SetupEngine.TaskName == taskName && SetupEngine.RegistryPath.Equals(RegistryPath, StringComparison.OrdinalIgnoreCase) &&
                SetupEngine.StartMenuShortcut.Equals(startShortcut, StringComparison.OrdinalIgnoreCase) &&
                SetupEngine.DesktopShortcut.Equals(desktopShortcut, StringComparison.OrdinalIgnoreCase),
                "SETUP_TEST task, registry and shortcut paths are isolated from production");
            Check(!Path.GetFullPath(Assembly.GetExecutingAssembly().Location).StartsWith(installRoot + "\\", StringComparison.OrdinalIgnoreCase), "Tests run outside the installation directory");
            Check(!Directory.Exists(installRoot) && !RegistryExists() && ReadTask() == null, "Clean isolated install location, registration and task");
            Check(!File.Exists(startShortcut) && !File.Exists(desktopShortcut), "No pre-existing test shortcuts");
            Check(!Directory.Exists(appData), "No pre-existing test AppData directory will be overwritten");
            var payload = PayloadHashes(zipPath);
            Directory.CreateDirectory(appData); createdAppData = true;
            string settings = "theme=Kanji\r\noverlay=0\r\nwindowWidth=1080\r\nwindowHeight=300\r\nclockSound=0\r\nfpsEnabled=1\r\n";
            File.WriteAllText(appSettings, settings, new UTF8Encoding(false));
            string settingsHash = HashFile(appSettings);
            attemptedInstall = true;
            RejectArchiveBeforeInstall(Path.Combine(reportDirectory, "incomplete-payload.zip"), "PANDUAN.txt", "Berkas wajib",
                installRoot, startShortcut, desktopShortcut, appSettings, settingsHash);
            RejectArchiveBeforeInstall(Path.Combine(reportDirectory, "traversal-payload.zip"), "../escape.txt", "Jalur arsip",
                installRoot, startShortcut, desktopShortcut, appSettings, settingsHash);

            SetupEngine.Install(zipPath, false, true, message => Log("INSTALL: " + message));
            Check(SetupEngine.IsInstalled(), "Fresh installation detected");
            CheckPayload(installRoot, payload, "Fresh install matches every payload SHA-256");
            var initialTask = ReadTask();
            Check(initialTask != null && !initialTask.Enabled && !SetupEngine.GetStartupEnabled(), "Startup unchecked creates a disabled task");
            Check(initialTask.Arguments == "--startup", "Startup respects saved window/overlay mode");
            Check(String.Equals(initialTask.Command, Path.Combine(installRoot, "MiniMonitor.exe"), StringComparison.OrdinalIgnoreCase) &&
                String.Equals(initialTask.WorkingDirectory.TrimEnd('\\'), installRoot, StringComparison.OrdinalIgnoreCase), "Task targets only the isolated installed program");
            Check(RegistryExists(), "Uninstall registration exists in HKLM 64-bit");
            Check(RegistryValue("DisplayVersion") == SetupEngine.ProductVersion && SetupEngine.GetInstalledVersion() == SetupEngine.ProductVersion, "Installed version and uninstall registration match payload release");
            Check(String.Equals((RegistryValue("InstallLocation") ?? "").TrimEnd('\\'), installRoot, StringComparison.OrdinalIgnoreCase), "Uninstall registration points to isolated installation");
            string uninstallCommand = RegistryValue("UninstallString") ?? "";
            Check(uninstallCommand.IndexOf(installRoot, StringComparison.OrdinalIgnoreCase) >= 0 && uninstallCommand.IndexOf("--uninstall", StringComparison.OrdinalIgnoreCase) >= 0, "Uninstall command uses installed uninstaller");
            Check(File.Exists(Path.Combine(installRoot, "installed-files.txt")), "Installed file manifest exists");
            CheckShortcut(startShortcut, Path.Combine(installRoot, "MiniMonitor.exe"));
            CheckShortcut(desktopShortcut, Path.Combine(installRoot, "MiniMonitor.exe"));
            Check(HashFile(appSettings) == settingsHash, "Fresh installation preserves user settings");

            string unknownFile = Path.Combine(installRoot, "user-note.keep.txt");
            string legacySettings = Path.Combine(installRoot, "MiniMonitor.settings");
            File.WriteAllText(unknownFile, "User-owned file: retain on repair and uninstall.", new UTF8Encoding(false));
            File.WriteAllText(legacySettings, settings, new UTF8Encoding(false));
            string unknownHash = HashFile(unknownFile), legacyHash = HashFile(legacySettings);
            string repairFile = payload.Keys.FirstOrDefault(p => p.Equals("PANDUAN.txt", StringComparison.OrdinalIgnoreCase)) ?? payload.Keys.First(p => !p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
            File.WriteAllText(Path.Combine(installRoot, repairFile), "Simulated interrupted update: repair this file.");
            Check(HashFile(Path.Combine(installRoot, repairFile)) != payload[repairFile], "Repair fixture contains a damaged installed file");
            SetupEngine.Install(zipPath, false, true, message => Log("REPAIR: " + message));
            CheckPayload(installRoot, payload, "Repair restores exact payload hashes");
            var repairedTask = ReadTask();
            Check(repairedTask != null && !repairedTask.Enabled && !SetupEngine.GetStartupEnabled(), "Repair preserves disabled startup selection");
            Check(repairedTask.Principals == initialTask.Principals && repairedTask.Triggers == initialTask.Triggers, "Repair preserves task principal and login trigger");
            Check(HashFile(appSettings) == settingsHash && HashFile(legacySettings) == legacyHash, "Repair preserves preset theme, window mode and user settings");
            Check(HashFile(unknownFile) == unknownHash, "Repair preserves unknown user file");

            // Deliberately make the old installation different from the payload.
            // The failure point runs after files, task, shortcuts and registration
            // were changed, so these checks exercise real transaction rollback.
            File.WriteAllText(Path.Combine(installRoot, repairFile), "Rollback must restore these exact pre-update bytes.");
            using (var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var key = machine.OpenSubKey(RegistryPath, true))
            {
                key.SetValue("DisplayVersion", "3.5.1-rollback-fixture", RegistryValueKind.String);
                key.SetValue("SetupChecksSentinel", 1789, RegistryValueKind.DWord);
                using (var child = key.CreateSubKey("SetupChecks")) child.SetValue("Retain", "User metadata");
            }
            var beforeRollbackFiles = DirectoryHashes(installRoot);
            var beforeRollbackTask = ReadTask();
            string beforeRollbackRegistry = RegistryFingerprint();
            string beforeStartShortcut = HashFile(startShortcut), beforeDesktopShortcut = HashFile(desktopShortcut);
            bool injectedFailureReached = false;
            SetupEngine.TestFailurePoint = "after-configuration";
            try
            {
                // These choices also change the disabled task and remove the
                // desktop shortcut, both of which rollback must restore.
                SetupEngine.Install(zipPath, true, false, message => Log("ROLLBACK: " + message));
            }
            catch (IOException ex) { injectedFailureReached = ex.Message.IndexOf("Simulasi kegagalan", StringComparison.Ordinal) >= 0; }
            finally { SetupEngine.TestFailurePoint = null; }
            Check(injectedFailureReached, "Controlled failure occurs after all installation configuration writes");
            Check(SameHashes(beforeRollbackFiles, DirectoryHashes(installRoot)), "Rollback restores all prior file bytes and the original file set");
            Check(SameTask(beforeRollbackTask, ReadTask()), "Rollback restores disabled startup, task action, principal and triggers");
            Check(RegistryFingerprint() == beforeRollbackRegistry, "Rollback restores registry values, value types and nested metadata");
            Check(File.Exists(startShortcut) && File.Exists(desktopShortcut) && HashFile(startShortcut) == beforeStartShortcut &&
                HashFile(desktopShortcut) == beforeDesktopShortcut, "Rollback restores exact Start menu and desktop shortcut bytes");
            Check(HashFile(appSettings) == settingsHash && HashFile(legacySettings) == legacyHash && HashFile(unknownFile) == unknownHash,
                "Rollback leaves settings and unknown user files unchanged");

            SetupEngine.Uninstall(message => Log("UNINSTALL: " + message));
            Check(ReadTask() == null, "Uninstall removes only the isolated startup task");
            Check(!RegistryExists() && !SetupEngine.IsInstalled(), "Uninstall removes Installed Apps registration");
            Check(payload.Keys.All(p => !File.Exists(Path.Combine(installRoot, p))), "Uninstall removes every known payload file");
            Check(!File.Exists(Path.Combine(installRoot, "installed-files.txt")), "Uninstall removes installed file manifest");
            Check(!File.Exists(startShortcut) && !File.Exists(desktopShortcut), "Uninstall removes Start menu and desktop shortcuts");
            Check(File.Exists(unknownFile) && HashFile(unknownFile) == unknownHash, "Uninstall preserves unknown user file");
            Check(File.Exists(legacySettings) && HashFile(legacySettings) == legacyHash, "Uninstall preserves user-owned legacy settings");
            Check(File.Exists(appSettings) && HashFile(appSettings) == settingsHash, "Uninstall preserves AppData settings");
            File.Delete(unknownFile); File.Delete(legacySettings);
            Check(!Directory.EnumerateFileSystemEntries(installRoot).Any(), "No registered setup or application files remain after uninstall");
            Directory.Delete(installRoot);
            completed = true;
            Log("TOTAL: " + passed + " integration checks passed. Production Mini Monitor, PawnIO and GPU were not accessed.");
            return 0;
        }
        catch (Exception ex)
        {
            Log(ex.ToString());
            return 1;
        }
        finally
        {
            if (attemptedInstall && !completed)
            {
                try { DisableTestTaskIfPresent(); SetupEngine.Uninstall(message => Log("CLEANUP: " + message)); }
                catch (Exception ex) { Log("Cleanup incomplete: " + ex.Message); }
            }
            if (createdAppData)
            {
                try { File.Delete(appSettings); if (!Directory.EnumerateFileSystemEntries(appData).Any()) Directory.Delete(appData); }
                catch (Exception ex) { Log("Test AppData cleanup incomplete: " + ex.Message); }
            }
            report.Dispose(); report = null;
        }
#endif
    }
}
