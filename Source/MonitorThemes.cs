using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

enum MonitorTheme { DotBlack, DotWhite, KanjiArt, GShock }

static class ThemeCatalog
{
    public static readonly string[] Labels = { "Dot Hitam (default)", "Dot Putih", "Kanji Art", "G-Shock" };
    public static readonly string[] Keys = { "dot-black", "dot-white", "kanji-art", "g-shock" };
    public static MonitorTheme Parse(string key)
    {
        int index = Array.IndexOf(Keys, key);
        return index < 0 ? MonitorTheme.DotBlack : (MonitorTheme)index;
    }
}

sealed partial class MonitorWindow
{
    MonitorTheme theme = MonitorTheme.DotBlack;
    ToolStripMenuItem themeMenu;
    Color rule = Color.FromArgb(48, 48, 48), frame = Color.FromArgb(57, 57, 57), track = Color.FromArgb(48, 48, 48);
    readonly Font posterTime = new Font("Consolas", 28f, FontStyle.Bold);
    readonly Font posterTitle = new Font("Impact", 20f, FontStyle.Regular);
    readonly Font kanjiLarge = new Font("Yu Gothic", 42f, FontStyle.Bold);
    readonly Font kanjiSmall = new Font("Yu Gothic", 9f, FontStyle.Bold);
    readonly Font mono = new Font("Consolas", 9f, FontStyle.Regular);
    readonly Font monoTitle = new Font("Consolas", 12f, FontStyle.Bold);
    readonly Font monoTime = new Font("Consolas", 20f, FontStyle.Regular);

    void SetTheme(MonitorTheme selected, bool persist)
    {
        theme = selected;
        switch (theme)
        {
            case MonitorTheme.DotWhite:
                BackColor = Color.FromArgb(245, 245, 240); ForeColor = Color.FromArgb(28, 28, 27);
                accent = Color.FromArgb(198, 43, 44); muted = Color.FromArgb(102, 102, 98);
                rule = frame = Color.FromArgb(210, 210, 203); track = Color.FromArgb(220, 220, 212);
                break;
            case MonitorTheme.KanjiArt:
                BackColor = Color.FromArgb(250, 248, 237); ForeColor = Color.FromArgb(29, 43, 146);
                accent = Color.FromArgb(205, 36, 42); muted = Color.FromArgb(84, 90, 126);
                rule = frame = Color.FromArgb(29, 43, 146); track = Color.FromArgb(221, 220, 222);
                break;
            case MonitorTheme.GShock:
                BackColor = Color.FromArgb(30, 33, 35); ForeColor = Color.FromArgb(32, 40, 32);
                accent = Color.FromArgb(151, 34, 26); muted = Color.FromArgb(65, 75, 62);
                rule = Color.FromArgb(118, 129, 106); frame = Color.FromArgb(68, 72, 75); track = Color.FromArgb(152, 162, 134);
                break;
            default:
                BackColor = Color.FromArgb(15, 15, 15); ForeColor = Color.FromArgb(238, 238, 234);
                accent = Color.FromArgb(239, 52, 55); muted = Color.FromArgb(154, 154, 154);
                rule = track = Color.FromArgb(48, 48, 48); frame = Color.FromArgb(57, 57, 57);
                break;
        }
        // All four layouts share the same bounds. A theme change only repaints;
        // no handle recreation, activation, timer, sensor reload or menu closing.
        ApplyCaptionTheme(); if (persist) SaveSettings();
        Invalidate();
    }

