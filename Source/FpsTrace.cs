using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

// Count successful Present calls, grouped by swap chain. Ignore test/failed
// presents and keep the same bounded, timestamp-based window as PresentMon.
sealed class FpsPresents
{
    struct Pending { public string Chain; public double Time; }
    readonly int pid;
    readonly FpsFrames frames;
    readonly Dictionary<int, Pending> pending = new Dictionary<int, Pending>();
    readonly Dictionary<string, double> previous = new Dictionary<string, double>();
    public bool Seen { get; private set; }
    public double LastReceived { get; private set; }
    public FpsPresents(int processId) { pid = processId; frames = new FpsFrames(pid); }
    public void Start(int processId, int thread, string chain, double time, bool test)
    {
        if (processId != pid) return;
        pending.Remove(thread);
        if (test || pending.Count >= 32) return;
        pending[thread] = new Pending { Chain = chain, Time = time };
    }
    public void Stop(int processId, int thread, int result, double received)
    {
        Pending p;
        if (processId != pid || !pending.TryGetValue(thread, out p)) return;
        pending.Remove(thread);
        if (result < 0 || result == 0x087a0001) return; // failed / DXGI_STATUS_OCCLUDED
        double last;
        if (previous.TryGetValue(p.Chain, out last))
        {
            if (p.Time <= last) return;
            frames.AcceptFrame(pid, p.Chain, p.Time, (p.Time - last) * 1000, received);
        }
        else if (previous.Count >= 8) { previous.Clear(); }
        previous[p.Chain] = p.Time; Seen = true; LastReceived = received;
    }
    public double? Read(double now) { return frames.Read(now); }
}

