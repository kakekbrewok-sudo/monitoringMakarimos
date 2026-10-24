// Mini Monitor 3.5.4: native Windows Forms / .NET Framework 4.7.2+.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;

static class Native
{
    [StructLayout(LayoutKind.Sequential)] public struct Memory
    {
        public uint Length, Load;
        public ulong Total, Available, Page, AvailablePage, Virtual, AvailableVirtual, Extended;
    }
    [DllImport("kernel32.dll")] public static extern bool GlobalMemoryStatusEx(ref Memory m);
    [DllImport("user32.dll")] public static extern bool ReleaseCapture();
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, int m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint key);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr h, int index, int value);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int attribute, ref int value, int size);
    public static bool Admin { get { using (var i = WindowsIdentity.GetCurrent()) return new WindowsPrincipal(i).IsInRole(WindowsBuiltInRole.Administrator); } }
    public static bool DriverInstalled
    {
        get
        {
            using (var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO")) return key != null;
        }
    }
}

sealed class Reading
{
    public string Kind, Name, Sensor, LoadSensor, Error;
    public float? Value, Load;
    public Reading(string kind, string name) { Kind = kind; Name = name; }
    public string Text { get { return Value.HasValue ? Value.Value.ToString("0") + " °C" : "—"; } }
    public string UsageAndTemperature { get { return (Load.HasValue ? Load.Value.ToString("0") + "%" : "—") + " / " + (Value.HasValue ? Value.Value.ToString("0") + "°C" : "—"); } }
}

sealed class Snapshot
{
    public double Used, Total;
    public uint Load;
    public bool MemoryValid;
    public DateTime Timestamp = DateTime.Now;
    public Reading Cpu = new Reading("CPU", "Membaca sensor CPU...");
    public Reading Gpu = new Reading("GPU", "Membaca sensor GPU...");
    public List<Reading> Disks = new List<Reading>();
    public string Error;
}

sealed class SensorReader : IDisposable
{
    Computer computer;
    DateTime nextDiskRead = DateTime.MinValue;
    List<Reading> disks = new List<Reading>();
    string openError;
    public SensorReader()
    {
        computer = new Computer { IsCpuEnabled = true, IsGpuEnabled = true, IsStorageEnabled = true };
        LibreHardwareMonitor.Hardware.Storage.StorageDevice.ThrottleInterval = TimeSpan.FromSeconds(15);
        try { computer.Open(); } catch (Exception ex) { openError = ex.GetBaseException().Message; }
    }
    public static bool Valid(float? v) { return v.HasValue && !float.IsNaN(v.Value) && v.Value > 0 && v.Value < 150; }
    public static bool ValidLoad(float? v) { return v.HasValue && !float.IsNaN(v.Value) && v.Value >= 0 && v.Value <= 100; }
    public static int LoadRank(string kind, string name)
    {
        string n = name.ToLowerInvariant();
        if (kind == "CPU") return n == "cpu total" ? 100 : -1;
        if (kind == "GPU") return n == "gpu core" ? 100 : n == "d3d 3d" ? 80 : -1;
        return -1;
    }
    public static int Rank(string kind, string name)
    {
        string n = name.ToLowerInvariant();
        // NVMe also exposes warning/critical thresholds as Temperature sensors.
        if (n.Contains("critical") || n.Contains("warning") || n.Contains("limit") || n.Contains("distance") || n.Contains("t.j.") || n.Contains("tj max")) return -1;
        if (kind == "CPU") return n.Contains("tctl/tdie") || n.Contains("package") ? 100 : n.Contains("tdie") ? 90 : 10;
        if (kind == "GPU") return n == "gpu core" ? 100 : n.Contains("hot spot") || n.Contains("memory") ? 5 : 20;
        return n.Contains("composite") ? 100 : n == "temperature" ? 90 : 10;
    }
    static void Update(IHardware h) { h.Update(); foreach (IHardware child in h.SubHardware) Update(child); }
    static IEnumerable<ISensor> Sensors(IHardware h)
    {
        foreach (ISensor s in h.Sensors) yield return s;
        foreach (IHardware child in h.SubHardware) foreach (ISensor s in Sensors(child)) yield return s;
    }
    static Reading Read(IHardware h, string kind)
    {
        var r = new Reading(kind, h.Name);
        try
        {
            Update(h);
            ISensor selected = Sensors(h).Where(s => s.SensorType == SensorType.Temperature && Valid(s.Value) && Rank(kind, s.Name) >= 0)
                .OrderByDescending(s => Rank(kind, s.Name)).ThenByDescending(s => s.Value.Value).FirstOrDefault();
            if (selected != null) { r.Value = selected.Value; r.Sensor = selected.Name; }
            if (kind == "CPU" || kind == "GPU")
            {
                ISensor load = Sensors(h).Where(s => s.SensorType == SensorType.Load && ValidLoad(s.Value) && LoadRank(kind, s.Name) >= 0)
                    .OrderByDescending(s => LoadRank(kind, s.Name)).FirstOrDefault();
                if (load != null) { r.Load = load.Value; r.LoadSensor = load.Name; }
            }
        }
        catch (Exception ex) { r.Error = ex.GetBaseException().Message; }
        return r;
    }
    static string DiskKind(IHardware h)
    {
        try
        {
            object storage = h.GetType().GetProperty("Storage").GetValue(h, null);
            foreach (string property in new[] { "IsSSD", "IsNVMe" })
            { var p = storage.GetType().GetProperty(property); if (p != null && (bool)p.GetValue(storage, null)) return "SSD"; }
        }
        catch { }
        return "HDD";
    }
    public Snapshot Poll()
    {
        var r = new Snapshot { Error = openError };
        var m = new Native.Memory { Length = (uint)Marshal.SizeOf(typeof(Native.Memory)) };
        if (Native.GlobalMemoryStatusEx(ref m)) { r.MemoryValid = true; r.Load = m.Load; r.Total = m.Total / 1073741824.0; r.Used = (m.Total - m.Available) / 1073741824.0; }
        bool diskDue = DateTime.UtcNow >= nextDiskRead, cpuSeen = false, gpuSeen = false;
        var freshDisks = new List<Reading>();
        foreach (IHardware h in computer.Hardware)
        {
            string type = h.HardwareType.ToString();
            if (type == "Cpu")
            {
                Reading item = Read(h, "CPU");
                if (!cpuSeen || (item.Value.HasValue && (!r.Cpu.Value.HasValue || item.Value > r.Cpu.Value))) r.Cpu = item;
                cpuSeen = true;
            }
            else if (type.StartsWith("Gpu", StringComparison.Ordinal))
            {
                Reading item = Read(h, "GPU");
                if (!gpuSeen || (item.Value.HasValue && (!r.Gpu.Value.HasValue || item.Value > r.Gpu.Value))) r.Gpu = item;
                gpuSeen = true;
            }
            else if (type == "Storage" && diskDue) freshDisks.Add(Read(h, DiskKind(h)));
        }
        if (diskDue) { disks = freshDisks.OrderBy(d => d.Kind == "SSD" ? 0 : 1).ThenBy(d => d.Name).ToList(); nextDiskRead = DateTime.UtcNow.AddSeconds(15); }
        r.Disks = disks;
        if (!cpuSeen) r.Cpu.Name = "CPU tidak terdeteksi";
        if (!gpuSeen) r.Gpu.Name = "GPU tidak terdeteksi";
        return r;
    }
    public string Report(Snapshot s)
    {
        var b = new StringBuilder(); b.AppendLine("Mini Monitor 3.5.4 | " + s.Timestamp.ToString("s"));
        b.AppendLine("Administrator: " + Native.Admin + " | PawnIO installed: " + Native.DriverInstalled);
        b.AppendLine(String.Format(CultureInfo.InvariantCulture, "RAM: {0:0.00}/{1:0.00} GB ({2}%)", s.Used, s.Total, s.Load));
        foreach (Reading r in new[] { s.Cpu, s.Gpu }.Concat(s.Disks)) b.AppendLine(r.Kind + " | " + r.Name + " | " + r.Sensor + " | " + (r.Value.HasValue ? r.Value.Value.ToString("0.0", CultureInfo.InvariantCulture) + " C" : "unavailable") + " | Load " + (r.Load.HasValue ? r.Load.Value.ToString("0.0", CultureInfo.InvariantCulture) + "% (" + r.LoadSensor + ")" : "n/a") + " | " + r.Error);
        if (s.Error != null) b.AppendLine("Error: " + s.Error);
        return b.ToString();
    }
    public void Dispose() { if (computer != null) { try { computer.Close(); } catch { } computer = null; } }
}

