using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

sealed class CoolingButton : Button
{
    public Color Ink, Surface, ActiveSurface;
    public bool Running;
    public bool PowerSymbol;
    public int SecondsLeft;
    public CoolingButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
        TabStop = true; AccessibleRole = AccessibleRole.PushButton;
        AccessibleName = "Dinginkan GPU: kipas 100 persen selama 15 detik";
        Cursor = Cursors.Hand;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics; g.Clear(Running ? ActiveSurface : Surface);
        Color ink = Enabled ? (Running ? Color.White : Ink) : SystemColors.GrayText;
        if (Running) TextRenderer.DrawText(g, SecondsLeft > 0 ? SecondsLeft.ToString() : "…", Font, ClientRectangle, ink,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        else if (PowerSymbol) {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float cx = Width / 2f, cy = Height / 2f, radius = Math.Min(Width, Height) * .28f;
            using (var pen = new Pen(ink, Math.Max(1.4f, Width / 16f))) {
                pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                g.DrawArc(pen, cx - radius, cy - radius, radius * 2, radius * 2, -50, 280);
                g.DrawLine(pen, cx, cy - radius * 1.28f, cx, cy - radius * .12f);
            }
        } else {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float cx = Width / 2f, cy = Height / 2f, radius = Math.Min(Width, Height) * .32f;
            using (var pen = new Pen(ink, Math.Max(1.25f, Width / 19f))) {
                for (int i = 0; i < 6; i++) {
                    double a = Math.PI * i / 3;
                    float dx = (float)Math.Cos(a), dy = (float)Math.Sin(a);
                    g.DrawLine(pen, cx, cy, cx + dx * radius, cy + dy * radius);
                    float bx = cx + dx * radius * .62f, by = cy + dy * radius * .62f;
                    for (int side = -1; side <= 1; side += 2)
                        g.DrawLine(pen, bx, by, bx + dx * radius * .15f - dy * radius * .25f * side, by + dy * radius * .15f + dx * radius * .25f * side);
                }
            }
        }
        if (Focused) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(ClientRectangle, -2, -2), ink, Surface);
    }
}

