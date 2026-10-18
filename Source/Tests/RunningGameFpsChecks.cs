using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Threading;

// Read-only integration check for an already running game. Never launches,
// activates, injects into, or terminates the target process.
static class RunningGameFpsChecks
{
    static int Main(string[] args)
    {
        var log = new StringBuilder();
        try
        {
            using (var identity = WindowsIdentity.GetCurrent())
                log.AppendLine("Administrator: " + new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator));
            using (var game = Process.GetProcessById(int.Parse(args[0], CultureInfo.InvariantCulture)))
            {
                var tracker = new ActivityTracker();
                var resolve = typeof(ActivityTracker).GetMethod("Resolve", BindingFlags.Instance | BindingFlags.NonPublic);
                var app = (AppIdentity)resolve.Invoke(tracker, new object[] { game.MainWindowHandle, (uint)game.Id });
                if (app == null) throw new Exception("Game identity unavailable");
                log.AppendLine("Game: " + app.Name + " | PID " + app.ProcessId);
                log.AppendLine("Executable: " + app.ExecutablePath);
                if (!new FpsRules().Tracks(app)) throw new Exception("Game not automatically recognized");
                log.AppendLine("PASS: actual foreground identity resolves to an automatically recognized game");
                using (var reader = new FpsReader())
                {
                    reader.SetTarget(app); int samples = 0; long peakMemory = 0;
                    for (int i = 0; i < 15; i++)
                    {
                        Thread.Sleep(1000);
                        double? fps = reader.Read(app.Key);
                        if (fps.HasValue) { samples++; log.AppendLine("FPS " + fps.Value.ToString("0.00", CultureInfo.InvariantCulture)); }
                        int helperId = reader.HelperId;
                        if (helperId > 0) using (var helper = Process.GetProcessById(helperId)) peakMemory = Math.Max(peakMemory, helper.WorkingSet64);
                    }
                    log.AppendLine("Status: " + reader.Status);
                    log.AppendLine("Helper peak working set: " + (peakMemory / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB");
                    reader.StopAsync().GetAwaiter().GetResult();
                    if (reader.HelperId != 0) throw new Exception("Helper survived shutdown");
                    if (game.HasExited) throw new Exception("Game exited during check");
                    log.AppendLine("PASS: capture stopped and game remains running");
                    if (samples < 5) throw new Exception("Expected at least five FPS samples; received " + samples);
                    log.AppendLine("PASS: live frames received from the running game");
                }
            }
            File.WriteAllText(args[1], log.ToString()); return 0;
        }
        catch (Exception ex) { File.WriteAllText(args[1], log + "\n" + ex); return 1; }
    }
}
