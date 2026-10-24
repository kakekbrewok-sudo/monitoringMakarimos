using System;
using System.IO;
using System.Linq;
using System.Text;

static class ChimeChecks
{
    static StringBuilder log = new StringBuilder();
    static void Check(bool ok, string label) { if (!ok) throw new Exception("FAIL: " + label); log.AppendLine("PASS: " + label); }
    static int Main(string[] args)
    {
        try
        {
            DateTime t = new DateTime(2026, 10, 11, 12, 59, 59);
            var s = new ChimeSchedule();
            Check(s.Tick(t, 0, false, false, true, true) == ChimeCue.None, "silent startup");
            Check(s.Tick(t.AddSeconds(1), 1, false, false, true, true) == ChimeCue.ClockHour, "one chime on exact clock-hour rollover");
            Check(s.Tick(t.AddSeconds(2), 2, false, false, true, true) == ChimeCue.None, "no repeated clock chime in the same hour");
            Check(s.Tick(t.AddSeconds(3), 3, false, true, true, true) == ChimeCue.ActiveHour, "distinct cue for the active-hour event");
            s.Rebase(t, 0);
            Check(s.Tick(t.AddSeconds(1), 1, false, false, true, true) == ChimeCue.None, "a backward clock edit cannot replay the already chimed hour");
            s = new ChimeSchedule(); s.Rebase(t, 0);
            Check(s.Tick(t.AddSeconds(1), 1, false, true, true, true) == (ChimeCue.ClockHour | ChimeCue.ActiveHour), "simultaneous events select a short sequenced clip");
            s.Rebase(t, 0); Check(s.Tick(t.AddHours(2), 7200, false, false, true, true) == ChimeCue.None, "sleep/resume skips catch-up chimes");
            s.Rebase(t, 0); Check(s.Tick(t.AddHours(1), 1, false, false, true, true) == ChimeCue.None, "manual clock jump is silent");
            s.Rebase(t, 0); Check(s.Tick(t.AddSeconds(1), 1, true, true, true, true) == ChimeCue.None, "lock mutes both cues");
            s = new ChimeSchedule(); s.Rebase(t, 0);
            Check(s.Tick(t.AddSeconds(1), 1, false, true, false, false) == ChimeCue.None, "independent sound switches mute events");
            Check(s.Tick(t.AddSeconds(2), 2, false, false, true, true) == ChimeCue.None, "unmute does not replay an earlier event");
            var tracker = new ActivityTracker(); var a = new AppIdentity { ProcessId = 1, Key = "game" }; var b = new AppIdentity { ProcessId = 2, Key = "browser" };
            tracker.Advance(a, 0, false);
            for (int i = 0; i < 3599; i++) tracker.Advance(a, 1, false);
            Check(!tracker.ActiveHourReached, "active-hour sound is silent before 60 minutes");
            tracker.Advance(b, 1, false); Check(tracker.ActiveHourReached, "milestone is preserved even when focus switches on the boundary");
            tracker.Advance(a, 1, false); Check(!tracker.ActiveHourReached, "refocusing an app does not repeat its hourly cue");
            tracker.Selected.Acknowledge();
            for (int i = 0; i < 3600; i++) tracker.Advance(a, 1, false);
            Check(tracker.ActiveHourReached, "second active-hour cue fires independently of acknowledgement");
            tracker.Advance(a, 1, false); Check(!tracker.ActiveHourReached, "active cue is a single-tick event");
            tracker.Selected.ActiveSeconds = 5211; tracker.Selected.Acknowledge();
            tracker.Selected.ActiveSeconds = 7199; tracker.Advance(a, 1, false);
            Check(!tracker.ActiveHourReached, "acknowledgement shifts chime away from absolute hour boundary");
            tracker.Selected.ActiveSeconds = 8810; tracker.Advance(a, 1, false);
            Check(tracker.ActiveHourReached, "chime follows the full hour after late acknowledgement");
            foreach (ChimeCue cue in new[] { ChimeCue.ClockHour, ChimeCue.ActiveHour, ChimeCue.ClockHour | ChimeCue.ActiveHour })
            {
                byte[] wav = SoftChimes.CreateWave(cue);
                Check(Encoding.ASCII.GetString(wav, 0, 4) == "RIFF" && BitConverter.ToInt32(wav, 4) == wav.Length - 8 && BitConverter.ToInt32(wav, 40) == wav.Length - 44, "valid PCM WAV header: " + cue);
                short[] samples = new short[(wav.Length - 44) / 2]; Buffer.BlockCopy(wav, 44, samples, 0, wav.Length - 44);
                double peak = samples.Max(v => Math.Abs((int)v)) / 32767.0;
                Check(peak > .10 && peak <= .32 && Math.Abs(samples[0]) <= 1 && Math.Abs(samples[samples.Length - 1]) <= 1, "noticeable bounded amplitude and click-free endpoints: " + cue);
                double seconds = (double)samples.Length / SoftChimes.SampleRate;
                Check(seconds <= 3.2 && Math.Abs(seconds - SoftChimes.Seconds(cue)) < .001, "short reminder clip: " + cue);
                log.AppendLine(String.Format(System.Globalization.CultureInfo.InvariantCulture, "Audio {0}: {1:0.000}s, peak {2:0.0}% full-scale", cue, seconds, peak * 100));
                if (cue != (ChimeCue.ClockHour | ChimeCue.ActiveHour)) File.WriteAllBytes(Path.Combine(args[0], cue == ChimeCue.ClockHour ? "jam-baru.wav" : "satu-jam-aktif.wav"), wav);
            }
            Check(!SoftChimes.CreateWave(ChimeCue.ClockHour).SequenceEqual(SoftChimes.CreateWave(ChimeCue.ActiveHour)), "the two cue patterns differ");
            using (var player = new SoftChimes()) { /* Load and dispose resources without playing sound. */ }
            File.WriteAllText(Path.Combine(args[0], "chime-checks.txt"), log.ToString()); return 0;
        }
        catch (Exception e) { File.WriteAllText(Path.Combine(args[0], "chime-checks.txt"), log + "\n" + e); return 1; }
    }
}