sealed partial class MonitorWindow
{
    readonly CoolingButton coolingButton = new CoolingButton();
    readonly CoolingButton coolingShutdownButton = new CoolingButton { PowerSymbol = true };
    ToolStripMenuItem coolingItem, coolingShutdownItem;
    EventWaitHandle coolingCancel;
    Task coolingTask;
    bool coolingBusy;
    bool coolingShutdownAfter;
    string coolingStatePath;
    void InitializeCooling()
    {
        coolingButton.Click += async (s, e) => await ToggleCooling();
        coolingShutdownButton.Click += async (s, e) => await ToggleCoolingAndShutdown();
        Controls.Add(coolingButton);
        Controls.Add(coolingShutdownButton);
        coolingItem = new ToolStripMenuItem("❄ Dinginkan GPU · 100% / 15 detik");
        coolingItem.Click += async (s, e) => await ToggleCooling();
        menu.Items.Add(coolingItem);
        coolingShutdownItem = new ToolStripMenuItem("❄⏻ Dinginkan GPU lalu matikan PC");
        coolingShutdownItem.Click += async (s, e) => await ToggleCoolingAndShutdown();
        menu.Items.Add(coolingShutdownItem);
        UpdateCooling();
    }
    bool CoolingSupported { get { return data.Gpu.Name != null && data.Gpu.Name.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0; } }
    void PlaceCoolingButton(int x, int y, int size)
    {
        Rectangle bounds = new Rectangle(S(x), S(y), Math.Max(18, S(size)), Math.Max(18, S(size)));
        if (coolingButton.Bounds != bounds) coolingButton.Bounds = bounds;
        coolingButton.Surface = theme == MonitorTheme.GShock ? Color.FromArgb(189, 197, 166) : BackColor;
        coolingButton.Ink = accent; coolingButton.ActiveSurface = accent;
        UpdateCooling();
    }
    void PlaceShutdownButton(int x, int y, int size)
    {
        Rectangle bounds = new Rectangle(S(x), S(y), Math.Max(18, S(size)), Math.Max(18, S(size)));
        if (coolingShutdownButton.Bounds != bounds) coolingShutdownButton.Bounds = bounds;
        coolingShutdownButton.Surface = theme == MonitorTheme.GShock ? Color.FromArgb(189, 197, 166) : BackColor;
        coolingShutdownButton.Ink = accent; coolingShutdownButton.ActiveSurface = accent;
        UpdateCooling();
    }
    void UpdateCooling()
    {
        if (coolingButton.IsDisposed) return;
        coolingButton.Enabled = !closing && (coolingBusy ? !coolingShutdownAfter : CoolingSupported);
        coolingShutdownButton.Enabled = !closing && (coolingBusy ? coolingShutdownAfter : CoolingSupported);
        coolingButton.Running = coolingBusy && !coolingShutdownAfter;
        coolingShutdownButton.Running = coolingBusy && coolingShutdownAfter;
        int seconds = 0;
        if (coolingBusy) {
            FanBoostState state = GpuFanBoost.ReadState(coolingStatePath);
            if (state != null && state.Status == "Running") seconds = Math.Max(0, Math.Min(GpuFanBoost.DurationSeconds, (int)Math.Ceiling(GpuFanBoost.DurationSeconds - (DateTime.UtcNow - state.StartedUtc).TotalSeconds)));
        }
        coolingButton.SecondsLeft = seconds;
        coolingShutdownButton.SecondsLeft = seconds;
        coolingButton.AccessibleName = coolingBusy ? "Batalkan pendinginan GPU" : "Dinginkan GPU: kipas 100 persen selama 15 detik";
        coolingShutdownButton.AccessibleName = coolingBusy
            ? (coolingShutdownAfter ? "Batalkan pendinginan dan shutdown Windows" : "Pendinginan GPU sedang berjalan")
            : "Dinginkan GPU selama 15 detik lalu matikan Windows";
        if (coolingItem != null) {
            coolingItem.Enabled = coolingButton.Enabled;
            coolingItem.Text = coolingBusy ? "Batalkan pendinginan GPU" : "❄ Dinginkan GPU · 100% / 15 detik";
        }
        if (coolingShutdownItem != null) {
            coolingShutdownItem.Enabled = coolingShutdownButton.Enabled;
            coolingShutdownItem.Text = coolingBusy && coolingShutdownAfter ? "Batalkan pendinginan dan shutdown"
                : coolingBusy ? "Pendinginan GPU sedang berjalan" : "❄⏻ Dinginkan GPU lalu matikan PC";
        }
        coolingButton.Invalidate();
        coolingShutdownButton.Invalidate();
    }
    async Task ToggleCooling()
    {
        if (coolingBusy) { CancelCooling(); return; }
        if (closing || !CoolingSupported) return;
        coolingShutdownAfter = false;
        await StartCooling();
    }
    async Task ToggleCoolingAndShutdown()
    {
        if (coolingBusy) { if (coolingShutdownAfter) CancelCooling(); return; }
        if (closing || !CoolingSupported) return;
        coolingShutdownAfter = true;
        await StartCooling();
    }
    async Task StartCooling()
    {
        string name = data.Gpu.Name;
        coolingBusy = true;
        string token = Guid.NewGuid().ToString("N");
        string eventName = "Local\\MiniMonitor-Cooling-" + token;
        string directory = Path.GetDirectoryName(settingsPath);
        coolingStatePath = Path.Combine(directory, "fan-boost-" + token + ".txt");
        coolingCancel = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
        UpdateCooling();
        coolingTask = RunCooling(name, eventName, coolingStatePath, coolingShutdownAfter);
        await coolingTask;
    }
    static string EncodeCoolingArgument(string value) { return Convert.ToBase64String(Encoding.UTF8.GetBytes(value)); }
    async Task RunCooling(string gpuName, string eventName, string statePath, bool shutdownAfter)
    {
        string failure = null;
        bool completed = false;
        try {
          try {
            Directory.CreateDirectory(Path.GetDirectoryName(statePath));
            string arguments = "--fan-boost " + Process.GetCurrentProcess().Id + " " + EncodeCoolingArgument(gpuName) + " " + EncodeCoolingArgument(statePath) + " " + EncodeCoolingArgument(eventName);
            using (var helper = Process.Start(new ProcessStartInfo(Application.ExecutablePath, arguments) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden })) {
                await Task.Run(() => helper.WaitForExit());
                FanBoostState state = GpuFanBoost.ReadState(statePath);
                completed = helper.ExitCode == 0 && state != null && state.Status == "Complete";
                if (helper.ExitCode != 0 || state == null || (state.Status != "Complete" && state.Status != "Cancelled")) {
                    failure = state == null ? "Pembaca kontrol kipas belum berhasil dimulai." : state.Message;
                }
            }
          } catch (Exception ex) {
            failure = "Pendinginan GPU gagal: " + ex.GetBaseException().Message;
          }
          if (failure != null) {
            string recovery = await Task.Run(() => GpuFanBoost.Recover(statePath));
            failure += Environment.NewLine + recovery;
          }
        } finally {
            try { if (File.Exists(statePath)) { File.Copy(statePath, Path.Combine(Path.GetDirectoryName(statePath), "fan-boost-last.txt"), true); File.Delete(statePath); } } catch { }
            if (coolingCancel != null) { coolingCancel.Dispose(); coolingCancel = null; }
            coolingBusy = false;
            coolingShutdownAfter = false;
            if (!IsDisposed) { UpdateCooling(); Invalidate(); }
        }
        if (failure != null && !closing && !IsDisposed) MessageBox.Show(this, failure, "Pendinginan GPU", MessageBoxButtons.OK, MessageBoxIcon.Information);
        else if (ShouldShutdownAfterCooling(completed, shutdownAfter, closing, IsDisposed)) ShutdownWindowsAfterCooling();
    }
    internal static bool ShouldShutdownAfterCooling(bool completed, bool shutdownRequested, bool windowClosing, bool disposed)
    {
        return completed && shutdownRequested && !windowClosing && !disposed;
    }
    internal static ProcessStartInfo CreateShutdownCommand()
    {
        return new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"),
            "/s /t 0 /d p:0:0 /c \"Pendinginan GPU Mini Monitor selesai\"")
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
    }
    void ShutdownWindowsAfterCooling()
    {
        try { SaveSettings(); Process.Start(CreateShutdownCommand()); }
        catch (Exception ex) { if (!closing && !IsDisposed) MessageBox.Show(this, "Windows belum dapat dimatikan: " + ex.Message, Text); }
    }
    void CancelCooling() { try { if (coolingCancel != null) coolingCancel.Set(); } catch (ObjectDisposedException) { } }
}
