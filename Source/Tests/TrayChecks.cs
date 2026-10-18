using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

static class TrayChecks
{
    static readonly BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
    static readonly StringBuilder log = new StringBuilder();
    static object Get(object o, string name) { return o.GetType().GetField(name, flags).GetValue(o); }
    static void Set(object o, string name, object value) { o.GetType().GetField(name, flags).SetValue(o, value); }
    static void Check(bool ok, string text) { if (!ok) throw new Exception("FAIL: " + text); log.AppendLine("PASS: " + text); }
    static void Click(ToolStripItem item)
    {
        // Same framework path used by mouse and keyboard item activation,
        // including automatic menu dismissal (unlike invoking the event alone).
        typeof(ToolStripItem).GetMethod("HandleClick", flags).Invoke(item, new object[] { EventArgs.Empty });
        Application.DoEvents();
    }
    static void Open(ContextMenuStrip menu)
    {
        menu.Show(new Point(Screen.PrimaryScreen.WorkingArea.Right - 480, 120));
        Application.DoEvents();
    }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            using (var form = new MonitorWindow(true))
            {
                Set(form, "settingsPath", Path.Combine(args[0], "test-settings.txt"));
                ((NotifyIcon)Get(form, "tray")).Visible = false;
                Set(form, "keepMenuOpen", true);
                var menu = (ContextMenuStrip)Get(form, "menu");
                menu.Closing += (s,e) => log.AppendLine("CLOSE: " + e.CloseReason);
                var keep = (ToolStripMenuItem)Get(form, "keepMenuItem");
                var clock = (ToolStripMenuItem)Get(form, "clockSoundItem");
                var app = (ToolStripMenuItem)Get(form, "appSoundItem");
                var speed = (ToolStripMenuItem)Get(form, "speedMenu");
                var opacity = (ToolStripMenuItem)Get(form, "opacityMenu");
                var sound = (ToolStripMenuItem)clock.OwnerItem;
                Open(menu);
                Check(menu.Visible && keep.Checked, "keep-open option enabled and checked");
                bool before = (bool)Get(form, "overlay");
                Click((ToolStripMenuItem)Get(form, "overlayItem"));
                log.AppendLine("After overlay: visible=" + menu.Visible + " overlay=" + Get(form, "overlay") + " before=" + before);
                Check(menu.Visible && (bool)Get(form, "overlay") != before, "overlay mode changes without dismissing menu");
                Click((ToolStripMenuItem)Get(form, "overlayItem"));
                Check(menu.Visible && (bool)Get(form, "overlay") == before, "multiple top-level changes stay open");
                sound.ShowDropDown(); Application.DoEvents();
                bool oldClock = clock.Checked; Click(clock);
                Check(menu.Visible && sound.DropDown.Visible && clock.Checked != oldClock, "sound submenu stays open and check mark updates immediately");
                bool oldApp = app.Checked; Click(app);
                Check(menu.Visible && sound.DropDown.Visible && app.Checked != oldApp, "second sound toggle works without reopening submenu");
                Click(clock); Click(app); sound.HideDropDown();
                speed.ShowDropDown(); Application.DoEvents(); Click(speed.DropDownItems[2]);
                Check(menu.Visible && speed.DropDown.Visible && ((ToolStripMenuItem)speed.DropDownItems[2]).Checked && !((ToolStripMenuItem)speed.DropDownItems[0]).Checked,
                    "interval selection stays open with exactly one checked option");
                Click(speed.DropDownItems[1]);
                Check(speed.DropDownItems.OfType<ToolStripMenuItem>().Count(i => i.Checked) == 1, "consecutive interval changes keep mutually exclusive checks");
                speed.HideDropDown(); opacity.ShowDropDown(); Application.DoEvents(); Click(opacity.DropDownItems[1]);
                Check(menu.Visible && opacity.DropDown.Visible && opacity.DropDownItems.OfType<ToolStripMenuItem>().Count(i => i.Checked) == 1 && ((ToolStripMenuItem)opacity.DropDownItems[1]).Checked,
                    "opacity submenu stays open with current opacity checked");
                opacity.HideDropDown();
                using (var bmp = new Bitmap(menu.Width, menu.Height)) { menu.DrawToBitmap(bmp, new Rectangle(Point.Empty, bmp.Size)); bmp.Save(Path.Combine(args[0], "menu-preview.png")); }
                menu.Close(ToolStripDropDownCloseReason.Keyboard); Application.DoEvents();
                Check(!menu.Visible, "Escape dismissal remains available");
                Open(menu); menu.Close(ToolStripDropDownCloseReason.AppClicked); Application.DoEvents();
                Check(!menu.Visible, "outside-click dismissal remains available");
                Open(menu); Click(keep);
                Check(!menu.Visible && !keep.Checked && File.ReadAllText(Path.Combine(args[0], "test-settings.txt")).Contains("keepMenuOpen=0"), "turning off keep-open closes menu and persists preference");
                Open(menu); sound.ShowDropDown(); Application.DoEvents(); Click(clock);
                Check(!menu.Visible && !sound.DropDown.Visible, "normal click-to-close behavior restored when option disabled");
                Open(menu); Click(keep);
                Check(menu.Visible && keep.Checked, "option can be re-enabled");
                Check(!(menu.Items[0] is KeepOpenMenuItem) && !(menu.Items[menu.Items.Count - 1] is KeepOpenMenuItem), "show/unlock and exit retain normal command behavior");
                var command = new ToolStripMenuItem("Test command"); menu.Items.Add(command); Click(command);
                Check(!menu.Visible, "ordinary commands still dismiss menu");
                Check(!menu.ShowItemToolTips && ((NotifyIcon)Get(form, "tray")).Text == "", "no hover tooltips reintroduced");
                var trayIcon = ((NotifyIcon)Get(form, "tray")).Icon;
                Check(trayIcon != null && trayIcon.Size == SystemInformation.SmallIconSize && form.Icon != null,
                    "custom icon loaded for form and tray at native small-icon size");
                using (var bmp = trayIcon.ToBitmap()) bmp.Save(Path.Combine(args[0], "tray-actual-size.png"));
            }
            File.WriteAllText(Path.Combine(args[0], "tray-checks.txt"), log.ToString()); return 0;
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(args[0], "tray-checks.txt"), log + "\n" + ex); return 1; }
    }
}

