using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

sealed partial class MonitorWindow
{
    readonly FpsRules fpsRules = new FpsRules();
    readonly FpsMonitor fpsReader = new FpsMonitor(Program.FpsSessionName);
    bool fpsEnabled = true;
    double? fpsValue;
    AppIdentity fpsMenuApp;
    ToolStripMenuItem fpsMenu, fpsEnabledItem, fpsAppItem, fpsStatusItem;

    void BuildFpsMenu()
    {
        fpsMenu = new ToolStripMenuItem("FPS game");
        fpsEnabledItem = Setting("Aktifkan FPS", (s, e) => { fpsEnabled = !fpsEnabled; SaveSettings(); UpdateFps(true); });
        fpsAppItem = Setting("Pantau FPS aplikasi ini", (s, e) =>
        {
            if (fpsMenuApp == null) return;
            fpsRules.Set(fpsMenuApp, !fpsRules.Tracks(fpsMenuApp)); SaveSettings(); UpdateFps(true);
        });
        fpsStatusItem = new ToolStripMenuItem { Enabled = false };
        fpsMenu.DropDownItems.Add(fpsEnabledItem); fpsMenu.DropDownItems.Add(fpsAppItem);
        fpsMenu.DropDownItems.Add(new ToolStripSeparator()); fpsMenu.DropDownItems.Add(fpsStatusItem);
        menu.Items.Add(fpsMenu);
    }
    void RefreshFpsMenu()
    {
        fpsEnabledItem.Checked = fpsEnabled;
        fpsAppItem.Enabled = !String.IsNullOrEmpty(FpsRules.Key(fpsMenuApp));
        fpsAppItem.Checked = fpsRules.Tracks(fpsMenuApp);
        string name = fpsMenuApp == null ? "aplikasi ini" : fpsMenuApp.Name;
        if (name.Length > 36) name = name.Substring(0, 33) + "...";
        fpsAppItem.Text = "Pantau FPS: " + name.Replace("&", "&&");
        fpsStatusItem.Text = !fpsEnabled ? "FPS dimatikan" : fpsValue.HasValue ? "FPS aplikasi / pembaruan 1 detik" :
            fpsReader.Status.IndexOf("access denied", StringComparison.OrdinalIgnoreCase) >= 0 ? "Mulai ulang sebagai administrator untuk FPS" :
            (fpsReader.Status.StartsWith("FPS:") || fpsReader.Status.StartsWith("FPS belum") ? "Belum terbaca; lihat Simpan diagnostik" : fpsReader.Status);
    }
    void UpdateFps(bool settingChanged = false)
    {
        // Keep the capture target stable while the user interacts with tray
        // menus. Background refresh never changes focus, menu or window size.
        AppIdentity app = activity.Selected == null ? null : activity.Selected.App;
        if (settingChanged || !menu.Visible || activity.Suspended || closing)
        {
            AppIdentity candidate = settingChanged && menu.Visible ? fpsMenuApp : app;
            bool active = activity.IsActive || (settingChanged && menu.Visible);
            fpsReader.Configure(fpsEnabled && !closing, !closing && !activity.Suspended && active && fpsRules.Tracks(candidate) ? candidate : null);
        }
        fpsValue = fpsEnabled && app != null ? fpsReader.Read(app.Key) : null;
        if (settingChanged) Invalidate();
    }
    void PaintAppWithFps(Graphics g, AppSession current, Font font, int y, int width)
    {
        bool showFps = fpsEnabled && current != null && fpsRules.Tracks(current.App);
        int reserved = showFps ? 83 : 0;
        DrawText(g, current == null ? "Buka game atau aplikasi" : current.App.Name, font, ForeColor,
            new Rectangle(18, y, width - 36 - reserved, 25));
        if (showFps)
        {
            string text = (fpsValue.HasValue ? Math.Min(9999, Math.Round(fpsValue.Value)).ToString("0", CultureInfo.InvariantCulture) : "—") + " FPS";
            DrawText(g, text, theme == MonitorTheme.GShock ? mono : usageFont, fpsValue.HasValue ? accent : muted,
                new Rectangle(width - 97, y, 79, 25), true);
        }
    }
}
