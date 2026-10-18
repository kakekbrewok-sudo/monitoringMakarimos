using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

static class ThemeTrayPollingChecks
{
    static readonly BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
    static readonly StringBuilder log = new StringBuilder();
    static int clockTicks, sensorTicks;
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern IntPtr GetActiveWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    static string Focus()
    {
        uint pid; IntPtr foreground = GetForegroundWindow(); GetWindowThreadProcessId(foreground, out pid);
        return " foreground=" + foreground + " pid=" + pid + " active=" + GetActiveWindow();
    }
    static object Get(object o, string name) { return o.GetType().GetField(name, flags).GetValue(o); }
    static void Set(object o, string name, object value) { o.GetType().GetField(name, flags).SetValue(o, value); }
    static void Call(object o, string name, params object[] args) { o.GetType().GetMethod(name, flags).Invoke(o, args); }
    static void Check(bool ok, string text) { if (!ok) throw new Exception("FAIL: " + text); log.AppendLine("PASS: " + text); }
    static void Click(ToolStripItem item) { typeof(ToolStripItem).GetMethod("HandleClick", flags).Invoke(item, new object[] { EventArgs.Empty }); }
    static async Task SwitchThemes(MonitorWindow form)
    {
        var tray = (NotifyIcon)Get(form, "tray");
        var menu = (ContextMenuStrip)Get(form, "menu");
        var choices = (ToolStripMenuItem)Get(form, "themeMenu");
        Call(tray, "ShowContextMenu"); choices.Select(); choices.ShowDropDown(); choices.DropDownItems[0].Select();
        IntPtr handle = form.Handle; Rectangle bounds = form.Bounds;
        foreach (MonitorTheme theme in Enum.GetValues(typeof(MonitorTheme)))
        {
            Click(choices.DropDownItems[(int)theme]);
            int clockBefore = clockTicks, sensorsBefore = sensorTicks;
            DateTime pollBefore = (DateTime)Get(form, "lastPoll");
            await Task.Delay(3400);
            Check(menu.Visible && choices.DropDown.Visible, theme + ": theme submenu remains open across live clock/sensor updates");
            Check(clockTicks - clockBefore >= 3 && sensorTicks - sensorsBefore >= 3 && (DateTime)Get(form, "lastPoll") > pollBefore, theme + ": clock and sensors continue while selecting themes");
            Check(form.Handle == handle && form.Bounds == bounds && (MonitorTheme)Get(form, "theme") == theme, theme + ": correct theme without moving or recreating window");
        }
        menu.Close(ToolStripDropDownCloseReason.Keyboard);
        await Task.Delay(100);
    }
    static async Task HoldTrayMenu(MonitorWindow form, string label, ToolStripMenuItem submenu)
    {
        var tray = (NotifyIcon)Get(form, "tray");
        var menu = (ContextMenuStrip)Get(form, "menu");
        // Use NotifyIcon's actual context-menu entry point, which gives its
        // native notification window foreground ownership (not menu.Show).
        Call(tray, "ShowContextMenu");
        log.AppendLine(label + ": opened" + Focus());
        await Task.Delay(100);
        Check(menu.Visible, label + ": tray context menu opens");
        if (submenu != null) { submenu.Select(); submenu.ShowDropDown(); submenu.DropDownItems[0].Select(); }
        int startClock = clockTicks, startSensors = sensorTicks;
        DateTime lastPoll = (DateTime)Get(form, "lastPoll");
        IntPtr windowHandle = form.Handle;
        await Task.Delay(3400);
        Check(clockTicks - startClock >= 3 && sensorTicks - startSensors >= 3 && (DateTime)Get(form, "lastPoll") > lastPoll,
            label + ": real clock and sensor timers keep updating");
        log.AppendLine(label + ": root visible=" + menu.Visible + ", submenu visible=" + (submenu == null ? "n/a" : submenu.DropDown.Visible.ToString()));
        Check(menu.Visible && (submenu == null || submenu.DropDown.Visible), label + ": menu survives multiple live refreshes");
        Check(form.Handle == windowHandle, label + ": window handle remains stable");
        var option = (ToolStripMenuItem)Get(form, submenu == null ? "keepMenuItem" : "clockSoundItem");
        if (submenu != null)
        {
            bool before = option.Checked; Click(option);
            Check(option.Checked != before && menu.Visible && submenu.DropDown.Visible, label + ": delayed click is usable and updates check mark");
            Click(option); // restore muted QA setting
        }
        menu.Close(ToolStripDropDownCloseReason.Keyboard);
        await Task.Delay(100);
        Check(!menu.Visible, label + ": Escape can still dismiss");
    }
    [STAThread] static int Main(string[] args)
    {
        int result = 0;
        if (args.Length < 3 || args[2] != "--child") return DesktopTestRunner.Run(args);
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        using (var form = new MonitorWindow(false))
        {
            Set(form, "settingsPath", Path.Combine(args[0], "polling-test-settings.txt"));
            Set(form, "keepMenuOpen", true); Set(form, "clockSound", false); Set(form, "appSound", false);
            Set(form, "pin", true); Set(form, "interval", 1000);
            ((Timer)Get(form, "timer")).Interval = 1000;
            ((Timer)Get(form, "timer")).Tick += (s,e) => sensorTicks++;
            ((Timer)Get(form, "clockTimer")).Tick += (s,e) => clockTicks++;
            form.Location = new Point(Screen.PrimaryScreen.WorkingArea.Right - form.Width - 30, 30);
            ((ContextMenuStrip)Get(form, "menu")).Closed += (s,e) => log.AppendLine("Menu closed: " + e.CloseReason + Focus());
            form.Activated += (s,e) => log.AppendLine("Monitor activated" + Focus());
            form.Shown += async (s,e) =>
            {
                try
                {
                    for (int wait=0; wait<100 && (!(bool)((Timer)Get(form,"timer")).Enabled || (bool)Get(form,"busy")); wait++) await Task.Delay(100);
                    Check(((Timer)Get(form, "timer")).Enabled, "live sensor polling started");
                    await SwitchThemes(form);
                    await HoldTrayMenu(form, "Pinned window / root", null);
                    var sound = (ToolStripMenuItem)((ToolStripMenuItem)Get(form, "clockSoundItem")).OwnerItem;
                    await HoldTrayMenu(form, "Pinned window / sound submenu", sound);
                    Call(form, "SetOverlay", true); await Task.Delay(200);
                    await HoldTrayMenu(form, "Overlay / sound submenu", sound);
                    form.Hide(); await Task.Delay(100);
                    await HoldTrayMenu(form, "Hidden in tray / sound submenu", sound);
                    Check(!form.Visible, "background refresh does not reshow the hidden overlay");
                    // Even normal click-to-close mode must wait for user input.
                    Set(form, "keepMenuOpen", false);
                    await HoldTrayMenu(form, "Normal menu mode / root", null);
                    var tray = (NotifyIcon)Get(form, "tray"); Call(tray, "ShowContextMenu");
                    sound.Select(); sound.ShowDropDown(); Click((ToolStripMenuItem)Get(form, "clockSoundItem"));
                    await Task.Delay(100);
                    Check(!((ContextMenuStrip)Get(form, "menu")).Visible, "normal menu mode closes only after selecting an option");
                }
                catch (Exception ex) { result = 1; log.AppendLine(ex.ToString()); }
                finally { form.Close(); }
            };
            Application.Run(form);
        }
        File.WriteAllText(Path.Combine(args[0], args.Length > 1 ? args[1] : "tray-polling-checks.txt"), log.ToString());
        return result;
    }
}