    void PaintKanji(Graphics g, DateTime now, int width)
    {
        DrawText(g, "TIME / SYSTEM", posterTitle, ForeColor, new Rectangle(14, 4, width - 28, 38));
        DrawText(g, "時間・記録・休憩", kanjiSmall, accent, new Rectangle(18, 37, width - 36, 22));
        // Typographic poster: a large time ideograph, print rules and a red
        // registration mark. No bitmap background or external font download.
        DrawText(g, "時", kanjiLarge, ForeColor, new Rectangle(7, 55, 88, 75));
        using (var line = new Pen(ForeColor, S(1)))
        {
            g.DrawRectangle(line, S(95), S(64), S(width - 114), S(62));
            g.DrawLine(line, S(18), S(142), S(width - 18), S(142));
        }
        using (var red = new SolidBrush(accent)) g.FillRectangle(red, S(width - 28), S(58), S(10), S(10));
        DrawText(g, now.ToString("HH:mm"), posterTime, ForeColor, new Rectangle(101, 62, width - 126, 47));
        PlaceSleepButton(width - 82, 72, 28);
        PlaceShutdownButton(width - 48, 72, 28);
        DrawText(g, now.ToString("ss") + " SEC", mono, accent, new Rectangle(102, 105, width - 128, 19), true);
        DrawText(g, now.ToString("ddd, dd MMM yyyy", CultureInfo.GetCultureInfo("id-ID")).ToUpperInvariant(), mono, muted, new Rectangle(18, 126, width - 36, 19));
        AppSession current = activity.Selected;
        bool reminder = current != null && current.ReminderDue;
        DrawText(g, ActivityLabel(current), small, muted, new Rectangle(18, 151, width - 36, 18));
        PaintAppWithFps(g, current, appFont, 172, width);
        string active = ActivityTracker.Duration(current == null ? 0 : current.ActiveSeconds);
        DrawText(g, active, monoTime, reminder ? accent : ForeColor, new Rectangle(14, 198, width - 80, 38));
        DrawText(g, "AKTIF", small, muted, new Rectangle(width - 64, 204, 46, 25), true);
        double opened = current == null ? -1 : ActivityTracker.OpenDuration(current.App, DateTime.UtcNow);
        DrawText(g, "SEJAK DIBUKA  " + (opened < 0 ? "—" : ActivityTracker.Duration(opened)), small, muted, new Rectangle(18, 236, width - 36, 21));
        DrawText(g, reminder ? "60 MENIT+ / WAKTUNYA ISTIRAHAT" : "PENGINGAT ISTIRAHAT / 60 MENIT", small, reminder ? accent : muted, new Rectangle(18, 261, width - 36, 18));
        ProgressRule(g, current, reminder, width, 283, 2);
        int ramWidth = 48, col = (width - 36 - ramWidth - 20) / 2;
        Metric(g, "RAM", data.MemoryValid ? data.Load + "%" : "—", 18, ramWidth, false);
        Metric(g, "CPU", data.Cpu.UsageAndTemperature, 18 + ramWidth + 10, col, true);
        Metric(g, "GPU", data.Gpu.UsageAndTemperature, 18 + ramWidth + 20 + col, col, true);
    }

    string ActivityLabel(AppSession current)
    {
        return activity.IsActive ? "APLIKASI AKTIF" : current == null ? "MENUNGGU APLIKASI" : "JEDA / DI LATAR BELAKANG";
    }

