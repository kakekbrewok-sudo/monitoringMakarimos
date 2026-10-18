using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

// Presented FPS, not monitor refresh rate or a guess from GPU utilization.
// Buffers are bounded; event timestamps (not stdout arrival times) define FPS.
sealed class FpsFrames
{
    struct Frame { public double Time, Milliseconds; }
    sealed class Chain
    {
        public readonly Queue<Frame> Frames = new Queue<Frame>();
        public double Total, LastTime = -1, Received;
    }
    readonly object gate = new object();
    readonly Dictionary<string, Chain> chains = new Dictionary<string, Chain>();
    readonly int targetPid;
    int pidIndex = -1, chainIndex = -1, timeIndex = -1, intervalIndex = -1;
    public FpsFrames(int pid) { targetPid = pid; }
    static bool Number(string text, out double value)
    { return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && !double.IsNaN(value) && !double.IsInfinity(value); }
    static string[] Columns(string line)
    {
        if (line.IndexOf('"') < 0) return line.Split(',');
        var fields = new List<string>(); var part = new StringBuilder(); bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"') { if (quoted && i + 1 < line.Length && line[i + 1] == '"') { part.Append(c); i++; } else quoted = !quoted; }
            else if (c == ',' && !quoted) { fields.Add(part.ToString()); part.Length = 0; }
            else part.Append(c);
        }
        fields.Add(part.ToString()); return fields.ToArray();
    }
    public void Accept(string line, double received)
    {
        if (String.IsNullOrEmpty(line) || line.Length > 8192) return;
        string[] fields = Columns(line.TrimStart('\uFEFF'));
        lock (gate)
        {
            if (fields.Length > 1 && fields[0] == "Application")
            {
                pidIndex = Array.IndexOf(fields, "ProcessID"); chainIndex = Array.IndexOf(fields, "SwapChainAddress");
                timeIndex = Array.IndexOf(fields, "TimeInSeconds");
                intervalIndex = Array.FindIndex(fields, s => s.Equals("msBetweenPresents", StringComparison.OrdinalIgnoreCase));
                chains.Clear(); return;
            }
            if (pidIndex < 0 || chainIndex < 0 || timeIndex < 0 || intervalIndex < 0 ||
                fields.Length <= Math.Max(Math.Max(pidIndex, chainIndex), Math.Max(timeIndex, intervalIndex))) return;
            int pid; double time, ms;
            if (!int.TryParse(fields[pidIndex], out pid) || pid != targetPid ||
                !Number(fields[timeIndex], out time) || time < 0 || !Number(fields[intervalIndex], out ms) || ms <= 0 || ms > 60000) return;
            AcceptFrame(pid, fields[chainIndex], time, ms, received);
        }
    }
    internal void AcceptFrame(int pid, string key, double time, double ms, double received)
    {
        if (pid != targetPid || String.IsNullOrEmpty(key) || key.Length > 32 || time < 0 ||
            double.IsNaN(time) || double.IsInfinity(time) || double.IsNaN(ms) || double.IsInfinity(ms) || ms <= 0 || ms > 60000) return;
        lock (gate)
        {
            Chain chain;
            if (!chains.TryGetValue(key, out chain))
            {
                if (chains.Count >= 8) chains.Remove(chains.OrderBy(p => p.Value.Received).First().Key);
                chain = new Chain(); chains.Add(key, chain);
            }
            if (time <= chain.LastTime) return;
            if (time - chain.LastTime > 2) { chain.Frames.Clear(); chain.Total = 0; }
            chain.LastTime = time; chain.Received = received;
            chain.Frames.Enqueue(new Frame { Time = time, Milliseconds = ms }); chain.Total += ms;
            while (chain.Frames.Count > 4096 || (chain.Frames.Count > 1 && chain.Frames.Peek().Time < time - 1)) chain.Total -= chain.Frames.Dequeue().Milliseconds;
        }
    }
    public double? Read(double now)
    {
        lock (gate)
        {
            Chain best = null;
            foreach (Chain c in chains.Values)
                if (now - c.Received <= 2.5 && c.Frames.Count >= 3 && c.Total >= 250 &&
                    (best == null || c.Frames.Count > best.Frames.Count)) best = c;
            return best == null ? (double?)null : 1000.0 * best.Frames.Count / best.Total;
        }
    }
    internal int BufferedFrames { get { lock (gate) return chains.Values.Sum(c => c.Frames.Count); } }
}

