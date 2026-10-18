using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

sealed class AppIdentity
{
    public int ProcessId;
    public string Name, Key, ExecutablePath;
    public DateTime? OpenedUtc;
}

sealed class AppSession
{
    public AppIdentity App;
    public double ActiveSeconds;
    public int AcknowledgedHours;
    public bool ReminderDue { get { return ActiveSeconds >= (AcknowledgedHours + 1) * 3600.0; } }
    public double ReminderProgress { get { return Math.Max(0, Math.Min(1, (ActiveSeconds - AcknowledgedHours * 3600.0) / 3600.0)); } }
    public void Acknowledge() { AcknowledgedHours = (int)(ActiveSeconds / 3600.0); }
}

// Foreground duration is sampled once per second and is never inferred from
// process uptime. Process ID + start timestamp isolate separate launches.
sealed class ActivityTracker
{
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr window, StringBuilder text, int count);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref uint size);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    readonly Dictionary<string, AppSession> sessions = new Dictionary<string, AppSession>();
    readonly int ownPid = Process.GetCurrentProcess().Id;
    readonly Stopwatch watch = Stopwatch.StartNew();
    double lastTick;
    IntPtr lastWindow;
    uint lastPid;
    AppIdentity cached;
    string previousKey;
    int checks;
    public AppSession Selected { get; private set; }
    public bool IsActive { get; private set; }
    public bool Suspended { get; private set; }
    public bool ActiveHourReached { get; private set; }
    public void Pause(bool pause) { Suspended = pause; previousKey = null; lastTick = watch.Elapsed.TotalSeconds; IsActive = false; }
    public static string Duration(double seconds)
    {
        long total = (long)Math.Max(0, seconds);
        return String.Format("{0:00}:{1:00}:{2:00}", total / 3600, total / 60 % 60, total % 60);
    }
    public static double OpenDuration(AppIdentity app, DateTime utcNow)
    { return app != null && app.OpenedUtc.HasValue ? Math.Max(0, (utcNow - app.OpenedUtc.Value).TotalSeconds) : -1; }
    public void Advance(AppIdentity app, double elapsed, bool paused)
    {
        ActiveHourReached = false;
        // Long gaps represent suspend, a blocked event loop, or lock. Do not
        // charge that unobserved time to the last application.
        AppSession previous;
        if (!paused && app != null && elapsed >= 0 && elapsed <= 5 && previousKey != null && sessions.TryGetValue(previousKey, out previous))
        {
            long oldHour = (long)(previous.ActiveSeconds / 3600);
            previous.ActiveSeconds += elapsed;
            ActiveHourReached = (long)(previous.ActiveSeconds / 3600) > oldHour;
        }
        if (paused || app == null) { IsActive = false; previousKey = null; return; }
        AppSession current;
        if (!sessions.TryGetValue(app.Key, out current))
        {
            if (sessions.Count >= 128) sessions.Remove(sessions.Keys.First(k => k != previousKey));
            current = new AppSession { App = app }; sessions.Add(app.Key, current);
        }
        Selected = current; previousKey = app.Key; IsActive = true;
    }
    public void ResetSelected() { if (Selected != null) { Selected.ActiveSeconds = 0; Selected.AcknowledgedHours = 0; } }
    internal static string ExecutablePath(int pid)
    {
        // MainModule requires access that some games disallow. Ask only for
        // executable metadata, without opening their memory or enumerating modules.
        IntPtr handle = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
        if (handle == IntPtr.Zero) return null;
        try
        {
            var path = new StringBuilder(1024); uint size = (uint)path.Capacity;
            if (QueryFullProcessImageName(handle, 0, path, ref size)) return path.ToString();
            if (Marshal.GetLastWin32Error() != 122) return null; // ERROR_INSUFFICIENT_BUFFER
            path = new StringBuilder(32768); size = (uint)path.Capacity;
            return QueryFullProcessImageName(handle, 0, path, ref size) ? path.ToString() : null;
        }
        finally { CloseHandle(handle); }
    }
    public void Poll()
    {
        double now = watch.Elapsed.TotalSeconds, delta = now - lastTick; lastTick = now;
        AppIdentity app = null;
        if (!Suspended)
        {
            IntPtr window = GetForegroundWindow(); uint pid = 0;
            if (window != IntPtr.Zero) GetWindowThreadProcessId(window, out pid);
            if (window != lastWindow || pid != lastPid)
            {
                lastWindow = window; lastPid = pid; cached = Resolve(window, pid);
            }
            app = cached;
        }
        Advance(app, delta, Suspended);
        if (app == null && Selected != null && ++checks % 5 == 0)
        {
            try { using (var p = Process.GetProcessById(Selected.App.ProcessId)) if (p.HasExited || (Selected.App.OpenedUtc.HasValue && p.StartTime.ToUniversalTime() != Selected.App.OpenedUtc.Value)) Selected = null; }
            catch { Selected = null; }
        }
    }
    AppIdentity Resolve(IntPtr window, uint pid)
    {
        if (pid == 0 || pid == ownPid) return null;
        var name = new StringBuilder(256); GetClassName(window, name, name.Capacity);
        if (name.ToString() == "Progman" || name.ToString() == "WorkerW" || name.ToString() == "Shell_TrayWnd") return null;
        try
        {
            using (var p = Process.GetProcessById((int)pid))
            {
                string processName = p.ProcessName;
                if (processName == "LockApp" || processName == "LogonUI" || processName == "SearchHost" || processName == "StartMenuExperienceHost" || processName == "MiniMonitor") return null;
                DateTime? start = null; try { start = p.StartTime.ToUniversalTime(); } catch { }
                string label = processName, executable = ExecutablePath((int)pid);
                try { if (!String.IsNullOrEmpty(executable)) { var file = FileVersionInfo.GetVersionInfo(executable); if (!String.IsNullOrWhiteSpace(file.FileDescription)) label = file.FileDescription; } } catch { }
                // Avoid window/document titles: only application metadata is read.
                if (label.Length > 80) label = label.Substring(0, 80);
                return new AppIdentity { ProcessId = (int)pid, Name = label, ExecutablePath = executable, OpenedUtc = start, Key = pid + ":" + (start.HasValue ? start.Value.Ticks.ToString() : window.ToInt64().ToString()) };
            }
        }
        catch { return null; }
    }
}