static class DesktopTestRunner
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct StartupInfo
    {
        public int cb;
        public string reserved, desktop, title;
        public int x, y, xSize, ySize, xChars, yChars, fill, flags;
        public short show, reservedSize;
        public IntPtr reservedData, stdin, stdout, stderr;
    }
    [StructLayout(LayoutKind.Sequential)] struct ProcessInfo { public IntPtr process, thread; public uint pid, tid; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateDesktop(string name, IntPtr device, IntPtr mode, uint flags, uint access, IntPtr security);
    [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CreateProcess(string app, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity, bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInfo info);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle, uint ms);
    [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr process, out uint code);
    [DllImport("kernel32.dll")] static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    public static int Run(string[] args)
    {
        string name = "MiniMonitorThemeQA-" + System.Diagnostics.Process.GetCurrentProcess().Id;
        IntPtr desktop = CreateDesktop(name, IntPtr.Zero, IntPtr.Zero, 0, 0x01ff, IntPtr.Zero);
        if (desktop == IntPtr.Zero) { File.WriteAllText(Path.Combine(args[0], "isolated-desktop-error.txt"), "CreateDesktop: " + Marshal.GetLastWin32Error()); return 2; }
        ProcessInfo process = new ProcessInfo();
        try
        {
            string executable = Assembly.GetExecutingAssembly().Location;
            string report = args.Length > 1 ? args[1] : "tray-polling-checks.txt";
            var startup = new StartupInfo { cb = Marshal.SizeOf(typeof(StartupInfo)), desktop = name };
            var command = new StringBuilder("\"" + executable + "\" \"" + args[0] + "\" \"" + report + "\" --child");
            if (!CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, Path.GetDirectoryName(executable), ref startup, out process))
            { File.WriteAllText(Path.Combine(args[0], "isolated-desktop-error.txt"), "CreateProcess: " + Marshal.GetLastWin32Error()); return 2; }
            // Only the child test process is terminated on timeout. The user
            // desktop is never switched, and the installed app is untouched.
            if (WaitForSingleObject(process.process, 85000) != 0) { TerminateProcess(process.process, 3); return 3; }
            uint code; GetExitCodeProcess(process.process, out code); return (int)code;
        }
        finally
        {
            if (process.thread != IntPtr.Zero) CloseHandle(process.thread);
            if (process.process != IntPtr.Zero) CloseHandle(process.process);
            CloseDesktop(desktop);
        }
    }
}
