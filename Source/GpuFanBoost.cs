using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Xml;

// One short-lived helper owns a burst. It survives UI exit long enough to put
// the fans back; the UI can recover this journal if the helper itself fails.
public sealed class FanBoostState
{
    public string Status { get; internal set; }
    public string Message { get; internal set; }
    public DateTime StartedUtc { get; internal set; }
    internal string GpuName, Uuid;
    internal readonly List<FanBoostSnapshot> Fans = new List<FanBoostSnapshot>();
    internal readonly List<string> Log = new List<string>();
}

internal sealed class FanBoostSnapshot
{
    internal int Index;
    internal uint Policy, Target;
    internal bool Intent;
}

internal struct FanBoostReading
{
    internal uint Policy, Target;
    internal FanBoostReading(uint policy, uint target) { Policy = policy; Target = target; }
}

internal interface IFanBoostDevice : IDisposable
{
    string Name { get; }
    string Uuid { get; }
    int FanCount { get; }
    uint Minimum { get; }
    uint Maximum { get; }
    FanBoostReading Read(int fan);
    void SetSpeed(int fan, uint percent);
    void SetPolicy(int fan, uint policy);
}

internal interface IFanBoostClock
{
    DateTime UtcNow { get; }
    double Seconds { get; }
    void Sleep(int milliseconds);
}

internal interface IFanBoostStop
{
    string Reason { get; }
}

internal interface IFanBoostStore
{
    void Write(FanBoostState state);
}

public static class GpuFanBoost
{
    public const int DurationSeconds = 15;
    internal const uint AutomaticPolicy = 0, ManualPolicy = 1;

    public static FanBoostState ReadState(string path)
    {
        try { return FanBoostFileStore.Read(path); }
        catch { return null; }
    }

    public static string Probe(string gpuName)
    {
        try
        {
            using (var device = new NvmlFanBoostDevice(gpuName, null))
            {
                FanBoostState state = Snapshot(device, DateTime.UtcNow);
                return "Siap: " + device.Name + ", " + state.Fans.Count + " kipas; 100% selama 15 detik.";
            }
        }
        catch (Exception ex) { return "Kontrol kipas tidak tersedia: " + ex.Message; }
    }

    public static int RunHelper(string gpuName, int parentPid, string statePath, string cancelEventName)
    {
        var store = new FanBoostFileStore(statePath);
        try
        {
            using (var lease = new FanBoostLease())
            using (var stop = new FanBoostProcessStop(parentPid, cancelEventName))
            using (var device = new NvmlFanBoostDevice(gpuName, null))
                return Execute(device, new FanBoostClock(), stop, store);
        }
        catch (Exception ex)
        {
            // Execute contains its own rollback. This is the preflight path,
            // before a device is available and before any write can take place.
            try
            {
                store.Write(new FanBoostState { Status = "Failed", Message = "Kipas belum diubah: " + ex.Message,
                    StartedUtc = DateTime.UtcNow, GpuName = gpuName ?? "", Uuid = "" });
            }
            catch { }
            return 1;
        }
    }

    // Called only for this UI's just-failed helper, never as startup recovery.
    public static string Recover(string statePath)
    {
        FanBoostState state = ReadState(statePath);
        if (state == null) return "Catatan pemulihan kipas tidak dapat dibaca.";
        if (!HasIntent(state)) return state.Message;
        TimeSpan age = DateTime.UtcNow - state.StartedUtc;
        if (age < TimeSpan.FromSeconds(-5) || age > TimeSpan.FromMinutes(2))
            return "Catatan kipas sudah kedaluwarsa; pengaturan kipas saat ini tidak diubah.";
        try
        {
            using (var lease = new FanBoostLease())
            using (var device = new NvmlFanBoostDevice(state.GpuName, state.Uuid))
            {
                var store = new FanBoostFileStore(statePath);
                string problem = Restore(device, state, store);
                state.Status = problem == null ? "Cancelled" : "Failed";
                state.Message = problem == null ? "Boost berhenti; pengaturan kipas sudah dipulihkan." : problem;
                store.Write(state);
                return state.Message;
            }
        }
        catch (Exception ex) { return "Pemulihan kipas belum berhasil: " + ex.Message; }
    }