    void PaintGShock(Graphics g, DateTime now, int width)
    {
        int height = (int)(ClientSize.Height / scale);
        var caseShape = new[] { new Point(S(23), S(3)), new Point(S(width - 23), S(3)), new Point(S(width - 4), S(22)), new Point(S(width - 4), S(height - 22)), new Point(S(width - 23), S(height - 3)), new Point(S(23), S(height - 3)), new Point(S(4), S(height - 22)), new Point(S(4), S(22)) };
        using (var rim = new Pen(Color.FromArgb(83, 88, 90), S(2))) g.DrawPolygon(rim, caseShape);
        using (var lcd = new SolidBrush(Color.FromArgb(189, 197, 166))) g.FillRectangle(lcd, S(13), S(39), S(width - 26), S(height - 49));
        using (var bezel = new Pen(Color.FromArgb(13, 17, 17), S(3))) g.DrawRectangle(bezel, S(12), S(38), S(width - 24), S(height - 47));
        TextRenderer.DrawText(g, "G-SHOCK", DisplayFont(compact), new Rectangle(S(35), S(5), S(width - 70), S(29)), Color.FromArgb(226, 63, 48), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        using (var screw = new Pen(Color.FromArgb(149, 154, 155), S(1)))
        {
            foreach (int x in new[] { 20, width - 27 }) { g.DrawEllipse(screw, S(x), S(16), S(6), S(6)); g.DrawLine(screw, S(x + 1), S(19), S(x + 5), S(19)); }
        }
        DrawText(g, "24H", mono, ForeColor, new Rectangle(22, 44, 39, 20));
        DrawText(g, now.ToString("ddd, dd MMM yyyy", CultureInfo.GetCultureInfo("id-ID")).ToUpperInvariant(), small, muted, new Rectangle(65, 44, width - 87, 20), true);
        SegmentClock.Draw(g, now.ToString("HH:mm"), 23, 70, 59, ForeColor, track, scale);
        PlaceSleepButton(width - 82, 68, 28);
        PlaceShutdownButton(width - 48, 68, 28);
        SegmentClock.Draw(g, now.ToString("ss"), width - 59, 100, 28, ForeColor, track, scale);
        Divider(g, 138, width);
        AppSession current = activity.Selected;
        bool reminder = current != null && current.ReminderDue;
        DrawText(g, ActivityLabel(current), mono, muted, new Rectangle(18, 147, width - 36, 18));
        PaintAppWithFps(g, current, monoTitle, 168, width);
        string active = ActivityTracker.Duration(current == null ? 0 : current.ActiveSeconds);
        DrawText(g, active, monoTime, reminder ? accent : ForeColor, new Rectangle(14, 195, width - 80, 38));
        DrawText(g, "AKTIF", mono, muted, new Rectangle(width - 65, 203, 47, 25), true);
        double opened = current == null ? -1 : ActivityTracker.OpenDuration(current.App, DateTime.UtcNow);
        DrawText(g, "DIBUKA  " + (opened < 0 ? "—" : ActivityTracker.Duration(opened)), mono, muted, new Rectangle(18, 234, width - 36, 21));
        ProgressRule(g, current, reminder, width, 261, 4);
        DrawText(g, reminder ? "60 MENIT+ / WAKTUNYA ISTIRAHAT" : "PENGINGAT ISTIRAHAT / 60 MENIT", small, reminder ? accent : muted, new Rectangle(18, 271, width - 36, 18));
        int ramWidth = 48, col = (width - 36 - ramWidth - 20) / 2;
        Metric(g, "RAM", data.MemoryValid ? data.Load + "%" : "—", 18, ramWidth, false);
        Metric(g, "CPU", data.Cpu.UsageAndTemperature, 18 + ramWidth + 10, col, true);
        Metric(g, "GPU", data.Gpu.UsageAndTemperature, 18 + ramWidth + 20 + col, col, true);
        LoadBar(g, data.MemoryValid ? (float?)data.Load : null, 18, 334, ramWidth);
        LoadBar(g, data.Cpu.Load, 18 + ramWidth + 10, 334, col);
        LoadBar(g, data.Gpu.Load, 18 + ramWidth + 20 + col, 334, col);
    }

    void ProgressRule(Graphics g, AppSession current, bool reminder, int width, int y, int height)
    {
        using (var background = new SolidBrush(track)) g.FillRectangle(background, S(18), S(y), S(width - 36), S(height));
        double progress = current == null ? 0 : current.ReminderProgress;
        if (progress > 0) using (var fill = new SolidBrush(reminder ? accent : ForeColor))
            g.FillRectangle(fill, S(18), S(y), S((int)((width - 36) * progress)), S(height));
    }

    void LoadBar(Graphics g, float? load, int x, int y, int width)
    {
        using (var background = new SolidBrush(track)) g.FillRectangle(background, S(x), S(y), S(width), S(2));
        if (load.HasValue && load > 0) using (var fill = new SolidBrush(muted))
            g.FillRectangle(fill, S(x), S(y), S((int)(width * Math.Min(100, load.Value) / 100)), S(2));
    }

    void DisposeThemeFonts()
    {
        posterTime.Dispose(); posterTitle.Dispose(); kanjiLarge.Dispose(); kanjiSmall.Dispose(); mono.Dispose(); monoTitle.Dispose(); monoTime.Dispose();
    }
}

static class SegmentClock
{
    static readonly int[] Masks = { 0x3f, 0x06, 0x5b, 0x4f, 0x66, 0x6d, 0x7d, 0x07, 0x7f, 0x6f };
    public static void Draw(Graphics g, string text, float x, float y, float height, Color ink, Color dim, float scale)
    {
        GraphicsState saved = g.Save(); g.ScaleTransform(scale, scale); g.SmoothingMode = SmoothingMode.AntiAlias;
        float w = height * .52f, t = height * .075f, half = height / 2;
        using (var on = new SolidBrush(ink))
        using (var off = new SolidBrush(dim))
        {
            foreach (char c in text)
            {
                if (c == ':')
                {
                    g.FillRectangle(on, x + t, y + height * .31f, t, t);
                    g.FillRectangle(on, x + t, y + height * .68f, t, t);
                    x += height * .25f; continue;
                }
                if (c < '0' || c > '9') continue;
                int mask = Masks[c - '0'];
                Horizontal(g, (mask & 1) != 0 ? on : off, x + t, y, w - 2 * t, t);
                Vertical(g, (mask & 2) != 0 ? on : off, x + w - t, y + t, half - 1.5f * t, t);
                Vertical(g, (mask & 4) != 0 ? on : off, x + w - t, y + half + t / 2, half - 1.5f * t, t);
                Horizontal(g, (mask & 8) != 0 ? on : off, x + t, y + height - t, w - 2 * t, t);
                Vertical(g, (mask & 16) != 0 ? on : off, x, y + half + t / 2, half - 1.5f * t, t);
                Vertical(g, (mask & 32) != 0 ? on : off, x, y + t, half - 1.5f * t, t);
                Horizontal(g, (mask & 64) != 0 ? on : off, x + t, y + half - t / 2, w - 2 * t, t);
                x += height * .70f;
            }
        }
        g.Restore(saved);
    }
    static void Horizontal(Graphics g, Brush b, float x, float y, float w, float t)
    {
        g.FillPolygon(b, new[] { new PointF(x, y + t / 2), new PointF(x + t / 2, y), new PointF(x + w - t / 2, y), new PointF(x + w, y + t / 2), new PointF(x + w - t / 2, y + t), new PointF(x + t / 2, y + t) });
    }
    static void Vertical(Graphics g, Brush b, float x, float y, float h, float t)
    {
        g.FillPolygon(b, new[] { new PointF(x + t / 2, y), new PointF(x + t, y + t / 2), new PointF(x + t, y + h - t / 2), new PointF(x + t / 2, y + h), new PointF(x, y + h - t / 2), new PointF(x, y + t / 2) });
    }
}
