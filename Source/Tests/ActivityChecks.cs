using System;
using System.Diagnostics;
using System.IO;
using System.Text;

static class ActivityChecks
{
    static StringBuilder log = new StringBuilder();
    static void Check(bool ok, string label) { if (!ok) throw new Exception("FAIL: " + label); log.AppendLine("PASS: " + label); }
    static int Main(string[] args)
    {
        try
        {
            using(var own=Process.GetCurrentProcess())
                Check(String.Equals(ActivityTracker.ExecutablePath(own.Id),own.MainModule.FileName,StringComparison.OrdinalIgnoreCase), "limited process query resolves the executable path");
            Check(ActivityTracker.ExecutablePath(-1)==null, "invalid or inaccessible process metadata stays unavailable");
            var a = new AppIdentity { ProcessId = 1, Key = "1:first", Name = "Game A", OpenedUtc = new DateTime(2026, 10, 10, 19, 0, 0, DateTimeKind.Utc) };
            var b = new AppIdentity { ProcessId = 2, Key = "2:first", Name = "App B" };
            var t = new ActivityTracker();
            t.Advance(a, 0, false); t.Advance(a, 2, false); t.Advance(b, 1, false);
            var bs = t.Selected; t.Advance(b, 2, false); t.Advance(null, 1, false);
            Check(bs.ActiveSeconds == 2 && !t.IsActive, "pause on desktop/overlay without counting background");
            t.Advance(a, 1, false); Check(t.Selected.ActiveSeconds == 3, "switch back resumes per-process total");
            t.Advance(a, 120, false); Check(t.Selected.ActiveSeconds == 3, "sleep or long timer gap is excluded");
            t.Advance(a, 2, true); Check(t.Selected.ActiveSeconds == 3 && !t.IsActive, "locked session is excluded");
            t.Advance(a, 1, false); t.Advance(a, 1, false); Check(t.Selected.ActiveSeconds == 4, "resume does not count locked interval");
            var reboot = new AppIdentity { ProcessId = 1, Key = "1:second", Name = "Game A" };
            t.Advance(reboot, 1, false); Check(t.Selected.ActiveSeconds == 0, "PID reuse/new game launch gets a fresh total");
            for (int i = 0; i < 3599; i++) t.Advance(reboot, 1, false);
            Check(!t.Selected.ReminderDue, "no reminder before 60 minutes");
            t.Advance(reboot, 1, false); Check(t.Selected.ReminderDue && t.Selected.ReminderProgress == 1, "reminder at exactly 60 active minutes");
            t.Selected.Acknowledge(); Check(!t.Selected.ReminderDue, "acknowledge clears red reminder");
            for (int i = 0; i < 3600; i++) t.Advance(reboot, 1, false);
            Check(t.Selected.ReminderDue, "next reminder after another 60 active minutes");
            t.ResetSelected(); Check(t.Selected.ActiveSeconds == 0 && !t.Selected.ReminderDue, "reset clears active total and reminder");
            Check(ActivityTracker.Duration(90061) == "25:01:01", "duration does not wrap after 24 hours");
            Check(ActivityTracker.OpenDuration(a, a.OpenedUtc.Value.AddMinutes(90)) == 5400, "process age stays independent of foreground duration");
            Check(ActivityTracker.OpenDuration(b, DateTime.UtcNow) == -1, "unreadable start time stays unavailable");
            Check(ActivityTracker.OpenDuration(a, a.OpenedUtc.Value.AddMinutes(-1)) == 0, "backward clock adjustment is clamped");
            File.WriteAllText(args[0], log.ToString()); return 0;
        }
        catch (Exception e) { File.WriteAllText(args[0], log + "\n" + e); return 1; }
    }
}
