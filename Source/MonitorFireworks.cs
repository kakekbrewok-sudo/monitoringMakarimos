using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

// Fixed-size, client-space particles: no bitmap, helper process or idle updates.
sealed class PixelFireworks : IDisposable
{
    public const double DurationSeconds = 6;
    public const int ParticleCount = 96;
    const double WaveDelay = 1.35, Lifetime = 3.3;
    struct Particle { public double X, Y, Reach, Size; public int Wave; public bool Red; }
    readonly Particle[] particles = new Particle[ParticleCount];
    readonly SolidBrush red = new SolidBrush(Color.Red), ink = new SolidBrush(Color.White);

    public PixelFireworks()
    {
        var random = new Random(731);
        for (int i = 0; i < particles.Length; i++) {
            int wave = i / 32;
            double angle = (i % 32 + wave * .37) * Math.PI / 16;
            particles[i] = new Particle { X = Math.Cos(angle), Y = Math.Sin(angle),
                Reach = .72 + random.NextDouble() * .48, Size = 3 + random.Next(4),
                Wave = wave, Red = i % 3 != 0 };
        }
    }

    public void Paint(Graphics g, Size size, double elapsed, Color accent, Color foreground)
    {
        if (elapsed < 0 || elapsed >= DurationSeconds || size.Width <= 0 || size.Height <= 0) return;
        float unit = Math.Max(.75f, Math.Min(1.8f, Math.Min(size.Width, size.Height) / 300f));
        for (int i = 0; i < particles.Length; i++) {
            Particle p = particles[i];
            double age = elapsed - p.Wave * WaveDelay;
            if (age < 0 || age >= Lifetime) continue;
            double progress = age / Lifetime;
            double spread = 1 - Math.Pow(1 - progress, 2.4);
            int alpha = (int)(235 * Math.Min(1, (Lifetime - age) / 1.15));
            SolidBrush brush = p.Red ? red : ink;
            brush.Color = Color.FromArgb(alpha, p.Red ? accent : foreground);
            int side = Math.Max(2, (int)Math.Round(p.Size * unit));
            // Snap square pixels to whole coordinates; scale radii separately
            // so both the portrait overlay and wide dashboard fill naturally.
            int x = (int)Math.Round(size.Width * (.5 + p.X * p.Reach * spread * .48)) - side / 2;
            int y = (int)Math.Round(size.Height * (.5 + p.Y * p.Reach * spread * .43 + .08 * progress * progress)) - side / 2;
            g.FillRectangle(brush, x, y, side, side);
        }
    }
    public void Dispose() { red.Dispose(); ink.Dispose(); }
}

sealed partial class MonitorWindow
{
    // 30 ms accommodates Windows' normal timer granularity without requesting
    // a high-resolution system timer; actual painting stays near 30 FPS.
    readonly Timer fireworksTimer = new Timer { Interval = 30 };
    readonly Stopwatch fireworksClock = new Stopwatch();
    readonly PixelFireworks fireworks = new PixelFireworks();
    bool pixelReminder = true;
    ToolStripMenuItem pixelReminderItem;

    bool CanShowFireworks { get { return !closing && !IsDisposed && Visible && WindowState != FormWindowState.Minimized && !sessionLocked && !powerSuspended; } }

    void InitializeFireworks()
    {
        fireworksTimer.Tick += (s, e) => AdvanceFireworks(fireworksClock.Elapsed.TotalSeconds);
        VisibleChanged += (s, e) => { if (!Visible) StopFireworks(); };
    }
    void BuildFireworksMenu()
    {
        pixelReminderItem = Setting("Efek pengingat pixel", (s, e) => {
            pixelReminder = !pixelReminder;
            if (!pixelReminder) StopFireworks();
            SaveSettings();
        });
        menu.Items.Add(pixelReminderItem);
        menu.Items.Add(Setting("Coba efek · 6 detik", (s, e) => StartFireworks(true)));
    }
    void HandleVisualReminder(bool activeHour)
    {
        if (activeHour) StartFireworks(false);
    }
    void StartFireworks(bool preview)
    {
        // A hidden/locked milestone is consumed, never queued for restoration.
        if ((!preview && !pixelReminder) || !CanShowFireworks || fireworksTimer.Enabled) return;
        fireworksClock.Restart(); fireworksTimer.Start(); Invalidate();
    }
    void AdvanceFireworks(double elapsed)
    {
        if (!CanShowFireworks || elapsed >= PixelFireworks.DurationSeconds) { StopFireworks(); return; }
        Invalidate();
    }
    void StopFireworks()
    {
        bool wasRunning = fireworksTimer.Enabled;
        fireworksTimer.Stop(); fireworksClock.Reset();
        if (wasRunning && !IsDisposed) Invalidate();
    }
    void PaintFireworks(Graphics g)
    {
        if (!fireworksTimer.Enabled) return;
        Color ink = theme == MonitorTheme.DotBlack ? Color.FromArgb(245, 245, 240) : Color.FromArgb(28, 28, 27);
        fireworks.Paint(g, ClientSize, fireworksClock.Elapsed.TotalSeconds, accent, ink);
    }
    void DisposeFireworks() { fireworksTimer.Dispose(); fireworksClock.Stop(); fireworks.Dispose(); }
}