sealed class FpsRules
{
    readonly Dictionary<string, bool> overrides = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
    public static string Key(AppIdentity app) { return app == null ? "" : (app.ExecutablePath ?? ""); }
    public bool Tracks(AppIdentity app)
    {
        string path = Key(app); if (path.Length == 0) return false;
        bool choice; if (overrides.TryGetValue(path, out choice)) return choice;
        string p = path.Replace('/', '\\').ToLowerInvariant();
        string name = Path.GetFileNameWithoutExtension(p);
        if (name.Contains("launcher") || name.Contains("crash") || name.Contains("helper") || name.Contains("anticheat") ||
            name == "steam" || name == "riotclientservices" || name == "eadesktop" || name == "eabackgroundservice" || name == "origin") return false;
        // Match complete directory names, including manually installed games.
        // A name such as GamesTools must not start frame capture by accident.
        return p.Contains("\\games\\") || p.Contains("\\xboxgames\\") ||
            p.Contains("\\steamapps\\common\\") || p.Contains("\\epic games\\") ||
            p.Contains("\\riot games\\") || p.Contains("\\gog games\\") || p.Contains("\\ea games\\") ||
            p.Contains("\\origin games\\") || p.Contains("\\ubisoft game launcher\\games\\");
    }
    public void Set(AppIdentity app, bool enabled)
    {
        string key = Key(app); if (key.Length == 0) return;
        if (overrides.Count >= 128 && !overrides.ContainsKey(key)) overrides.Remove(overrides.Keys.First());
        overrides[key] = enabled;
    }
    public string Serialize()
    {
        return String.Join(";", overrides.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p =>
            (p.Value ? "+" : "-") + Convert.ToBase64String(Encoding.UTF8.GetBytes(p.Key))));
    }
    public void Restore(string saved)
    {
        overrides.Clear(); if (saved == null || saved.Length > 100000) return;
        foreach (string entry in saved.Split(';').Take(128))
        {
            if (entry.Length < 2 || (entry[0] != '+' && entry[0] != '-')) continue;
            try { string path = Encoding.UTF8.GetString(Convert.FromBase64String(entry.Substring(1))); if (path.Length > 0 && path.Length <= 2048) overrides[path] = entry[0] == '+'; }
            catch (FormatException) { }
        }
    }
}

