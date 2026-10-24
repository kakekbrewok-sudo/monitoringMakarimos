using System;
using System.Drawing;
using System.Globalization;

sealed partial class MonitorWindow
{
    readonly Font wideClock = new Font("Segoe UI", 44f, FontStyle.Bold);
    readonly Font wideValue = new Font("Segoe UI", 23f, FontStyle.Bold);
    readonly Font wideDuration = new Font("Consolas", 26f, FontStyle.Regular);
    readonly Font widePosterClock = new Font("Consolas", 43f, FontStyle.Bold);

    // Native dashboard paint; temporary reminder particles are drawn afterward.
    void PaintDesignedWide(Graphics g)
    {
        bool paper = theme == MonitorTheme.DotWhite, poster = theme == MonitorTheme.KanjiArt;
        DateTime now = DateTime.Now;
        bool status = lastPoll == DateTime.MinValue || !data.Cpu.Value.HasValue || transient != null;
        int contentHeight = Math.Max(244, 145 + Math.Max(1, data.Disks.Count) * 26 + 8 + (status ? 24 : 0));
        scale = Math.Min(ClientSize.Width / 900f, ClientSize.Height / (float)contentHeight);
        int width = (int)(ClientSize.Width / scale), height = (int)(ClientSize.Height / scale);
        if (paper) WideFill(g, Color.FromArgb(243, 237, 230), 0, 0, 267, height);
        if (poster) {
            WideFill(g, ForeColor, 20, 15, 232, 3);
            DrawText(g, "TIME / SYSTEM", posterTitle, ForeColor, new Rectangle(16, 21, 240, 35));
            DrawText(g, "時間・記録・休憩", kanjiSmall, accent, new Rectangle(20, 55, 234, 22));
            DrawText(g, now.ToString("HH:mm"), widePosterClock, ForeColor, new Rectangle(13, 77, 245, 76));
            WideFill(g, accent, 214, 165, 33, 33);
            DrawText(g, "時", monoTitle, BackColor, new Rectangle(217, 169, 30, 26));
            DrawText(g, now.ToString("ss") + " SEC", monoTitle, accent, new Rectangle(20, 167, 150, 28));
        } else {
            WideFill(g, accent, 20, 23, 6, 6);
            DrawText(g, "MINI / MONITOR", heading, paper ? accent : ForeColor, new Rectangle(32, 15, 180, 24));
            DrawText(g, "24H", mono, muted, new Rectangle(214, 15, 39, 24), true);
            if (paper) DrawText(g, now.ToString("HH:mm"), wideClock, accent, new Rectangle(13, 62, 244, 88));
            else DotClock.Draw(g, now.ToString("HH:mm"), 20, 81, 8.8f, ForeColor, scale);
            DrawText(g, now.ToString("ss"), monoTitle, accent, new Rectangle(20, 157, 38, 27));
            DrawText(g, "DETIK", small, muted, new Rectangle(60, 159, 80, 24));
        }
        PlaceSleepButton(180, 165, 32);
        PlaceShutdownButton(218, 165, 32);
        DrawText(g, now.ToString("ddd, dd MMM yyyy", CultureInfo.GetCultureInfo("id-ID")).ToUpperInvariant(), small, muted, new Rectangle(20, 211, 235, 23));
        using (var pen = new Pen(rule)) {
            g.DrawLine(pen, S(267), S(15), S(267), S(height - 15));
            g.DrawLine(pen, S(566), S(15), S(566), S(height - 15));
        }
        AppSession current = activity.Selected;
        bool reminder = current != null && current.ReminderDue;
        DrawText(g, ActivityLabel(current), small, muted, new Rectangle(287, 15, 259, 23));
        DrawText(g, current == null ? "Buka game atau aplikasi" : current.App.Name, appFont, ForeColor, new Rectangle(287, 46, 259, 32));
        string duration = ActivityTracker.Duration(current == null ? 0 : current.ActiveSeconds);
        if (!paper && !poster) DotClock.Draw(g, duration, 289, 96, duration.Length > 8 ? 3.4f : 4.6f, reminder ? accent : ForeColor, scale);
        else DrawText(g, duration, duration.Length > 8 ? monoTime : wideDuration, reminder ? accent : ForeColor, new Rectangle(283, 86, 263, 49));
        double opened = current == null ? -1 : ActivityTracker.OpenDuration(current.App, DateTime.UtcNow);
        DrawText(g, "DIBUKA  " + (opened < 0 ? "—" : ActivityTracker.Duration(opened)), small, muted, new Rectangle(287, 141, 259, 24));
        if (fpsEnabled && current != null && fpsRules.Tracks(current.App))
            DrawText(g, (fpsValue.HasValue ? Math.Min(9999, Math.Round(fpsValue.Value)).ToString("0") : "—") + " FPS", compact, accent, new Rectangle(287, 171, 130, 26));
        DrawText(g, "60 MENIT", mono, muted, new Rectangle(438, 173, 108, 24), true);
        double progress = current == null ? 0 : Math.Max(0, Math.Min(1, current.ReminderProgress));
        if (!paper && !poster) {
            for (int i = 0; i < 40; i++) WideFill(g, i < progress * 40 ? accent : track, 287 + i * 6.5f, 204, 4.5f, 4);
        } else {
            WideFill(g, track, 287, 204, 259, poster ? 2 : 4);
            WideFill(g, accent, 287, 204, (float)(259 * progress), poster ? 2 : 4);
        }
        PaintReminderAction(g, new Rectangle(287, 214, 259, 23));

        int col = (width - 606) / 3;
        string[] names = { "RAM", "CPU", "GPU" };
        float?[] loads = { data.MemoryValid ? (float?)data.Load : null, data.Cpu.Load, data.Gpu.Load };
        for (int i = 0; i < 3; i++) {
            int x = 586 + i * col;
            DrawText(g, names[i], heading, poster ? ForeColor : muted, new Rectangle(x, 15, col, 23));
            DrawText(g, loads[i].HasValue ? loads[i].Value.ToString("0") + "%" : "—", wideValue, ForeColor, new Rectangle(x - 3, 41, col + 3, 44));
            string detail = i == 0 ? (data.MemoryValid ? data.Used.ToString("0.0") + " GB" : "—") : i == 1 ? data.Cpu.Text : data.Gpu.Text;
            DrawText(g, detail, compact, muted, new Rectangle(x, 87, i == 2 ? col - 30 : col, 26));
            if (i == 2) PlaceCoolingButton(x + col - 28, 87, 26);
            WideFill(g, track, x, 118, col - 14, 2);
            if (loads[i].HasValue) WideFill(g, poster ? ForeColor : accent, x, 118, (col - 14) * Math.Max(0, Math.Min(100, loads[i].Value)) / 100f, 2);
        }
        DrawText(g, poster ? "記録 / PENYIMPANAN" : "PENYIMPANAN", small, poster ? accent : muted, new Rectangle(586, 125, width - 606, 20));
        int y = 147, ssd = 1, hdd = 1;
        foreach (Reading disk in data.Disks) {
            string label = disk.Kind + " " + (disk.Kind == "SSD" ? ssd++ : hdd++);
            DrawText(g, label, small, muted, new Rectangle(586, y, 49, 24));
            DrawText(g, disk.Name, small, muted, new Rectangle(637, y, width - 729, 24));
            DrawText(g, disk.Text, compact, ForeColor, new Rectangle(width - 89, y, 69, 24), true);
            y += 26;
        }
        if (data.Disks.Count == 0) DrawText(g, "SSD  —", small, muted, new Rectangle(586, y, width - 606, 24));
        if (status) DrawText(g, StatusText(), small, muted, new Rectangle(586, height - 26, width - 606, 22));
    }
    void WideFill(Graphics g, Color color, float x, float y, float width, float height)
    {
        if (width <= 0 || height <= 0) return;
        using (var brush = new SolidBrush(color)) g.FillRectangle(brush, x * scale, y * scale, width * scale, height * scale);
    }
    void DisposeWideFonts() { wideClock.Dispose(); wideValue.Dispose(); wideDuration.Dispose(); widePosterClock.Dispose(); }
}