sealed partial class MonitorWindow : Form
{
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    readonly System.Windows.Forms.Timer clockTimer = new System.Windows.Forms.Timer();
    readonly ActivityTracker activity = new ActivityTracker();
    readonly ChimeSchedule chimeSchedule = new ChimeSchedule();
    readonly SoftChimes chimes = new SoftChimes();
    readonly Stopwatch chimeClock = Stopwatch.StartNew();
    readonly ContextMenuStrip menu = new ContextMenuStrip();
    readonly NotifyIcon tray = new NotifyIcon();
    readonly Icon appIcon, trayIcon;
    readonly Font heading = new Font("Segoe UI", 9f, FontStyle.Bold), small = new Font("Segoe UI", 8.5f), value = new Font("Segoe UI", 17f, FontStyle.Bold), compact = new Font("Segoe UI", 11f, FontStyle.Bold);
    readonly Font clearClock = new Font("Segoe UI", 36f, FontStyle.Bold);
    readonly Font appFont = new Font("Segoe UI", 12f, FontStyle.Bold);
    readonly Font usageFont = new Font("Segoe UI", 10f, FontStyle.Bold);
    Color accent = Color.FromArgb(239, 52, 55), muted = Color.FromArgb(154, 154, 154);
    readonly string settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MiniMonitor", "MiniMonitor.settings");
    SensorReader reader;
    Snapshot data = new Snapshot();
    ToolStripMenuItem overlayItem, lockItem, topItem, startupItem, acknowledgeItem;
    ToolStripMenuItem clockSoundItem, appSoundItem, keepMenuItem, speedMenu, opacityMenu;
    bool keepMenuOpen = true, suppressMenuDismissal;
    bool clockSound = true, appSound = true;
    bool overlay = true, locked, busy, closing, mayClose, installing, lockHotkey, pin = true, sessionLocked, powerSuspended;
    int interval = 3000;
    float scale = 1;
    float dpiScale = 1, fontScale;
    readonly Dictionary<Font, Font> scaledFonts = new Dictionary<Font, Font>();
    Size windowSize;
    bool applyingLayout, windowInitialized, windowMaximized;
    double overlayOpacity = 1;
    DateTime lastPoll = DateTime.MinValue;
    string transient;
    public MonitorWindow(bool? overlayOverride)
    {
        Text = "Mini Monitor"; BackColor = Color.FromArgb(15, 15, 15); ForeColor = Color.FromArgb(238, 238, 234);
        DoubleBuffered = true; MaximizeBox = false; AutoScaleMode = AutoScaleMode.None;
        using (Graphics g = CreateGraphics()) scale = dpiScale = g.DpiX / 96f;
        ReadSettings(); if (overlayOverride.HasValue) overlay = overlayOverride.Value;
        SetTheme(theme, false);
        BuildMenu(); InitializeCooling(); ContextMenuStrip = menu;
        using (Stream iconStream = typeof(MonitorWindow).Assembly.GetManifestResourceStream("MiniMonitor.Icon"))
        using (var sourceIcon = new Icon(iconStream))
        {
            appIcon = new Icon(sourceIcon, System.Windows.Forms.SystemInformation.IconSize);
            trayIcon = new Icon(sourceIcon, System.Windows.Forms.SystemInformation.SmallIconSize);
        }
        Icon = appIcon; tray.Icon = trayIcon; tray.Text = ""; tray.ContextMenuStrip = menu; tray.Visible = true;
        tray.DoubleClick += (s, e) => RestoreWindow();
        timer.Interval = interval; timer.Tick += async (s, e) => await PollAsync();
        chimeSchedule.Rebase(DateTime.Now, chimeClock.Elapsed.TotalSeconds);
        clockTimer.Interval = 1000; clockTimer.Tick += (s, e) =>
        {
            activity.Poll(); UpdateFps();
            ChimeCue cue = chimeSchedule.Tick(DateTime.Now, chimeClock.Elapsed.TotalSeconds, activity.Suspended, activity.ActiveHourReached, clockSound, appSound);
            chimes.Play(cue); UpdateCooling(); Invalidate();
        };
        SystemEvents.SessionSwitch += SessionChanged; SystemEvents.PowerModeChanged += PowerChanged;
        MouseDown += (s, e) => { if (e.Button == MouseButtons.Left && !locked && !coolingButton.Bounds.Contains(e.Location) && !coolingShutdownButton.Bounds.Contains(e.Location) && !coolingSleepButton.Bounds.Contains(e.Location)) { Native.ReleaseCapture(); Native.SendMessage(Handle, 0xA1, new IntPtr(2), IntPtr.Zero); } };
        MouseDoubleClick += (s, e) => { if (overlay && e.Button == MouseButtons.Left && !locked) SetOverlay(false); };
        Resize += (s, e) => {
            if (overlay && WindowState == FormWindowState.Minimized) Hide();
            if (!applyingLayout && windowInitialized && !overlay && WindowState != FormWindowState.Minimized) {
                windowMaximized = WindowState == FormWindowState.Maximized;
                if (!windowMaximized) windowSize = ClientSize;
            }
            Invalidate();
        };
        ResizeEnd += (s, e) => SaveSettings();
        Shown += async (s, e) => { activity.Poll(); clockTimer.Start(); await PollAsync(); if (!closing) timer.Start(); };
        FormClosing += HandleClosing; ApplyLayout();
        var area = Screen.FromPoint(Location).WorkingArea;
        if (WindowState == FormWindowState.Normal && !area.Contains(Bounds)) Location = new Point(Math.Max(area.Left, Math.Min(Left, area.Right - Width)), Math.Max(area.Top, Math.Min(Top, area.Bottom - Height)));
    }
    int S(int n) { return (int)Math.Round(n * scale); }
    protected override bool ShowWithoutActivation { get { return overlay; } }
    void SessionChanged(object sender, SessionSwitchEventArgs e)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(new Action(() => { if (e.Reason == SessionSwitchReason.SessionLock) sessionLocked = true; if (e.Reason == SessionSwitchReason.SessionUnlock) sessionLocked = false; activity.Pause(sessionLocked || powerSuspended); chimeSchedule.Rebase(DateTime.Now, chimeClock.Elapsed.TotalSeconds); })); } catch (InvalidOperationException) { }
    }
    void PowerChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (e.Mode == PowerModes.Suspend) CancelCooling();
        try { BeginInvoke(new Action(() => { if (e.Mode == PowerModes.Suspend) powerSuspended = true; if (e.Mode == PowerModes.Resume) powerSuspended = false; activity.Pause(sessionLocked || powerSuspended); chimeSchedule.Rebase(DateTime.Now, chimeClock.Elapsed.TotalSeconds); })); } catch (InvalidOperationException) { }
    }
    void BuildMenu()
    {
        menu.ShowItemToolTips = false;
        menu.Items.Add("Tampilkan / buka kunci", null, (s, e) => RestoreWindow());
        overlayItem = Setting("Mode overlay (Ctrl+Alt+O)", (s, e) => SetOverlay(!overlay)); menu.Items.Add(overlayItem);
        lockItem = Setting("Klik tembus (Ctrl+Alt+L)", (s, e) => SetLocked(!locked)); menu.Items.Add(lockItem);
        topItem = Setting("Selalu di atas", (s, e) => { pin = !pin; TopMost = overlay || pin; SaveSettings(); }); menu.Items.Add(topItem);
        keepMenuItem = Setting("Menu tetap terbuka", (s, e) => { keepMenuOpen = !keepMenuOpen; SaveSettings(); }); menu.Items.Add(keepMenuItem);
        themeMenu = new ToolStripMenuItem("Tema");
        foreach (MonitorTheme selected in Enum.GetValues(typeof(MonitorTheme)))
        {
            MonitorTheme choice = selected;
            var item = Setting(ThemeCatalog.Labels[(int)choice], (s, e) => SetTheme(choice, true));
            item.Tag = choice; themeMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(themeMenu); BuildFpsMenu();
        menu.Items.Add(new ToolStripSeparator());
        acknowledgeItem = Setting("Sudah istirahat · ingatkan 60 menit berikutnya", (s, e) => { if (activity.Selected != null) activity.Selected.Acknowledge(); Invalidate(); }); menu.Items.Add(acknowledgeItem);
        menu.Items.Add(Setting("Reset waktu aktif aplikasi ini", (s, e) => { activity.ResetSelected(); Invalidate(); }));
        var soundMenu = new ToolStripMenuItem("Suara pengingat");
        clockSoundItem = Setting("Pergantian jam · 2 nada", (s, e) => { clockSound = !clockSound; SaveSettings(); });
        appSoundItem = Setting("Tiap 1 jam aktif · 4 nada", (s, e) => { appSound = !appSound; SaveSettings(); });
        soundMenu.DropDownItems.Add(clockSoundItem); soundMenu.DropDownItems.Add(appSoundItem);
        soundMenu.DropDownItems.Add(new ToolStripSeparator());
        soundMenu.DropDownItems.Add(Setting("Coba bunyi pergantian jam", (s, e) => chimes.Play(ChimeCue.ClockHour)));
        soundMenu.DropDownItems.Add(Setting("Coba bunyi 1 jam aktif", (s, e) => chimes.Play(ChimeCue.ActiveHour)));
        menu.Items.Add(soundMenu);
        startupItem = Setting("Muncul saat login Windows", (s, e) =>
        {
            try { StartupIntegration.Toggle(); } catch (Exception ex) { MessageBox.Show(this, ex.GetBaseException().Message, Text); }
            startupItem.Checked = StartupIntegration.IsEnabled();
        }); menu.Items.Add(startupItem);
        menu.Items.Add(new ToolStripSeparator());
        speedMenu = new ToolStripMenuItem("Pembaruan");
        foreach (int seconds in new[] { 1, 3, 5 })
        {
            int ms = seconds * 1000; var item = Setting(seconds + " detik" + (seconds == 3 ? " (default)" : ""), (s, e) => { interval = ms; timer.Interval = interval; SaveSettings(); Invalidate(); });
            item.Tag = ms; speedMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(speedMenu);
        opacityMenu = new ToolStripMenuItem("Opasitas overlay");
        foreach (int percent in new[] { 75, 90, 100 })
        {
            double opacity = percent / 100.0;
            var item = Setting(percent + "%", (s, e) => { overlayOpacity = opacity; if (overlay) Opacity = opacity; SaveSettings(); });
            item.Tag = opacity; opacityMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(opacityMenu); menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Perbaiki / pasang driver CPU...", null, async (s, e) => await InstallDriver());
        menu.Items.Add("Mulai ulang sebagai administrator", null, (s, e) => Restart());
        menu.Items.Add("Simpan diagnostik...", null, (s, e) => SaveReport()); menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Keluar", null, (s, e) => Close());
        ConfigureMenuBehavior(menu);
        menu.Opening += (s, e) => { fpsMenuApp = activity.Selected == null ? null : activity.Selected.App; startupItem.Checked = StartupIntegration.IsEnabled(); RefreshMenuState(); };
        menu.Closed += (s, e) =>
        {
            if (!closing && IsHandleCreated) BeginInvoke(new Action(() => { if (!closing && !menu.Visible) ApplyLayout(); }));
        };
    }
    void ConfigureMenuBehavior(ToolStripDropDown dropdown)
    {
        dropdown.ShowItemToolTips = false;
        // ItemClicked runs before the setting's Click callback. Compute the
        // keep-open toggle's new state before WinForms tries to dismiss it.
        dropdown.ItemClicked += (s, e) => suppressMenuDismissal = e.ClickedItem == keepMenuItem ? !keepMenuOpen : keepMenuOpen && e.ClickedItem is KeepOpenMenuItem;
        dropdown.Closing += (s, e) =>
        {
            if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked && suppressMenuDismissal) e.Cancel = true;
        };
        foreach (ToolStripMenuItem item in dropdown.Items.OfType<ToolStripMenuItem>())
            if (item.HasDropDownItems) ConfigureMenuBehavior(item.DropDown);
    }
    ToolStripMenuItem Setting(string text, EventHandler click)
    {
        return new KeepOpenMenuItem(text, click, RefreshMenuState);
    }
    void RefreshMenuState()
    {
        overlayItem.Checked = overlay; lockItem.Checked = locked; lockItem.Enabled = overlay && lockHotkey;
        topItem.Checked = TopMost; topItem.Enabled = !overlay; keepMenuItem.Checked = keepMenuOpen;
        clockSoundItem.Checked = clockSound; appSoundItem.Checked = appSound;
        RefreshFpsMenu();
        acknowledgeItem.Enabled = activity.Selected != null && activity.Selected.ReminderDue;
        foreach (ToolStripMenuItem item in speedMenu.DropDownItems) item.Checked = (int)item.Tag == interval;
        foreach (ToolStripMenuItem item in opacityMenu.DropDownItems) item.Checked = Math.Abs((double)item.Tag - overlayOpacity) < .001;
        foreach (ToolStripMenuItem item in themeMenu.DropDownItems) item.Checked = (MonitorTheme)item.Tag == theme;
    }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); ApplyCaptionTheme(); lockHotkey = Native.RegisterHotKey(Handle, 1, 0x4003, (uint)Keys.L); Native.RegisterHotKey(Handle, 2, 0x4003, (uint)Keys.O); }
    void ApplyCaptionTheme()
    {
        if (!IsHandleCreated) return;
        try {
            int dark = theme == MonitorTheme.DotBlack || theme == MonitorTheme.GShock ? 1 : 0;
            int background = ColorTranslator.ToWin32(BackColor);
            int foreground = ColorTranslator.ToWin32(theme == MonitorTheme.GShock ? Color.FromArgb(238, 238, 234) : ForeColor);
            Native.DwmSetWindowAttribute(Handle, 20, ref dark, 4);
            Native.DwmSetWindowAttribute(Handle, 35, ref background, 4);
            Native.DwmSetWindowAttribute(Handle, 36, ref foreground, 4);
        } catch (DllNotFoundException) { } catch (EntryPointNotFoundException) { }
    }
    protected override void OnHandleDestroyed(EventArgs e) { Native.UnregisterHotKey(Handle, 1); Native.UnregisterHotKey(Handle, 2); base.OnHandleDestroyed(e); }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x312) { if (m.WParam.ToInt32() == 1) { if (locked) RestoreWindow(); else if (overlay) SetLocked(true); } else if (m.WParam.ToInt32() == 2) { Show(); SetOverlay(!overlay); } }
        base.WndProc(ref m);
    }
    void SetLocked(bool enabled)
    {
        locked = enabled && overlay && lockHotkey; int style = Native.GetWindowLong(Handle, -20);
        int wanted = locked ? style | 0x80000 | 0x20 | 0x08000000 : style & ~0x20 & ~0x08000000;
        if (style != wanted) Native.SetWindowLong(Handle, -20, wanted);
        Invalidate();
    }
    void RestoreWindow() { SetLocked(false); Show(); WindowState = FormWindowState.Normal; Activate(); }
    void SetOverlay(bool enabled)
    {
        if (enabled == overlay) return;
        SetLocked(false);
        applyingLayout = true;
        if (!overlay) { windowMaximized = WindowState == FormWindowState.Maximized; if (WindowState == FormWindowState.Normal) windowSize = ClientSize; }
        WindowState = FormWindowState.Normal;
        overlay = enabled; windowInitialized = false;
        applyingLayout = false;
        ApplyLayout(); SaveSettings(); Invalidate();
    }
    int ContentHeight { get { return 354 + Math.Max(1, data.Disks.Count) * 23 + (lastPoll == DateTime.MinValue || !data.Cpu.Value.HasValue || transient != null ? 18 : 0); } }
    void ApplyLayout()
    {
        applyingLayout = true;
        scale = dpiScale;
        Point old = Location; var wanted = overlay ? FormBorderStyle.None : FormBorderStyle.Sizable;
        if (FormBorderStyle != wanted) FormBorderStyle = wanted;
        MaximizeBox = !overlay;
        // TopMost's setter calls SetWindowPos even when its value is unchanged.
        // Repeating it on every sensor poll activates the form and dismisses
        // NotifyIcon's menu. Background refresh must not change window focus.
        if (ShowInTaskbar != !overlay) ShowInTaskbar = !overlay;
        if (TopMost != (overlay || pin)) TopMost = overlay || pin;
        double wantedOpacity = overlay ? overlayOpacity : 1;
        if (Opacity != wantedOpacity) Opacity = wantedOpacity;
        MinimumSize = overlay ? Size.Empty : SizeFromClientSize(new Size(S(312), S(300)));
        if (overlay) {
            Size size = new Size(S(312), S(ContentHeight));
            if (ClientSize != size) ClientSize = size;
        } else if (!windowInitialized) {
            ClientSize = windowSize.IsEmpty ? new Size(S(420), S(ContentHeight)) : windowSize;
            windowInitialized = true;
            if (windowMaximized) WindowState = FormWindowState.Maximized;
        }
        if (WindowState == FormWindowState.Normal && Location != old) Location = old;
        if (locked) SetLocked(true);
        applyingLayout = false;
    }
    async Task PollAsync()
    {
        if (busy || closing || installing) return; busy = true;
        try
        {
            Snapshot fresh = await Task.Run(() => { if (reader == null) reader = new SensorReader(); return reader.Poll(); });
            if (!closing)
            {
                transient = null; data = fresh; lastPoll = DateTime.Now;
                // Keep menu interaction stable even when sensors appearing or
                // disappearing change the required window size. Values and the
                // clock keep painting; deferred geometry applies after closing.
                if (!menu.Visible) ApplyLayout();
                Invalidate();
            }
        }
        catch (Exception ex) { transient = "Sensor: " + ex.GetBaseException().Message; data = new Snapshot { Error = transient }; if (!closing) Invalidate(); }
        finally { busy = false; }
        if (closing) await FinishClose();
    }
    string StatusText()
    {
        if (closing) return "Menutup sensor..."; if (installing) return "Memasang driver CPU..."; if (transient != null) return transient;
        if (lastPoll == DateTime.MinValue) return "Membaca perangkat...";
        if (!data.Cpu.Value.HasValue) { if (!Native.DriverInstalled) return "CPU: klik kanan > pasang driver"; if (!Native.Admin) return "CPU: jalankan sebagai administrator"; return "CPU belum terbaca · lihat diagnostik"; }
        if (data.Disks.Count == 0 || data.Disks.All(d => !d.Value.HasValue)) return "SSD belum terbaca · lihat diagnostik";
        return locked ? "TERKUNCI  /  CTRL + ALT + L" : "GESER  /  KLIK KANAN UNTUK OPSI";
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); Graphics g = e.Graphics;
        if (!overlay && ClientSize.Width >= 680 * dpiScale && ClientSize.Width >= ClientSize.Height * 2) { PaintWide(g); return; }
        scale = overlay ? dpiScale : Math.Max(.5f, Math.Min(ClientSize.Width / 420f, ClientSize.Height / (float)ContentHeight));
        using (var pen = new Pen(frame)) g.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        int width = (int)(ClientSize.Width / scale);
        DateTime now = DateTime.Now;
        if (theme == MonitorTheme.KanjiArt) PaintKanji(g, now, width);
        else if (theme == MonitorTheme.GShock) PaintGShock(g, now, width);
        else PaintDot(g, now, width);
        PaintDisks(g, width);
    }
    void PaintWide(Graphics g)
    {
        if (theme != MonitorTheme.GShock) { PaintDesignedWide(g); return; }
        int rows = Math.Max(1, data.Disks.Count);
        scale = Math.Min(ClientSize.Width / 900f, ClientSize.Height / (float)Math.Max(240, 128 + rows * 26));
        int width = (int)(ClientSize.Width / scale), height = (int)(ClientSize.Height / scale);
        bool watch = theme == MonitorTheme.GShock;
        if (watch) using (var lcd = new SolidBrush(Color.FromArgb(189, 197, 166))) g.FillRectangle(lcd, 0, 0, ClientSize.Width, ClientSize.Height);
        using (var pen = new Pen(rule)) {
            g.DrawLine(pen, S(260), S(20), S(260), S(height - 20));
            g.DrawLine(pen, S(560), S(20), S(560), S(height - 20));
        }
        DateTime now = DateTime.Now;
        DrawText(g, watch ? "G-SHOCK / SYSTEM" : theme == MonitorTheme.KanjiArt ? "時間 / TIME SYSTEM" : "MINI / MONITOR", heading, accent, new Rectangle(20, 17, 225, 25));
        if (watch) SegmentClock.Draw(g, now.ToString("HH:mm"), 20, 66, 54, ForeColor, track, scale);
        else if (theme == MonitorTheme.KanjiArt) DrawText(g, now.ToString("HH:mm"), posterTime, ForeColor, new Rectangle(17, 58, 225, 68));
        else if (theme == MonitorTheme.DotWhite) DrawText(g, now.ToString("HH:mm"), clearClock, accent, new Rectangle(15, 54, 237, 78));
        else DotClock.Draw(g, now.ToString("HH:mm"), 20, 66, 7.6f, ForeColor, scale);
        PlaceSleepButton(184, 132, 28);
        PlaceShutdownButton(220, 132, 28);
        DrawText(g, now.ToString("ss") + " SEC", compact, accent, new Rectangle(20, 132, 190, 28));
        DrawText(g, now.ToString("ddd, dd MMM yyyy", CultureInfo.GetCultureInfo("id-ID")).ToUpperInvariant(), small, muted, new Rectangle(20, 173, 225, 26));
        AppSession current = activity.Selected;
        bool reminder = current != null && current.ReminderDue;
        DrawText(g, ActivityLabel(current), small, muted, new Rectangle(280, 17, 260, 25));
        DrawText(g, current == null ? "Buka game atau aplikasi" : current.App.Name, appFont, ForeColor, new Rectangle(280, 47, 260, 30));
        string duration = ActivityTracker.Duration(current == null ? 0 : current.ActiveSeconds);
        if (theme == MonitorTheme.DotBlack) DotClock.Draw(g, duration, 282, 92, duration.Length > 8 ? 3.4f : 4.6f, reminder ? accent : ForeColor, scale);
        else DrawText(g, duration, monoTime, reminder ? accent : ForeColor, new Rectangle(280, 86, 260, 42));
        double opened = current == null ? -1 : ActivityTracker.OpenDuration(current.App, DateTime.UtcNow);
        DrawText(g, "DIBUKA  " + (opened < 0 ? "—" : ActivityTracker.Duration(opened)), small, muted, new Rectangle(280, 132, 260, 26));
        if (fpsEnabled && current != null && fpsRules.Tracks(current.App)) DrawText(g, (fpsValue.HasValue ? Math.Round(fpsValue.Value).ToString("0") : "—") + " FPS", compact, accent, new Rectangle(280, 166, 260, 27));
        using (var background = new SolidBrush(track)) g.FillRectangle(background, S(280), S(203), S(260), S(3));
        if (current != null) using (var fill = new SolidBrush(accent)) g.FillRectangle(fill, S(280), S(203), S((int)(260 * current.ReminderProgress)), S(3));
        DrawText(g, reminder ? "WAKTUNYA ISTIRAHAT" : "PENGINGAT / 60 MENIT AKTIF", small, reminder || theme == MonitorTheme.DotWhite ? accent : muted, new Rectangle(280, 210, 260, 24));
        int col = (width - 600) / 3;
        string[] labels = { "RAM", "CPU", "GPU" };
        string[] loads = { data.MemoryValid ? data.Load + "%" : "—", data.Cpu.Load.HasValue ? data.Cpu.Load.Value.ToString("0") + "%" : "—", data.Gpu.Load.HasValue ? data.Gpu.Load.Value.ToString("0") + "%" : "—" };
        for (int i = 0; i < 3; i++) {
            int x = 580 + i * col;
            DrawText(g, labels[i], heading, muted, new Rectangle(x, 17, col, 25));
            DrawText(g, loads[i], value, ForeColor, new Rectangle(x, 47, col, 36));
            DrawText(g, i == 0 ? data.Used.ToString("0.0") + " GB" : i == 1 ? data.Cpu.Text : data.Gpu.Text, compact, muted, new Rectangle(x, 85, i == 2 ? col - 30 : col, 27));
            if (i == 2) PlaceCoolingButton(x + col - 28, 85, 26);
        }
        int y = 128, index = 1;
        foreach (Reading disk in data.Disks) {
            DrawText(g, disk.Kind + " " + index++ + "  " + disk.Name, small, muted, new Rectangle(580, y, width - 675, 25));
            DrawText(g, disk.Text, compact, ForeColor, new Rectangle(width - 90, y, 70, 25), true); y += 26;
        }
        if (data.Disks.Count == 0) DrawText(g, "SSD  —", small, muted, new Rectangle(580, y, width - 600, 25));
        if (lastPoll == DateTime.MinValue || !data.Cpu.Value.HasValue || transient != null) DrawText(g, StatusText(), small, muted, new Rectangle(580, height - 27, width - 600, 24));
    }
    void PaintDot(Graphics g, DateTime now, int width)
    {
        using (var red = new SolidBrush(accent)) g.FillEllipse(red, S(18), S(17), S(6), S(6));
        DrawText(g, "MINI / MONITOR", heading, theme == MonitorTheme.DotWhite ? accent : ForeColor, new Rectangle(30, 9, 170, 22));
        DrawText(g, "( 03 )", small, muted, new Rectangle(width - 76, 9, 58, 22), true);
        if (theme == MonitorTheme.DotWhite) DrawText(g, now.ToString("HH:mm"), clearClock, accent, new Rectangle(13, 30, 237, 75));
        else DotClock.Draw(g, now.ToString("HH:mm"), 19, 43, 7.6f, ForeColor, scale);
        DotClock.Draw(g, now.ToString("ss"), width - 55, 68, 3.1f, accent, scale);
        PlaceSleepButton(width - 82, 94, 28);
        PlaceShutdownButton(width - 48, 94, 28);
        DrawText(g, now.ToString("ddd, dd MMM yyyy", CultureInfo.GetCultureInfo("id-ID")).ToUpperInvariant(), small, muted, new Rectangle(18, 99, width - 76, 20));
        Divider(g, 126, width);
        AppSession current = activity.Selected;
        bool reminder = current != null && current.ReminderDue;
        DrawText(g, activity.IsActive ? "APLIKASI AKTIF" : current == null ? "MENUNGGU APLIKASI" : "JEDA / DI LATAR BELAKANG", small, muted, new Rectangle(18, 135, width - 36, 18));
        PaintAppWithFps(g, current, appFont, 154, width);
        string duration = ActivityTracker.Duration(current == null ? 0 : current.ActiveSeconds);
        // Duration has a smaller dot grid; the main clock remains the visual focus.
        if (theme == MonitorTheme.DotWhite) DrawText(g, duration, monoTime, reminder ? accent : ForeColor, new Rectangle(16, 180, width - 85, 38));
        else DotClock.Draw(g, duration, 20, 188, duration.Length > 8 ? 2.8f : 3.6f, reminder ? accent : ForeColor, scale);
        DrawText(g, "AKTIF", small, muted, new Rectangle(width - 63, 190, 45, 22), true);
        double opened = current == null ? -1 : ActivityTracker.OpenDuration(current.App, DateTime.UtcNow);
        DrawText(g, "SEJAK DIBUKA   " + (opened < 0 ? "—" : ActivityTracker.Duration(opened)), small, muted, new Rectangle(18, 218, width - 36, 21));
        using (var faint = new SolidBrush(track))
        using (var ink = new SolidBrush(reminder ? accent : ForeColor))
        {
            double progress = current == null ? 0 : current.ReminderProgress;
            float step = (width - 36f) / 30;
            for (int i = 0; i < 30; i++) g.FillRectangle(i < progress * 30 ? ink : faint, (18 + i * step) * scale, S(247), Math.Max(1, (step - 3) * scale), S(3));
        }
        DrawText(g, reminder ? "60 MENIT+ / WAKTUNYA ISTIRAHAT" : "PENGINGAT ISTIRAHAT / 60 MENIT", small, reminder || theme == MonitorTheme.DotWhite ? accent : muted, new Rectangle(18, 254, width - 36, 20));
        Divider(g, 282, width);
        int ramWidth = 48, col = (width - 36 - ramWidth - 20) / 2;
        Metric(g, "RAM", data.MemoryValid ? data.Load + "%" : "—", 18, ramWidth, false);
        Metric(g, "CPU", data.Cpu.UsageAndTemperature, 18 + ramWidth + 10, col, true);
        Metric(g, "GPU", data.Gpu.UsageAndTemperature, 18 + ramWidth + 20 + col, col, true);
    }
    void PaintDisks(Graphics g, int width)
    {
        int y = 343, ssd = 1, hdd = 1;
        if (data.Disks.Count == 0) DrawText(g, "SSD   —", small, muted, new Rectangle(18, y, width - 36, 21));
        foreach (Reading disk in data.Disks)
        {
            string label = disk.Kind + " " + (disk.Kind == "SSD" ? ssd++ : hdd++);
            DrawText(g, label, small, muted, new Rectangle(18, y, 53, 21));
            DrawText(g, disk.Name, small, muted, new Rectangle(73, y, width - 155, 21));
            DrawText(g, disk.Text, heading, ForeColor, new Rectangle(width - 77, y, 59, 21), true); y += 23;
        }
        // Only show actionable errors; normal operation has no hover text.
        if (lastPoll == DateTime.MinValue || !data.Cpu.Value.HasValue || transient != null)
            DrawText(g, StatusText(), small, muted, new Rectangle(18, (int)(ClientSize.Height / scale) - (theme == MonitorTheme.GShock ? 27 : 20), width - 36, 18));
    }
    void Divider(Graphics g, int y, int width) { using (var p = new Pen(rule)) g.DrawLine(p, S(18), S(y), S(width - 18), S(y)); }
    void Metric(Graphics g, string label, string reading, int x, int width, bool usage)
    {
        if (label == "GPU") {
            DrawText(g, label, small, muted, new Rectangle(x, 291, 35, 19));
            DrawText(g, data.Gpu.Load.HasValue ? data.Gpu.Load.Value.ToString("0") + "%" : "—", usageFont, ForeColor, new Rectangle(x + 35, 291, width - 35, 19), true);
            DrawText(g, data.Gpu.Text, usageFont, ForeColor, new Rectangle(x, 310, width - 28, 25));
            PlaceCoolingButton(x + width - 26, 309, 24);
        } else { DrawText(g, label, small, muted, new Rectangle(x, 291, width, 19)); DrawText(g, reading, usage ? usageFont : compact, ForeColor, new Rectangle(x, 310, width, 25)); }
    }
    void DrawText(Graphics g, string text, Font font, Color color, Rectangle r, bool right = false)
    {
        var rect = new Rectangle(S(r.X), S(r.Y), S(r.Width), S(r.Height));
        TextRenderer.DrawText(g, text ?? "", DisplayFont(font), rect, color, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | (right ? TextFormatFlags.Right : TextFormatFlags.Left));
    }
    Font DisplayFont(Font font)
    {
        float factor = scale / dpiScale;
        if (Math.Abs(fontScale - factor) > .0001f) { ClearScaledFonts(); fontScale = factor; }
        if (Math.Abs(factor - 1) < .0001f) return font;
        Font result;
        if (!scaledFonts.TryGetValue(font, out result)) { result = new Font(font.FontFamily, font.Size * factor, font.Style, font.Unit); scaledFonts.Add(font, result); }
        return result;
    }
    void ClearScaledFonts() { foreach (Font f in scaledFonts.Values) f.Dispose(); scaledFonts.Clear(); }
    void DrawRow(Graphics g, string name, string reading, string detail, int y, float? temp, float? fraction)
    {
        int width = (int)(ClientSize.Width / scale); Color color = temp.HasValue && temp >= 85 ? Color.FromArgb(255, 168, 113) : ForeColor;
        if (overlay) { DrawText(g, name, heading, muted, new Rectangle(14, y, 57, 26)); DrawText(g, detail, small, muted, new Rectangle(73, y, width - 165, 26)); DrawText(g, reading, compact, color, new Rectangle(width - 85, y, 71, 26), true); }
        else
        {
            DrawText(g, name, heading, accent, new Rectangle(18, y, 70, 23)); DrawText(g, detail, small, muted, new Rectangle(18, y + 22, width - 134, 24)); DrawText(g, reading, value, color, new Rectangle(width - 124, y, 106, 44), true);
            if (fraction.HasValue) { using (var b = new SolidBrush(Color.FromArgb(39, 52, 65))) g.FillRectangle(b, S(18), S(y + 50), S(width - 36), S(2)); using (var b = new SolidBrush(accent)) g.FillRectangle(b, S(18), S(y + 50), S((int)((width - 36) * fraction.Value)), S(2)); }
        }
    }
    async Task InstallDriver()
    {
        if (installing || busy) { transient = "Tunggu pembacaan selesai, lalu coba lagi"; Invalidate(); return; }
        string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "setup", "PawnIO_setup.exe");
        if (!File.Exists(path)) { MessageBox.Show(this, "Ekstrak seluruh ZIP agar installer driver tersedia.", Text); return; }
        installing = true; timer.Stop(); transient = null; Invalidate();
        try
        {
            using (var p = Process.Start(new ProcessStartInfo(path, "-install") { UseShellExecute = true, Verb = "runas" })) { await Task.Run(() => p.WaitForExit()); if (p.ExitCode != 0) throw new Exception("Installer berhenti dengan kode " + p.ExitCode); }
            if (!Native.DriverInstalled) throw new Exception("PawnIO belum terpasang"); if (!closing) Restart();
        }
        catch (Exception ex) { transient = "Pemasangan belum selesai"; if (!closing) MessageBox.Show(this, ex.GetBaseException().Message, Text); }
        finally { installing = false; if (!closing) { timer.Start(); Invalidate(); } }
    }
    void Restart()
    {
        try { SaveSettings(); Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--elevated --wait-for " + Process.GetCurrentProcess().Id) { UseShellExecute = true, Verb = "runas" }); Close(); }
        catch (Exception ex) { transient = "Akses administrator belum diberikan"; MessageBox.Show(this, ex.Message, Text); }
    }
    void SaveReport()
    {
        using (var dialog = new SaveFileDialog { Filter = "Text file|*.txt", FileName = "MiniMonitor-diagnostik.txt" })
            if (dialog.ShowDialog(this) == DialogResult.OK) try { File.WriteAllText(dialog.FileName, (reader != null ? reader.Report(data) : "Sensor belum selesai dibuka.") + Environment.NewLine + "FPS enabled: " + fpsEnabled + " | " + fpsReader.Status + " | FPS " + (fpsValue.HasValue ? fpsValue.Value.ToString("0.0", CultureInfo.InvariantCulture) : "n/a")); } catch (Exception ex) { MessageBox.Show(this, ex.Message, Text); }
    }
    void ReadSettings()
    {
        StartPosition = FormStartPosition.Manual; var area = Screen.PrimaryScreen.WorkingArea; Location = new Point(area.Right - S(320), area.Top + S(30));
        try
        {
            string loadPath = File.Exists(settingsPath) ? settingsPath : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MiniMonitor.settings");
            if (!File.Exists(loadPath)) return;
            foreach (string line in File.ReadAllLines(loadPath))
            {
                string[] p = line.Split(new[] { '=' }, 2); if (p.Length != 2) continue; int n; double d;
                if (p[0] == "overlay") overlay = p[1] == "1"; if (p[0] == "pin") pin = p[1] == "1";
                if (p[0] == "windowWidth" && int.TryParse(p[1], out n) && n >= 200 && n <= 16000) windowSize.Width = n;
                if (p[0] == "windowHeight" && int.TryParse(p[1], out n) && n >= 200 && n <= 16000) windowSize.Height = n;
                if (p[0] == "windowMaximized") windowMaximized = p[1] == "1";
                if (p[0] == "clockSound") clockSound = p[1] == "1"; if (p[0] == "appSound") appSound = p[1] == "1";
                if (p[0] == "keepMenuOpen") keepMenuOpen = p[1] == "1";
                if (p[0] == "theme") theme = ThemeCatalog.Parse(p[1]);
                if (p[0] == "fpsEnabled") fpsEnabled = p[1] == "1";
                if (p[0] == "fpsApps") fpsRules.Restore(p[1]);
                if (p[0] == "interval" && int.TryParse(p[1], out n) && (n == 1000 || n == 3000 || n == 5000)) interval = n;
                if (p[0] == "x" && int.TryParse(p[1], out n)) Left = n; if (p[0] == "y" && int.TryParse(p[1], out n)) Top = n;
                if (p[0] == "opacity" && double.TryParse(p[1], NumberStyles.Any, CultureInfo.InvariantCulture, out d) && d >= .5 && d <= 1) overlayOpacity = d;
            }
        }
        catch { }
    }
    void SaveSettings() { try { Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)); File.WriteAllLines(settingsPath, new[] { "windowWidth=" + windowSize.Width, "windowHeight=" + windowSize.Height, "windowMaximized=" + (windowMaximized ? "1" : "0"), "overlay=" + (overlay ? "1" : "0"), "pin=" + (pin ? "1" : "0"), "interval=" + interval, "x=" + Left, "y=" + Top, "opacity=" + overlayOpacity.ToString(CultureInfo.InvariantCulture), "clockSound=" + (clockSound ? "1" : "0"), "appSound=" + (appSound ? "1" : "0"), "keepMenuOpen=" + (keepMenuOpen ? "1" : "0"), "theme=" + ThemeCatalog.Keys[(int)theme], "fpsEnabled=" + (fpsEnabled ? "1" : "0"), "fpsApps=" + fpsRules.Serialize() }); } catch { } }
    async void HandleClosing(object sender, FormClosingEventArgs e)
    {
        if (mayClose) return; e.Cancel = true; if (closing) return; closing = true; timer.Stop(); clockTimer.Stop(); SaveSettings(); Invalidate(); if (!busy) await FinishClose();
    }
    async Task FinishClose() { if (mayClose) return; CancelCooling(); if (coolingTask != null) await Task.WhenAny(coolingTask, Task.Delay(3000)); await fpsReader.StopAsync(); if (reader != null) { SensorReader old = reader; reader = null; await Task.Run(() => old.Dispose()); } mayClose = true; Close(); }
    protected override void Dispose(bool disposing) { if (disposing) { SystemEvents.SessionSwitch -= SessionChanged; SystemEvents.PowerModeChanged -= PowerChanged; ClearScaledFonts(); timer.Dispose(); clockTimer.Dispose(); chimes.Dispose(); fpsReader.Dispose(); tray.Visible = false; tray.Dispose(); menu.Dispose(); if (appIcon != null) appIcon.Dispose(); if (trayIcon != null) trayIcon.Dispose(); clearClock.Dispose(); heading.Dispose(); small.Dispose(); value.Dispose(); compact.Dispose(); appFont.Dispose(); usageFont.Dispose(); DisposeThemeFonts(); DisposeWideFonts(); } base.Dispose(disposing); }
    internal void Render(Snapshot sample, bool compactMode, string path)
    {
        data = sample; lastPoll = DateTime.Now; overlay = compactMode; windowInitialized = false; activity.Poll(); ApplyLayout();
        using (var bitmap = new Bitmap(Width, Height)) { DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(path); }
    }
}