// Start/stop happens off the WinForms thread. Only a selected game's PID is
// captured. No CSV/ETL files, service installation, injection or global hotkey.
sealed class FpsReader : IDisposable
{
    readonly object gate = new object();
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly string executable;
    readonly string session = "MiniMonitor-FPS-" + Process.GetCurrentProcess().Id + "-" + Guid.NewGuid().ToString("N");
    AppIdentity desired;
    string currentKey;
    Process capture, watchedGame;
    FpsJob job;
    FpsFrames frames;
    bool working, disposed;
    int revision;
    Task worker = Task.FromResult(0);
    string status = "Siap saat game aktif";
    public FpsReader() : this(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fps", "PresentMon.exe")) { }
    internal FpsReader(string path) { executable = path; }
    public string Status { get { lock (gate) return status; } }
    internal int HelperId { get { lock (gate) { try { return capture == null || capture.HasExited ? 0 : capture.Id; } catch { return 0; } } } }
    internal string SessionName { get { return session; } }
    public double? Read(string key)
    {
        lock (gate)
        {
            if (desired == null || desired.Key != key || currentKey != key || capture == null || frames == null) return null;
            try { if (capture.HasExited) return null; } catch { return null; }
            return frames.Read(clock.Elapsed.TotalSeconds);
        }
    }
    public void SetTarget(AppIdentity target)
    {
        lock (gate)
        {
            if (disposed || (desired == null ? target == null : target != null && desired.Key == target.Key)) return;
            desired = target; revision++;
            status = target == null ? "Siap saat game aktif" : "Membaca frame game...";
            if (!working) { working = true; worker = Task.Run((Action)Reconcile); }
        }
    }
    void Reconcile()
    {
        while (true)
        {
            AppIdentity target; int version;
            lock (gate) { target = disposed ? null : desired; version = revision; }
            StopCapture();
            if (target != null)
            {
                try { StartCapture(target); }
                catch (Exception ex) { StopCapture(); lock (gate) status = "FPS belum tersedia: " + ex.GetBaseException().Message; }
            }
            lock (gate) { if (revision == version) { working = false; return; } }
        }
    }
    ProcessStartInfo StartInfo(string arguments)
    {
        return new ProcessStartInfo(executable, arguments) { UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(executable) };
    }
    void StartCapture(AppIdentity target)
    {
        if (!File.Exists(executable)) throw new FileNotFoundException("komponen FPS tidak ditemukan");
        var game = Process.GetProcessById(target.ProcessId);
        lock (gate) watchedGame = game;
        if (game.HasExited || (target.OpenedUtc.HasValue && game.StartTime.ToUniversalTime() != target.OpenedUtc.Value)) throw new InvalidOperationException("game sudah ditutup");
        var buffer = new FpsFrames(target.ProcessId);
        var process = new Process { StartInfo = StartInfo("--process_id " + target.ProcessId.ToString(CultureInfo.InvariantCulture) +
            " --output_stdout --no_console_stats --v1_metrics --no_track_gpu --no_track_input --no_track_display --terminate_on_proc_exit --session_name " + session), EnableRaisingEvents = true };
        var lifetime = new FpsJob();
        lock (gate) { capture = process; job = lifetime; frames = buffer; currentKey = target.Key; }
        process.OutputDataReceived += (s, e) => { if (e.Data != null) buffer.Accept(e.Data, clock.Elapsed.TotalSeconds); };
        process.ErrorDataReceived += (s, e) =>
        {
            if (String.IsNullOrWhiteSpace(e.Data)) return;
            lock (gate) if (capture == process && (!status.StartsWith("FPS: error:") || e.Data.StartsWith("error:")))
                status = "FPS: " + e.Data.Substring(0, Math.Min(180, e.Data.Length));
        };
        process.Exited += (s, e) => { lock (gate) if (capture == process && !status.StartsWith("FPS:")) status = "Pembaca FPS berhenti; aktifkan ulang FPS untuk mencoba"; };
        process.Start(); lifetime.Attach(process); process.BeginOutputReadLine(); process.BeginErrorReadLine();
        // Some traces miss a target's ProcessStop event. A read-only process
        // exit notification guarantees cleanup even with no further frames.
        game.Exited += (s, e) => { lock (gate) if (desired != null && desired.Key == target.Key) SetTarget(null); };
        game.EnableRaisingEvents = true;
    }
    void StopCapture()
    {
        Process process, game; FpsJob lifetime;
        lock (gate) { process = capture; game = watchedGame; watchedGame = null; lifetime = job; capture = null; job = null; frames = null; currentKey = null; }
        if (game != null) game.Dispose();
        if (process == null) return;
        try
        {
            // End only our uniquely named ETW session. This lets PresentMon
            // release tracing resources before any bounded kill fallback.
            if (File.Exists(executable)) using (var stop = new Process { StartInfo = StartInfo("--session_name " + session + " --terminate_existing_session") })
            {
                stop.Start(); stop.BeginOutputReadLine(); stop.BeginErrorReadLine();
                if (!stop.WaitForExit(2000)) { stop.Kill(); stop.WaitForExit(1000); }
            }
        }
        catch { }
        finally
        {
            try { if (!process.HasExited && !process.WaitForExit(1500)) { process.Kill(); process.WaitForExit(1000); } } catch { }
            if (lifetime != null) lifetime.Dispose(); process.Dispose();
        }
    }
    public Task StopAsync()
    {
        lock (gate)
        {
            if (disposed) return worker;
            disposed = true; desired = null; revision++;
            if (!working) { working = true; worker = Task.Run((Action)Reconcile); }
            return worker;
        }
    }
    public void Dispose() { StopAsync().GetAwaiter().GetResult(); }
}

// A Windows job also ends the helper if Mini Monitor is terminated abruptly.
sealed class FpsJob : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] struct BasicLimits { public long PerProcess, PerJob; public uint Flags; public UIntPtr MinWorking, MaxWorking; public uint Active; public UIntPtr Affinity; public uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)] struct IoCounters { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] struct Limits { public BasicLimits Basic; public IoCounters Io; public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateJobObject(IntPtr attributes, string name);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetInformationJobObject(IntPtr job, int type, ref Limits limits, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    IntPtr handle;
    public FpsJob()
    {
        handle = CreateJobObject(IntPtr.Zero, null);
        var limits = new Limits { Basic = new BasicLimits { Flags = 0x2000 } };
        if (handle == IntPtr.Zero || !SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf(typeof(Limits))))
        { int error = Marshal.GetLastWin32Error(); Dispose(); throw new System.ComponentModel.Win32Exception(error); }
    }
    public void Attach(Process process) { if (!AssignProcessToJobObject(handle, process.Handle)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); }
    public void Dispose() { if (handle != IntPtr.Zero) { CloseHandle(handle); handle = IntPtr.Zero; } }
}
