using System;
using System.IO;
using System.Media;
using System.Text;

[Flags] enum ChimeCue { None = 0, ClockHour = 1, ActiveHour = 2 }

sealed class ChimeSchedule
{
    DateTime previousWall;
    double previousMonotonic;
    bool initialized;
    long lastClockHour = -1;
    public void Rebase(DateTime wall, double monotonic)
    { previousWall = wall; previousMonotonic = monotonic; initialized = true; }
    public ChimeCue Tick(DateTime wall, double monotonic, bool paused, bool appHour, bool clockEnabled, bool appEnabled)
    {
        if (!initialized) { Rebase(wall, monotonic); return ChimeCue.None; }
        double wallStep = (wall - previousWall).TotalSeconds;
        double elapsed = monotonic - previousMonotonic;
        long hour = wall.Ticks / TimeSpan.TicksPerHour;
        bool boundary = hour != previousWall.Ticks / TimeSpan.TicksPerHour;
        // Do not chime on startup, clock edits, lock/resume or delayed catch-up.
        bool uninterrupted = elapsed > 0 && elapsed <= 5 && wallStep >= 0 && wallStep <= 5 && Math.Abs(wallStep - elapsed) < 1.5;
        ChimeCue cue = ChimeCue.None;
        if (boundary && uninterrupted && !paused && hour != lastClockHour)
        { lastClockHour = hour; if (clockEnabled) cue |= ChimeCue.ClockHour; }
        if (appHour && !paused && appEnabled) cue |= ChimeCue.ActiveHour;
        Rebase(wall, monotonic);
        return cue;
    }
}

// Noticeable, short PCM tones with a smooth attack and release; never change the
// system mixer volume and never spawn a helper process to play audio.
sealed class SoftChimes : IDisposable
{
    public const int SampleRate = 22050;
    readonly MemoryStream[] streams = new MemoryStream[3];
    readonly SoundPlayer[] players = new SoundPlayer[3];
    bool disposed, loaded;
    public SoftChimes()
    {
        try
        {
            for (int i = 0; i < 3; i++)
            { streams[i] = new MemoryStream(CreateWave((ChimeCue)(i + 1)), false); players[i] = new SoundPlayer(streams[i]); players[i].Load(); }
            loaded = true;
        }
        catch { loaded = false; }
    }
    public void Play(ChimeCue cue)
    {
        if (disposed || !loaded || cue == ChimeCue.None) return;
        int index = (int)cue - 1; if (index < 0 || index >= players.Length) return;
        try { players[index].Play(); } catch { /* An unavailable audio device must not stop monitoring. */ }
    }
    static double Bell(double t, double length, double frequency)
    {
        if (t <= 0 || t >= length) return 0;
        double attack = Math.Min(1, t / .012), release = Math.Min(1, (length - t) / .025);
        double envelope = attack * attack * release * release * Math.Exp(-3 * t / length);
        return .18 * envelope * (Math.Sin(2 * Math.PI * frequency * t) + .12 * Math.Sin(2 * Math.PI * frequency * 2 * t)) / 1.12;
    }
    public static double Seconds(ChimeCue cue) { return cue == ChimeCue.ClockHour ? .56 : cue == ChimeCue.ActiveHour ? 1.08 : 1.82; }
    public static byte[] CreateWave(ChimeCue cue)
    {
        int count = (int)Math.Round(Seconds(cue) * SampleRate);
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream, Encoding.ASCII))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + count * 2); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(SampleRate); writer.Write(SampleRate * 2); writer.Write((short)2); writer.Write((short)16);
            writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(count * 2);
            for (int i = 0; i < count; i++)
            {
                double t = (double)i / SampleRate, sound = 0;
                if ((cue & ChimeCue.ClockHour) != 0) sound += Bell(t, .22, 880) + Bell(t - .30, .26, 1046.5);
                if ((cue & ChimeCue.ActiveHour) != 0)
                {
                    double a = t - (cue == (ChimeCue.ClockHour | ChimeCue.ActiveHour) ? .74 : 0);
                    sound += Bell(a, .20, 784) + Bell(a - .28, .20, 784) + Bell(a - .56, .20, 784) + Bell(a - .84, .24, 1046.5);
                }
                writer.Write((short)Math.Round(Math.Max(-1, Math.Min(1, sound)) * Int16.MaxValue));
            }
            writer.Flush(); return stream.ToArray();
        }
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        foreach (SoundPlayer p in players) if (p != null) { try { p.Stop(); } catch { } p.Dispose(); }
        foreach (MemoryStream s in streams) if (s != null) s.Dispose();
    }
}
