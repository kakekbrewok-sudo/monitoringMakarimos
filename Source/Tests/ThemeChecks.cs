using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

static class ThemeChecks
{
    static readonly BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
    static readonly StringBuilder log = new StringBuilder();
    static object Get(object o, string name) { return o.GetType().GetField(name, flags).GetValue(o); }
    static void Set(object o, string name, object value) { o.GetType().GetField(name, flags).SetValue(o, value); }
    static void Call(object o, string name, params object[] args) { o.GetType().GetMethod(name, flags).Invoke(o, args); }
    static void Check(bool ok, string text) { if (!ok) throw new Exception("FAIL: " + text); log.AppendLine("PASS: " + text); }
    static void Click(ToolStripItem item) { typeof(ToolStripItem).GetMethod("HandleClick", flags).Invoke(item, new object[] { EventArgs.Empty }); }
    static void Paint(MonitorWindow form, string path)
    {
        using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(path); }
    }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            Check(ThemeCatalog.Parse("missing") == MonitorTheme.DotBlack, "unknown themes fall back to Dot Hitam");
            var sample = new Snapshot { Used = 12, Total = 31.6, Load = 38, MemoryValid = true };
            sample.Cpu.Value = 46; sample.Cpu.Load = 58; sample.Gpu.Value = 50; sample.Gpu.Load = 42;
            sample.Disks.Add(new Reading("SSD", "PNY CS2340 1TB SSD") { Value = 42 });
            sample.Disks.Add(new Reading("SSD", "XPG GAMMIX S70 BLADE") { Value = 46 });
            using (var form = new MonitorWindow(true))
            {
                Set(form, "settingsPath", Path.Combine(args[0], "theme-test-settings.txt"));
                ((NotifyIcon)Get(form, "tray")).Visible = false;
                Set(form, "keepMenuOpen", true); Set(form, "data", sample); Set(form, "lastPoll", DateTime.Now);
                var tracker = (ActivityTracker)Get(form, "activity");
                tracker.Advance(new AppIdentity { Name = "Cyberpunk 2077", Key = "preview", ProcessId = -1, OpenedUtc = DateTime.UtcNow.AddSeconds(-4592) }, 0, false);
                tracker.Selected.ActiveSeconds = 2537;
                Call(form, "ApplyLayout");
                var menu = (ContextMenuStrip)Get(form, "menu");
                var themes = (ToolStripMenuItem)Get(form, "themeMenu");
                Check(themes.DropDownItems.Count == 4, "exactly four theme choices");
                menu.Show(new Point(20,20)); themes.ShowDropDown(); Application.DoEvents();
                var handle = form.Handle; var bounds = form.Bounds;
                foreach (MonitorTheme choice in Enum.GetValues(typeof(MonitorTheme)))
                {
                    Click(themes.DropDownItems[(int)choice]); Application.DoEvents();
                    Check(menu.Visible && themes.DropDown.Visible, choice + ": switch keeps root menu and submenu open");
                    Check(themes.DropDownItems.OfType<ToolStripMenuItem>().Count(i => i.Checked) == 1 && ((ToolStripMenuItem)themes.DropDownItems[(int)choice]).Checked, choice + ": exactly one current theme checked");
                    Check(form.Handle == handle && form.Bounds == bounds, choice + ": no recreation, movement or resize");
                    Check(File.ReadAllText(Path.Combine(args[0], "theme-test-settings.txt")).Contains("theme=" + ThemeCatalog.Keys[(int)choice]), choice + ": theme preference saved");
                    Paint(form, Path.Combine(args[0], ThemeCatalog.Keys[(int)choice] + ".png"));
                }
                menu.Close(); Application.DoEvents();
                foreach (MonitorTheme choice in Enum.GetValues(typeof(MonitorTheme)))
                {
                    Call(form, "SetTheme", choice, true);
                    Set(form, "theme", MonitorTheme.DotBlack); Call(form, "ReadSettings");
                    Check((MonitorTheme)Get(form, "theme") == choice, choice + ": preference restores correctly");
                    Call(form, "SetTheme", choice, false);
                    tracker.Selected.ActiveSeconds = 3600 * 100 + 42;
                    tracker.Selected.App.Name = "A very long application name to verify ellipsis and readable time";
                    sample.Cpu.Load = 100; sample.Cpu.Value = 100; sample.Gpu.Load = 0; sample.Gpu.Value = null;
                    Paint(form, Path.Combine(args[0], ThemeCatalog.Keys[(int)choice] + "-stress.png"));
                    Check(tracker.Selected.ReminderDue, choice + ": reminders remain active after changing appearance");
                }
                Check(((Timer)Get(form, "timer")).Interval == 1000 || ((Timer)Get(form, "timer")).Interval == 3000 || ((Timer)Get(form, "timer")).Interval == 5000, "existing sensor interval preserved");
                Check(!menu.ShowItemToolTips, "no hover tooltips introduced");
            }
            File.WriteAllText(Path.Combine(args[0], "theme-checks.txt"), log.ToString()); return 0;
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(args[0], "theme-checks.txt"), log + "\n" + ex); return 1; }
    }
}
