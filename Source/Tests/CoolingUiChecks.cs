using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

// Rendering and accessibility checks only: never click the cooling action,
// launch its helper, or read/write a real fan controller.
static class CoolingUiChecks
{
    static readonly BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    static readonly StringBuilder Log = new StringBuilder();
    static object Get(object target, string name) { return target.GetType().GetField(name, Flags).GetValue(target); }
    static void Set(object target, string name, object value) { target.GetType().GetField(name, Flags).SetValue(target, value); }
    static object Call(object target, string name, params object[] args) { return target.GetType().GetMethod(name, Flags).Invoke(target, args); }
    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Log.AppendLine("PASS: " + message);
    }
    static void Render(MonitorWindow form, string path)
    {
        // Draw twice because the parent paint pass positions the child button.
        using (var image = new Bitmap(form.Width, form.Height)) {
            form.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
            form.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
            // WinForms omits child controls of a never-shown parent. Render the
            // real button separately at its computed client position so QA can
            // inspect it without starting the form's Shown sensor callbacks.
            var button = (CoolingButton)Get(form, "coolingButton");
            using (var child = new Bitmap(button.Width, button.Height)) {
                button.DrawToBitmap(child, new Rectangle(Point.Empty, child.Size));
                Point origin = form.PointToScreen(Point.Empty);
                using (Graphics graphics = Graphics.FromImage(image))
                    graphics.DrawImageUnscaled(child, origin.X - form.Left + button.Left, origin.Y - form.Top + button.Top);
            }
            image.Save(path);
        }
    }
    static void Layout(MonitorWindow form, bool overlay, Size size)
    {
        Set(form, "overlay", overlay); Set(form, "windowInitialized", false);
        Set(form, "windowMaximized", false); Set(form, "windowSize", size);
        form.WindowState = FormWindowState.Normal;
        Call(form, "ApplyLayout");
    }
    [STAThread] static int Main(string[] args)
    {
        string output = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cooling-ui");
        Directory.CreateDirectory(output);
        try {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            using (var form = new MonitorWindow(false)) {
                Set(form, "settingsPath", Path.Combine(output, "settings-test.txt"));
                ((NotifyIcon)Get(form, "tray")).Visible = false;
                Set(form, "dpiScale", 1f); Set(form, "locked", false);
                var sample = new Snapshot { MemoryValid = true, Load = 54, Used = 17.1, Total = 31.6 };
                sample.Cpu.Name = "AMD Ryzen"; sample.Cpu.Load = 100; sample.Cpu.Value = 100;
                sample.Gpu.Name = "NVIDIA GeForce RTX 3060"; sample.Gpu.Load = 100; sample.Gpu.Value = 100;
                sample.Disks.Add(new Reading("SSD", "PNY CS2340 1TB SSD") { Value = 54 });
                sample.Disks.Add(new Reading("SSD", "XPG GAMMIX S70 BLADE") { Value = 63 });
                sample.Disks.Add(new Reading("HDD", "TOSHIBA MQ01ABD050") { Value = 44 });
                Set(form, "data", sample); Set(form, "lastPoll", DateTime.Now);
                var app = new AppIdentity { Name = "CONTROL Resonant", Key = "cooling-ui-fixture", ProcessId = -1,
                    ExecutablePath = @"D:\Games\Control\Control.exe", OpenedUtc = DateTime.UtcNow.AddSeconds(-4508) };
                var tracker = (ActivityTracker)Get(form, "activity");
                tracker.Advance(app, 0, false); tracker.Selected.ActiveSeconds = 2537;
                ((FpsRules)Get(form, "fpsRules")).Set(app, true);
                Set(form, "fpsEnabled", true); Set(form, "fpsValue", (double?)118);
                var button = (CoolingButton)Get(form, "coolingButton");
                var item = (ToolStripMenuItem)Get(form, "coolingItem");
                Check(button.AccessibleRole == AccessibleRole.PushButton && button.TabStop, "cooling has a keyboard-accessible button role");
                Check(button.AccessibleName.Contains("100") && button.AccessibleName.Contains("15 detik"), "accessible name explains speed and duration");
                Check(!((ContextMenuStrip)Get(form, "menu")).ShowItemToolTips, "cooling adds no hover tooltip");
                foreach (MonitorTheme theme in Enum.GetValues(typeof(MonitorTheme))) {
                    Call(form, "SetTheme", theme, false);
                    for (int layout = 0; layout < 3; layout++) {
                        bool overlay = layout == 2;
                        string name = theme + (overlay ? "-overlay" : layout == 0 ? "-wide" : "-narrow");
                        Layout(form, overlay, layout == 0 ? new Size(1080, 300) : new Size(340, 320));
                        Render(form, Path.Combine(output, name + ".png"));
                        Check(button.Enabled && item.Enabled, name + ": NVIDIA enables both entry points");
                        Check(form.ClientRectangle.Contains(button.Bounds), name + ": complete button lies inside the client area");
                        Check(button.Width >= 18 && button.Height >= 18, name + ": target remains usable when compact");
                        Check(button.Left > form.ClientSize.Width * .6, name + ": cooling stays beside GPU on the right");
                        if (layout != 0) {
                            float scale = (float)Get(form, "scale");
                            Check(button.Bottom <= (int)Math.Round(343 * scale), name + ": button stays above disk readings");
                            if (theme == MonitorTheme.GShock)
                                Check(button.Bottom <= (int)Math.Round(334 * scale), name + ": button does not cover GPU load bar");
                        }
                        Log.AppendLine("  client=" + form.ClientSize + ", cooling=" + button.Bounds);
                    }
                }
                sample.Gpu.Name = "AMD Radeon RX 6800"; Call(form, "UpdateCooling");
                Check(!button.Enabled && !item.Enabled, "unsupported GPU disables both cooling entry points");
                sample.Gpu.Name = null; Call(form, "UpdateCooling");
                Check(!button.Enabled && !item.Enabled, "missing GPU name remains disabled");
                sample.Gpu.Name = "NVIDIA GeForce RTX 3060"; Call(form, "UpdateCooling");
                Check(button.Enabled && item.Enabled, "NVIDIA discovery enables cooling after startup");
                Set(form, "coolingStatePath", Path.Combine(output, "nonexistent-simulated-state.txt"));
                Set(form, "coolingBusy", true); Call(form, "UpdateCooling");
                Check(button.Running && button.AccessibleName.Contains("Batalkan") && item.Text.Contains("Batalkan"), "active action becomes cancellation in both entry points");
                string simulatedState = Path.Combine(output, "simulated-state.txt");
                new FanBoostFileStore(simulatedState).Write(new FanBoostState { Status = "Running", StartedUtc = DateTime.UtcNow });
                Set(form, "coolingStatePath", simulatedState); Call(form, "UpdateCooling");
                Check(button.SecondsLeft >= 13 && button.SecondsLeft <= 15, "countdown follows a simulated helper state without fan access");
                foreach (MonitorTheme theme in Enum.GetValues(typeof(MonitorTheme))) {
                    Call(form, "SetTheme", theme, false); Layout(form, false, new Size(1080, 300));
                    Render(form, Path.Combine(output, theme + "-wide-active.png"));
                }
                Set(form, "coolingBusy", false); Call(form, "UpdateCooling");
                Check(!button.Running && button.AccessibleName.Contains("15 detik"), "idle state restores action and duration");
                Set(form, "closing", true); Call(form, "UpdateCooling");
                Check(!button.Enabled && !item.Enabled, "closing disables new cooling requests");
                Set(form, "closing", false);
                Check(Get(form, "reader") == null && !((Timer)Get(form, "timer")).Enabled && !((Timer)Get(form, "clockTimer")).Enabled,
                    "offscreen rendering never starts sensor polling or clock timers");
                Check(Get(form, "coolingTask") == null && Get(form, "coolingCancel") == null,
                    "tests never launch a helper or change fan speeds");
            }
            File.WriteAllText(Path.Combine(output, "cooling-ui-checks.txt"), Log.ToString()); return 0;
        } catch (Exception ex) {
            File.WriteAllText(Path.Combine(output, "cooling-ui-checks.txt"), Log + "\nFAIL: " + ex); return 1;
        }
    }
}