    internal static FanBoostState Snapshot(IFanBoostDevice device, DateTime now)
    {
        if (device.FanCount < 1 || device.FanCount > 16 || device.Minimum > 100 || device.Maximum < 100)
            throw new InvalidOperationException("GPU tidak mendukung target kipas 100%." );
        if (String.IsNullOrEmpty(device.Uuid) || !device.Uuid.StartsWith("GPU-", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Identitas GPU tidak dapat dipastikan.");
        var state = new FanBoostState { Status = "Starting", Message = "Menyiapkan kipas...", StartedUtc = now,
            GpuName = device.Name, Uuid = device.Uuid };
        // Read every fan first, so an unsupported query cannot leave some fans changed.
        for (int i = 0; i < device.FanCount; i++)
        {
            FanBoostReading value = device.Read(i);
            if (value.Policy != AutomaticPolicy && value.Policy != ManualPolicy)
                throw new InvalidOperationException("Mode kipas " + (i + 1) + " tidak didukung.");
            if (value.Target > 100 || (value.Policy == ManualPolicy && value.Target < device.Minimum))
                throw new InvalidOperationException("Target awal kipas " + (i + 1) + " tidak dapat dipulihkan dengan pasti.");
            state.Fans.Add(new FanBoostSnapshot { Index = i, Policy = value.Policy, Target = value.Target });
            AddLog(state, "snapshot fan=" + i + " policy=" + value.Policy + " target=" + value.Target);
        }
        return state;
    }

    internal static int Execute(IFanBoostDevice device, IFanBoostClock clock, IFanBoostStop stop, IFanBoostStore store)
    {
        FanBoostState state = null;
        bool cancelled = false, failed = false;
        string message = null;
        try
        {
            state = Snapshot(device, clock.UtcNow);
            store.Write(state);
            string reason = stop.Reason;
            if (reason != null) throw new FanBoostCancelledException(reason);
            double started = clock.Seconds;
            state.StartedUtc = clock.UtcNow;
            foreach (FanBoostSnapshot fan in state.Fans)
            {
                reason = stop.Reason;
                if (reason != null) throw new FanBoostCancelledException(reason);
                // Persist intent BEFORE the native setter. Even a setter that
                // changes hardware then fails remains eligible for rollback.
                fan.Intent = true;
                store.Write(state);
                device.SetSpeed(fan.Index, 100);
                FanBoostReading value = device.Read(fan.Index);
                AddLog(state, "applied fan=" + fan.Index + " policy=" + value.Policy + " target=" + value.Target);
                if (!Owned(value)) throw new InvalidOperationException("Target kipas 100% tidak terkonfirmasi pada kipas " + (fan.Index + 1) + ".");
            }
            state.Status = "Running";
            state.Message = "Kipas GPU 100% / 15 detik";
            store.Write(state);
            while (clock.Seconds - started < DurationSeconds)
            {
                reason = stop.Reason;
                if (reason != null) throw new FanBoostCancelledException(reason);
                foreach (FanBoostSnapshot fan in state.Fans)
                {
                    FanBoostReading value = device.Read(fan.Index);
                    if (!Owned(value))
                    {
                        // A controller such as FireStorm took ownership. Never
                        // repeatedly force 100% against its user or fan curve.
                        fan.Intent = false;
                        AddLog(state, "external fan=" + fan.Index + " policy=" + value.Policy + " target=" + value.Target);
                        store.Write(state);
                        throw new FanBoostCancelledException("Boost berhenti: pengaturan kipas diubah aplikasi lain.");
                    }
                }
                clock.Sleep((int)Math.Max(1, Math.Min(200, Math.Ceiling((DurationSeconds - (clock.Seconds - started)) * 1000))));
            }
            message = "Selesai 15 detik; pengaturan kipas dipulihkan.";
        }
        catch (FanBoostCancelledException ex) { cancelled = true; message = ex.Message; }
        catch (Exception ex) { failed = true; message = "Boost kipas gagal: " + ex.Message; }
        finally
        {
            if (state == null)
                state = new FanBoostState { StartedUtc = clock.UtcNow, GpuName = device.Name, Uuid = device.Uuid };
            string restoreProblem = Restore(device, state, store);
            if (restoreProblem != null) { failed = true; message = restoreProblem; }
            state.Status = failed ? "Failed" : cancelled ? "Cancelled" : "Complete";
            state.Message = message ?? "Boost berhenti; pengaturan kipas dipulihkan.";
            try { store.Write(state); }
            catch { failed = true; }
        }
        return failed ? 1 : 0;
    }

    internal static string Restore(IFanBoostDevice device, FanBoostState state, IFanBoostStore store)
    {
        var problems = new List<string>();
        if (!HasIntent(state)) return null;
        if (!String.Equals(device.Uuid, state.Uuid, StringComparison.OrdinalIgnoreCase) || device.FanCount != state.Fans.Count)
            return "Pemulihan kipas dibatalkan: identitas atau jumlah kipas GPU berubah.";
        foreach (FanBoostSnapshot fan in state.Fans)
        {
            if (!fan.Intent) continue;
            try
            {
                FanBoostReading current = device.Read(fan.Index);
                if (MatchesOriginal(current, fan))
                {
                    fan.Intent = false;
                    AddLog(state, "already-restored fan=" + fan.Index + " policy=" + current.Policy + " target=" + current.Target);
                }
                else if (!Owned(current))
                {
                    fan.Intent = false;
                    AddLog(state, "leave-external fan=" + fan.Index + " policy=" + current.Policy + " target=" + current.Target);
                }
                else
                {
                    if (fan.Policy == ManualPolicy) device.SetSpeed(fan.Index, fan.Target);
                    else device.SetPolicy(fan.Index, fan.Policy);
                    current = device.Read(fan.Index);
                    AddLog(state, "restore fan=" + fan.Index + " policy=" + current.Policy + " target=" + current.Target);
                    if (!MatchesOriginal(current, fan))
                        throw new InvalidOperationException("mode/target awal belum terkonfirmasi");
                    fan.Intent = false;
                }
                store.Write(state);
            }
            catch (Exception ex)
            {
                // Keep intent so the parent can make one fresh recovery attempt.
                problems.Add("kipas " + (fan.Index + 1) + ": " + ex.Message);
                AddLog(state, "restore-error fan=" + fan.Index + " " + ex.Message);
            }
        }
        return problems.Count == 0 ? null : "Pemulihan kipas belum berhasil (" + String.Join("; ", problems.ToArray()) + ").";
    }

    internal static bool HasIntent(FanBoostState state)
    {
        foreach (FanBoostSnapshot fan in state.Fans) if (fan.Intent) return true;
        return false;
    }
    static bool Owned(FanBoostReading current) { return current.Policy == ManualPolicy && current.Target == 100; }
    static bool MatchesOriginal(FanBoostReading current, FanBoostSnapshot fan)
    {
        return current.Policy == fan.Policy && (fan.Policy == AutomaticPolicy || current.Target == fan.Target);
    }
    static void AddLog(FanBoostState state, string line)
    {
        if (state.Log.Count >= 64) state.Log.RemoveAt(0);
        state.Log.Add(line.Length <= 400 ? line : line.Substring(0, 400));
    }
    sealed class FanBoostCancelledException : Exception { internal FanBoostCancelledException(string message) : base(message) { } }
}

internal sealed class FanBoostLease : IDisposable
{
    Mutex mutex;
    bool owned;
    internal FanBoostLease()
    {
        string sid;
        using (WindowsIdentity identity = WindowsIdentity.GetCurrent()) sid = identity.User.Value;
        mutex = new Mutex(false, "Local\\MiniMonitor-GpuFanBoost-" + sid);
        try
        {
            try { owned = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { owned = true; }
            if (!owned) throw new InvalidOperationException("Boost kipas lain masih berjalan.");
        }
        catch { mutex.Dispose(); mutex = null; throw; }
    }
    public void Dispose()
    {
        if (mutex == null) return;
        if (owned) { owned = false; mutex.ReleaseMutex(); }
        mutex.Dispose(); mutex = null;
    }
}

internal sealed class FanBoostClock : IFanBoostClock
{
    readonly Stopwatch watch = Stopwatch.StartNew();
    public DateTime UtcNow { get { return DateTime.UtcNow; } }
    public double Seconds { get { return watch.Elapsed.TotalSeconds; } }
    public void Sleep(int milliseconds) { Thread.Sleep(milliseconds); }
}

internal sealed class FanBoostProcessStop : IFanBoostStop, IDisposable
{
    readonly Process parent;
    readonly EventWaitHandle cancel;
    internal FanBoostProcessStop(int parentPid, string cancelEventName)
    {
        if (parentPid <= 0 || parentPid == Process.GetCurrentProcess().Id)
            throw new InvalidOperationException("Proses Mini Monitor tidak valid.");
        if (String.IsNullOrEmpty(cancelEventName) || !cancelEventName.StartsWith("Local\\", StringComparison.Ordinal))
            throw new InvalidOperationException("Sinyal pembatalan kipas tidak valid.");
        try
        {
            parent = Process.GetProcessById(parentPid);
            cancel = EventWaitHandle.OpenExisting(cancelEventName);
        }
        catch { if (parent != null) parent.Dispose(); throw; }
    }
    public string Reason
    {
        get
        {
            try
            {
                if (cancel.WaitOne(0)) return "Boost dibatalkan; pengaturan kipas dipulihkan.";
                if (parent.HasExited) return "Mini Monitor ditutup; pengaturan kipas dipulihkan.";
                return null;
            }
            catch { return "Pemantauan berhenti; pengaturan kipas dipulihkan."; }
        }
    }
    public void Dispose() { if (cancel != null) cancel.Dispose(); if (parent != null) parent.Dispose(); }
}

internal sealed class FanBoostFileStore : IFanBoostStore
{
    readonly string path;
    internal FanBoostFileStore(string path) { this.path = Path.GetFullPath(path); }
    public void Write(FanBoostState state)
    {
        string directory = Path.GetDirectoryName(path);
        Directory.CreateDirectory(directory);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false };
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                using (XmlWriter writer = XmlWriter.Create(stream, options))
                {
                    writer.WriteStartElement("fanBoost");
                    writer.WriteAttributeString("version", "1");
                    writer.WriteAttributeString("status", state.Status ?? "Starting");
                    writer.WriteAttributeString("startedUtc", state.StartedUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
                    writer.WriteAttributeString("gpu", state.GpuName ?? "");
                    writer.WriteAttributeString("uuid", state.Uuid ?? "");
                    writer.WriteElementString("message", state.Message ?? "");
                    foreach (FanBoostSnapshot fan in state.Fans)
                    {
                        writer.WriteStartElement("fan");
                        writer.WriteAttributeString("index", fan.Index.ToString(CultureInfo.InvariantCulture));
                        writer.WriteAttributeString("policy", fan.Policy.ToString(CultureInfo.InvariantCulture));
                        writer.WriteAttributeString("target", fan.Target.ToString(CultureInfo.InvariantCulture));
                        writer.WriteAttributeString("intent", fan.Intent ? "1" : "0");
                        writer.WriteEndElement();
                    }
                    foreach (string line in state.Log) writer.WriteElementString("log", line);
                    writer.WriteEndElement();
                }
                stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
    }

    internal static FanBoostState Read(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length > 65536) return null;
        var xml = new XmlDocument { XmlResolver = null };
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 65536 };
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (XmlReader reader = XmlReader.Create(stream, settings)) xml.Load(reader);
        XmlElement root = xml.DocumentElement;
        if (root == null || root.Name != "fanBoost" || root.GetAttribute("version") != "1") return null;
        string status = root.GetAttribute("status");
        if (status != "Starting" && status != "Running" && status != "Complete" && status != "Cancelled" && status != "Failed") return null;
        DateTime started;
        if (!DateTime.TryParse(root.GetAttribute("startedUtc"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out started)) return null;
        var state = new FanBoostState { Status = status, StartedUtc = started.ToUniversalTime(), GpuName = root.GetAttribute("gpu"), Uuid = root.GetAttribute("uuid") };
        if (state.GpuName.Length > 256 || state.Uuid.Length > 128) return null;
        XmlNode message = root.SelectSingleNode("message");
        state.Message = message == null ? "" : message.InnerText;
        if (state.Message.Length > 4096) return null;
        foreach (XmlElement node in root.SelectNodes("fan"))
        {
            int index; uint policy, target;
            if (!Int32.TryParse(node.GetAttribute("index"), out index) || index != state.Fans.Count || index >= 16 ||
                !UInt32.TryParse(node.GetAttribute("policy"), out policy) || policy > 1 ||
                !UInt32.TryParse(node.GetAttribute("target"), out target) || target > 100 ||
                (node.GetAttribute("intent") != "0" && node.GetAttribute("intent") != "1")) return null;
            state.Fans.Add(new FanBoostSnapshot { Index = index, Policy = policy, Target = target, Intent = node.GetAttribute("intent") == "1" });
        }
        foreach (XmlElement node in root.SelectNodes("log"))
        {
            if (state.Log.Count >= 64 || node.InnerText.Length > 400) return null;
            state.Log.Add(node.InnerText);
        }
        if (GpuFanBoost.HasIntent(state) && !state.Uuid.StartsWith("GPU-", StringComparison.OrdinalIgnoreCase)) return null;
        return state;
    }
}

// NVML ships with the display driver. Load only the OS system-directory copy;
// never load a DLL from the current directory or from an arbitrary PATH entry.
internal sealed class NvmlFanBoostDevice : IFanBoostDevice
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr LoadLibraryEx(string name, IntPtr file, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("kernel32.dll")]
    static extern bool FreeLibrary(IntPtr module);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int SimpleCall();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int CountCall(out uint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int HandleCall(uint index, out IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int StringCall(IntPtr device, StringBuilder value, uint capacity);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int DeviceUintCall(IntPtr device, out uint value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int RangeCall(IntPtr device, out uint minimum, out uint maximum);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int FanGetCall(IntPtr device, uint fan, out uint value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int FanSetCall(IntPtr device, uint fan, uint value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr ErrorCall(int result);

    IntPtr library, handle;
    bool initialized;
    SimpleCall shutdown;
    FanGetCall getPolicy, getTarget;
    FanSetCall setSpeed, setPolicy;
    ErrorCall errorString;
    public string Name { get; private set; }
    public string Uuid { get; private set; }
    public int FanCount { get; private set; }
    public uint Minimum { get; private set; }
    public uint Maximum { get; private set; }

    internal NvmlFanBoostDevice(string gpuName, string expectedUuid)
    {
        if (String.IsNullOrWhiteSpace(gpuName)) throw new InvalidOperationException("GPU belum terdeteksi.");
        try
        {
            library = LoadLibraryEx(Path.Combine(Environment.SystemDirectory, "nvml.dll"), IntPtr.Zero, 0x00000800);
            if (library == IntPtr.Zero) throw new InvalidOperationException("Driver NVML NVIDIA tidak tersedia (" + Marshal.GetLastWin32Error() + ").");
            SimpleCall init = Load<SimpleCall>("nvmlInit_v2");
            shutdown = Load<SimpleCall>("nvmlShutdown");
            errorString = Load<ErrorCall>("nvmlErrorString");
            CountCall getCount = Load<CountCall>("nvmlDeviceGetCount_v2");
            HandleCall getHandle = Load<HandleCall>("nvmlDeviceGetHandleByIndex_v2");
            StringCall getName = Load<StringCall>("nvmlDeviceGetName");
            StringCall getUuid = Load<StringCall>("nvmlDeviceGetUUID");
            DeviceUintCall getFans = Load<DeviceUintCall>("nvmlDeviceGetNumFans");
            RangeCall getRange = Load<RangeCall>("nvmlDeviceGetMinMaxFanSpeed");
            getPolicy = Load<FanGetCall>("nvmlDeviceGetFanControlPolicy_v2");
            getTarget = Load<FanGetCall>("nvmlDeviceGetTargetFanSpeed");
            setSpeed = Load<FanSetCall>("nvmlDeviceSetFanSpeed_v2");
            setPolicy = Load<FanSetCall>("nvmlDeviceSetFanControlPolicy");
            Check(init(), "inisialisasi NVML"); initialized = true;
            uint count; Check(getCount(out count), "jumlah GPU");
            if (count > 64) throw new InvalidOperationException("Jumlah GPU tidak valid.");
            int matches = 0;
            for (uint i = 0; i < count; i++)
            {
                IntPtr candidate; Check(getHandle(i, out candidate), "identitas GPU");
                var name = new StringBuilder(256); Check(getName(candidate, name, 256), "nama GPU");
                var uuid = new StringBuilder(128); Check(getUuid(candidate, uuid, 128), "UUID GPU");
                bool match = expectedUuid == null ? NormalizeName(name.ToString()) == NormalizeName(gpuName) :
                    String.Equals(expectedUuid, uuid.ToString(), StringComparison.OrdinalIgnoreCase);
                if (!match) continue;
                matches++; handle = candidate; Name = name.ToString(); Uuid = uuid.ToString();
            }
            if (matches != 1) throw new InvalidOperationException(matches == 0 ? "GPU yang dipantau tidak ditemukan di NVML." : "GPU bernama sama lebih dari satu; kontrol kipas tidak dijalankan.");
            uint fans, minimum, maximum;
            Check(getFans(handle, out fans), "jumlah kipas");
            if (fans < 1 || fans > 16) throw new InvalidOperationException("Jumlah kipas GPU tidak didukung.");
            FanCount = (int)fans;
            Check(getRange(handle, out minimum, out maximum), "batas kipas");
            Minimum = minimum; Maximum = maximum;
        }
        catch { Dispose(); throw; }
    }
    static string NormalizeName(string name)
    {
        string value = name.Trim();
        if (value.StartsWith("NVIDIA ", StringComparison.OrdinalIgnoreCase)) value = value.Substring(7).Trim();
        return value.ToUpperInvariant();
    }
    T Load<T>(string name) where T : class
    {
        IntPtr pointer = GetProcAddress(library, name);
        if (pointer == IntPtr.Zero) throw new InvalidOperationException("Driver belum mendukung " + name + ".");
        return Marshal.GetDelegateForFunctionPointer(pointer, typeof(T)) as T;
    }
    void Check(int result, string operation)
    {
        if (result == 0) return;
        string detail = errorString == null ? result.ToString(CultureInfo.InvariantCulture) : Marshal.PtrToStringAnsi(errorString(result));
        throw new InvalidOperationException(operation + ": " + detail + " (NVML " + result + ")");
    }
    public FanBoostReading Read(int fan)
    {
        ValidateFan(fan);
        uint policy, target;
        Check(getPolicy(handle, (uint)fan, out policy), "baca mode kipas " + (fan + 1));
        Check(getTarget(handle, (uint)fan, out target), "baca target kipas " + (fan + 1));
        return new FanBoostReading(policy, target);
    }
    public void SetSpeed(int fan, uint percent)
    {
        ValidateFan(fan);
        if (percent > 100) throw new InvalidOperationException("Target kipas di luar batas.");
        Check(setSpeed(handle, (uint)fan, percent), "atur kipas " + (fan + 1));
    }
    public void SetPolicy(int fan, uint policy)
    {
        ValidateFan(fan);
        if (policy > 1) throw new InvalidOperationException("Mode kipas tidak didukung.");
        Check(setPolicy(handle, (uint)fan, policy), "pulihkan mode kipas " + (fan + 1));
    }
    void ValidateFan(int fan)
    {
        if (library == IntPtr.Zero || !initialized || fan < 0 || fan >= FanCount) throw new InvalidOperationException("Kipas GPU tidak tersedia.");
    }
    public void Dispose()
    {
        if (initialized) { initialized = false; try { shutdown(); } catch { } }
        if (library != IntPtr.Zero) { FreeLibrary(library); library = IntPtr.Zero; }
    }
}