// An idle ETW session is prepared before EA AntiCheat starts. Providers are
// disabled when idle; PID/event filters restrict capture to the selected game.
// No DLL is injected, no service is installed and no trace file is written.
sealed class FpsTrace : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] struct Wnode
    { public uint Size, Provider; public ulong Context; public long Timestamp; public Guid Guid; public uint Clock, Flags; }
    [StructLayout(LayoutKind.Sequential)] struct Properties
    {
        public Wnode Wnode;
        public uint BufferSize, MinimumBuffers, MaximumBuffers, MaximumFileSize, Mode, FlushTimer, EnableFlags;
        public int AgeLimit;
        public uint NumberOfBuffers, FreeBuffers, EventsLost, BuffersWritten, LogBuffersLost, RealTimeBuffersLost;
        public IntPtr LoggerThread;
        public uint FileOffset, NameOffset;
    }
    [StructLayout(LayoutKind.Sequential)] struct Filter { public ulong Pointer; public uint Size, Type; }
    [StructLayout(LayoutKind.Sequential)] struct EnableParameters
    { public uint Version, Property, Control; public Guid Source; public IntPtr Filters; public uint Count; }
    [StructLayout(LayoutKind.Sequential)] struct PropertyData { public ulong Name; public uint Index, Reserved; }
    // Windows SDK x64 EVENT_TRACE_LOGFILEW. Mini Monitor is built x64 only.
    [StructLayout(LayoutKind.Explicit, Size = 448)] struct LogFile
    {
        [FieldOffset(8)] public IntPtr Name;
        [FieldOffset(28)] public uint Mode;
        [FieldOffset(424)] public IntPtr Callback;
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate void EventCallback(IntPtr record);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern uint StartTrace(out ulong session, string name, IntPtr properties);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern uint ControlTrace(ulong session, string name, IntPtr properties, uint control);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern ulong OpenTrace(ref LogFile file);
    [DllImport("advapi32.dll")] static extern uint ProcessTrace(ulong[] handles, uint count, IntPtr start, IntPtr end);
    [DllImport("advapi32.dll")] static extern uint CloseTrace(ulong trace);
    [DllImport("advapi32.dll")] static extern uint EnableTraceEx2(ulong session, ref Guid provider, uint control, byte level, ulong any, ulong all, uint timeout, ref EnableParameters parameters);
    [DllImport("tdh.dll")] static extern uint TdhGetProperty(IntPtr record, uint contexts, IntPtr context, uint count, ref PropertyData property, uint size, out ulong value);
    static readonly Guid Dxgi = new Guid("ca11c036-0102-4a2d-a6ad-f03cfed5d3c9");
    static readonly Guid D3d9 = new Guid("783aca0a-790e-4d7f-8451-aa850511c6b9");
    readonly object gate = new object();
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly EventCallback callback;
    readonly string name;
    readonly Guid source = Guid.NewGuid();
    readonly IntPtr dxgiChain = Marshal.StringToHGlobalUni("pIDXGISwapChain"), d3d9Chain = Marshal.StringToHGlobalUni("pSwapchain");
    readonly IntPtr flags = Marshal.StringToHGlobalUni("Flags"), result = Marshal.StringToHGlobalUni("Result");
    IntPtr properties, loggerName;
    ulong session, trace = ulong.MaxValue;
    Task consumer;
    AppIdentity target;
    FpsPresents presents;
    long targetSince;
    bool disposed;
    public string SessionName { get { return name; } }
    public bool Seen { get { lock (gate) return presents != null && presents.Seen && clock.Elapsed.TotalSeconds - presents.LastReceived <= 5; } }
    internal long SessionBufferBytes
    {
        get
        {
            if (ControlTrace(session, name, properties, 0) != 0) return -1;
            var p = (Properties)Marshal.PtrToStructure(properties, typeof(Properties));
            return (long)p.NumberOfBuffers * p.BufferSize * 1024;
        }
    }
    public FpsTrace(string ownedSessionName = null)
    {
        name = ownedSessionName ?? ("MiniMonitor-Frames-" + Process.GetCurrentProcess().Id + "-" + Guid.NewGuid().ToString("N"));
        callback = OnEvent;
        try
        {
            if (IntPtr.Size != 8) throw new PlatformNotSupportedException("FPS memerlukan Windows 64-bit");
            int size = Marshal.SizeOf(typeof(Properties));
            int bytes = size + (name.Length + 1) * 2;
            properties = Marshal.AllocHGlobal(bytes);
            Marshal.Copy(new byte[bytes], 0, properties, bytes);
            var p = new Properties { Wnode = new Wnode { Size = (uint)bytes, Clock = 1, Flags = 0x20000, Guid = source },
                BufferSize = 16, MinimumBuffers = 2, MaximumBuffers = 8, Mode = 0x10000100, FlushTimer = 1, NameOffset = (uint)size };
            Marshal.StructureToPtr(p, properties, false);
            uint status = StartTrace(out session, name, properties);
            // The installed app holds its per-user singleton mutex. A session
            // with that exact name can only be its own residue after a crash.
            // Tests always use unique names and cannot stop the installed one.
            if (status == 183 && ownedSessionName != null && ControlTrace(0, name, properties, 1) == 0)
                status = StartTrace(out session, name, properties);
            if (status != 0) { session = 0; throw new Win32Exception((int)status); }
            loggerName = Marshal.StringToHGlobalUni(name);
            var file = new LogFile { Name = loggerName, Mode = 0x10001100, Callback = Marshal.GetFunctionPointerForDelegate(callback) };
            trace = OpenTrace(ref file);
            if (trace == ulong.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
            ulong handle = trace;
            consumer = Task.Factory.StartNew(() => ProcessTrace(new[] { handle }, 1, IntPtr.Zero, IntPtr.Zero),
                System.Threading.CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        catch { Dispose(); throw; }
    }
    uint Provider(Guid provider, int pid, short[] ids)
    {
        var parameters = new EnableParameters { Version = 2, Source = source };
        if (pid == 0) return EnableTraceEx2(session, ref provider, 0, 0, 0, 0, 0, ref parameters);
        IntPtr processId = Marshal.AllocHGlobal(4), events = Marshal.AllocHGlobal(4 + ids.Length * 2), descriptors = Marshal.AllocHGlobal(32);
        try
        {
            Marshal.WriteInt32(processId, pid); Marshal.WriteInt32(events, (ids.Length << 16) | 1);
            for (int i = 0; i < ids.Length; i++) Marshal.WriteInt16(events, 4 + i * 2, ids[i]);
            Marshal.StructureToPtr(new Filter { Pointer = (ulong)processId.ToInt64(), Size = 4, Type = 0x80000004 }, descriptors, false);
            Marshal.StructureToPtr(new Filter { Pointer = (ulong)events.ToInt64(), Size = (uint)(4 + ids.Length * 2), Type = 0x80000200 }, IntPtr.Add(descriptors, 16), false);
            parameters.Filters = descriptors; parameters.Count = 2;
            return EnableTraceEx2(session, ref provider, 1, 5, 0x8000000000000002, 0, 0, ref parameters);
        }
        finally { Marshal.FreeHGlobal(processId); Marshal.FreeHGlobal(events); Marshal.FreeHGlobal(descriptors); }
    }
    public void SetTarget(AppIdentity app)
    {
        lock (gate)
        {
            if (target == null ? app == null : app != null && target.Key == app.Key) return;
            target = app; presents = app == null ? null : new FpsPresents(app.ProcessId); targetSince = Stopwatch.GetTimestamp();
        }
        uint dxgi = Provider(Dxgi, app == null ? 0 : app.ProcessId, new short[] { 42, 43, 55, 56 });
        uint d3d9 = Provider(D3d9, app == null ? 0 : app.ProcessId, new short[] { 1, 2 });
        if (app != null && (dxgi != 0 || d3d9 != 0))
        {
            Provider(Dxgi, 0, null); Provider(D3d9, 0, null);
            lock (gate) { target = null; presents = null; }
            throw new Win32Exception((int)(dxgi != 0 ? dxgi : d3d9));
        }
    }
    static bool Property(IntPtr record, IntPtr name, uint size, out ulong value)
    {
        var property = new PropertyData { Name = (ulong)name.ToInt64(), Index = uint.MaxValue };
        return TdhGetProperty(record, 0, IntPtr.Zero, 1, ref property, size, out value) == 0;
    }
    void OnEvent(IntPtr record)
    {
        try
        {
            lock (gate)
            {
                // EVENT_RECORD header offsets from the Windows SDK; payload
                // properties are decoded by TDH, including 32-bit game events.
                if (target == null || Marshal.ReadInt32(record, 12) != target.ProcessId) return;
                long timestamp = Marshal.ReadInt64(record, 16); if (timestamp < targetSince) return;
                var provider = (Guid)Marshal.PtrToStructure(IntPtr.Add(record, 24), typeof(Guid));
                bool dxgi = provider == Dxgi; if (!dxgi && provider != D3d9) return;
                short id = Marshal.ReadInt16(record, 40); int thread = Marshal.ReadInt32(record, 8);
                ulong chain, presentFlags, hresult;
                if (dxgi ? id == 42 || id == 55 : id == 1)
                {
                    if (!Property(record, dxgi ? dxgiChain : d3d9Chain, 8, out chain) || !Property(record, flags, 4, out presentFlags)) return;
                    presents.Start(target.ProcessId, thread, (dxgi ? "X" : "9") + chain.ToString("x", CultureInfo.InvariantCulture),
                        timestamp / (double)Stopwatch.Frequency, dxgi && (presentFlags & 1) != 0);
                }
                else if ((dxgi ? id == 43 || id == 56 : id == 2) && Property(record, result, 4, out hresult))
                    presents.Stop(target.ProcessId, thread, unchecked((int)hresult), clock.Elapsed.TotalSeconds);
            }
        }
        catch { /* Never unwind through the native ETW callback. */ }
    }
    public double? Read(string key)
    { lock (gate) return target != null && target.Key == key ? presents.Read(clock.Elapsed.TotalSeconds) : null; }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        lock (gate) { target = null; presents = null; }
        if (session != 0) { Provider(Dxgi, 0, null); Provider(D3d9, 0, null); ControlTrace(session, name, properties, 1); session = 0; }
        if (trace != ulong.MaxValue) { CloseTrace(trace); trace = ulong.MaxValue; }
        if (consumer != null) consumer.GetAwaiter().GetResult();
        if (properties != IntPtr.Zero) Marshal.FreeHGlobal(properties);
        if (loggerName != IntPtr.Zero) Marshal.FreeHGlobal(loggerName);
        Marshal.FreeHGlobal(dxgiChain); Marshal.FreeHGlobal(d3d9Chain); Marshal.FreeHGlobal(flags); Marshal.FreeHGlobal(result);
        GC.KeepAlive(callback);
    }
}

// Serializes native session changes off the UI thread. Existing PresentMon
// remains an on-demand fallback for games without DXGI/D3D9 Present events.
sealed class FpsMonitor : IDisposable
{
    readonly object gate = new object();
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly FpsReader fallback = new FpsReader();
    readonly string sessionName;
    FpsTrace trace;
    AppIdentity desired;
    bool enabled, working, disposed, usingFallback;
    double changed, retryAfter;
    int revision;
    string status = "Menyiapkan FPS...";
    Task worker = Task.FromResult(0);
    public FpsMonitor(string ownedSessionName = null) { sessionName = ownedSessionName; }
    public string Status { get { lock (gate) return usingFallback ? fallback.Status : status; } }
    public void Configure(bool on, AppIdentity app)
    {
        lock (gate)
        {
            if (disposed) return;
            if (!on) app = null;
            bool same = desired == null ? app == null : app != null && desired.Key == app.Key;
            if (enabled == on && same && clock.Elapsed.TotalSeconds < retryAfter) return;
            bool fallbackNeeded = on && app != null && trace != null && !trace.Seen && clock.Elapsed.TotalSeconds - changed >= 5;
            bool retry = on && trace == null && clock.Elapsed.TotalSeconds >= retryAfter;
            if (enabled == on && same && !retry && fallbackNeeded == usingFallback) return;
            if (!same) changed = clock.Elapsed.TotalSeconds;
            desired = on ? app : null; enabled = on; revision++;
            if (!working) { working = true; worker = Task.Run((Action)Reconcile); }
        }
    }
    void Reconcile()
    {
        while (true)
        {
            AppIdentity app; bool on; int version;
            lock (gate) { app = desired; on = enabled && !disposed; version = revision; }
            try
            {
                if (!on)
                {
                    fallback.SetTarget(null);
                    FpsTrace old; lock (gate) { old = trace; trace = null; usingFallback = false; }
                    if (old != null) old.Dispose();
                }
                else
                {
                    if (trace == null) { var fresh = new FpsTrace(sessionName); lock (gate) trace = fresh; }
                    trace.SetTarget(app);
                    bool use; lock (gate) use = app != null && !trace.Seen && clock.Elapsed.TotalSeconds - changed >= 5;
                    fallback.SetTarget(use ? app : null);
                    lock (gate) { usingFallback = use; status = app == null ? "FPS siap sebelum game dibuka" : "Membaca frame game..."; }
                }
            }
            catch (Exception ex)
            {
                fallback.SetTarget(null);
                lock (gate)
                {
                    usingFallback = false; retryAfter = clock.Elapsed.TotalSeconds + 10;
                    var win = ex as Win32Exception;
                    bool admin;
                    using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
                        admin = new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                    status = win != null && win.NativeErrorCode == 5 ? (admin ? "Tutup game; tunggu FPS siap, lalu buka lagi" : "Mulai ulang sebagai administrator untuk FPS") : "FPS belum tersedia: " + ex.Message;
                }
            }
            lock (gate) { if (revision == version) { working = false; return; } }
        }
    }
    public double? Read(string key)
    {
        lock (gate)
        {
            if (!enabled || desired == null || desired.Key != key) return null;
            double? value = trace == null ? null : trace.Read(key);
            return value ?? (usingFallback ? fallback.Read(key) : null);
        }
    }
    public async Task StopAsync()
    {
        Task pending;
        lock (gate)
        {
            disposed = true; enabled = false; desired = null; revision++;
            if (!working) { working = true; worker = Task.Run((Action)Reconcile); }
            pending = worker;
        }
        await pending.ConfigureAwait(false); await fallback.StopAsync().ConfigureAwait(false);
    }
    public void Dispose() { StopAsync().GetAwaiter().GetResult(); }
}