static class Program
{
    internal static string FpsSessionName;
    [STAThread] static int Main(string[] args)
    {
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        try
        {
            if (args.Length > 0 && args[0] == "--fan-boost") {
                try {
                    if (args.Length != 5) return 2;
                    return GpuFanBoost.RunHelper(Encoding.UTF8.GetString(Convert.FromBase64String(args[2])), int.Parse(args[1], CultureInfo.InvariantCulture), Encoding.UTF8.GetString(Convert.FromBase64String(args[3])), Encoding.UTF8.GetString(Convert.FromBase64String(args[4])));
                } catch { return 2; }
            }
            if (args.Length == 3 && args[0] == "--fan-probe") { File.WriteAllText(args[2], GpuFanBoost.Probe(args[1])); return 0; }
            if (args.Length >= 2 && args[0] == "--probe")
            {
                using (var reader = new SensorReader())
                {
                    Snapshot sample = reader.Poll(); System.Threading.Thread.Sleep(1500); sample = reader.Poll(); File.WriteAllText(args[1], reader.Report(sample));
                    if (args.Length >= 3) using (var form = new MonitorWindow(null)) { form.Render(sample, true, Path.Combine(args[2], "overlay-preview.png")); form.Render(sample, false, Path.Combine(args[2], "window-preview.png")); }
                }
                return 0;
            }
            if (!Native.Admin && !args.Contains("--no-elevate") && !args.Contains("--elevated") && !args.Contains("--startup"))
            {
                try { Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--elevated " + String.Join(" ", args)) { UseShellExecute = true, Verb = "runas" }); return 0; }
                catch (System.ComponentModel.Win32Exception) { }
            }
            bool? mode = args.Contains("--window") ? (bool?)false : args.Contains("--overlay") ? (bool?)true : null;
            int waitIndex = Array.IndexOf(args, "--wait-for"), waitPid;
            if (waitIndex >= 0 && waitIndex + 1 < args.Length && int.TryParse(args[waitIndex + 1], out waitPid))
                try { using (var previous = Process.GetProcessById(waitPid)) previous.WaitForExit(15000); } catch (ArgumentException) { }
            bool created;
            using (var mutex = new System.Threading.Mutex(true, "Local\\" + StartupIntegration.TaskName, out created))
            {
                if (!created) return 0;
                FpsSessionName = "MiniMonitor-Frames-" + StartupIntegration.TaskName + "-" + Process.GetCurrentProcess().SessionId;
                try { Application.Run(new MonitorWindow(mode)); } finally { mutex.ReleaseMutex(); }
            }
            return 0;
        }
        catch (Exception ex)
        {
            if (args.Length >= 2 && args[0] == "--probe") File.WriteAllText(args[1], "ERROR: " + ex); else MessageBox.Show(ex.GetBaseException().Message, "Mini Monitor"); return 1;
        }
    }
}
