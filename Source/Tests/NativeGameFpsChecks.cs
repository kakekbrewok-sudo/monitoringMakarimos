using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;

// Arms its own session before the user/launcher opens the named game.
// Never launches, focuses, modifies or closes the game itself.
static class NativeGameFpsChecks
{
    static int Main(string[] args)
    {
        var log=new StringBuilder(); string report=args[1]; Process game=null;
        try
        {
            using(var reader=new FpsTrace())
            {
                log.AppendLine("READY: native FPS session prepared before game launch");
                log.AppendLine("Session: "+reader.SessionName);
                File.WriteAllText(report,log.ToString());
                for(int i=0;i<180&&game==null;i++)
                {
                    var matches=Process.GetProcessesByName(args[0]);
                    if(matches.Length>0)game=matches[0];
                    for(int j=1;j<matches.Length;j++)matches[j].Dispose();
                    if(game==null)Thread.Sleep(1000);
                }
                if(game==null)throw new Exception("Game was not opened within three minutes");
                var resolve=typeof(ActivityTracker).GetMethod("Resolve",BindingFlags.Instance|BindingFlags.NonPublic);
                var app=(AppIdentity)resolve.Invoke(new ActivityTracker(),new object[]{game.MainWindowHandle,(uint)game.Id});
                if(app==null||!new FpsRules().Tracks(app))throw new Exception("Game identity not automatically recognized");
                log.AppendLine("Game: "+app.Name+" | PID "+app.ProcessId);
                log.AppendLine("Executable: "+app.ExecutablePath);
                reader.SetTarget(app); int samples=0;
                for(int i=0;i<60&&samples<15;i++)
                {
                    Thread.Sleep(1000); if(game.HasExited)throw new Exception("Game exited during test");
                    double? fps=reader.Read(app.Key);
                    if(fps.HasValue){samples++;log.AppendLine("FPS "+fps.Value.ToString("0.00",CultureInfo.InvariantCulture));}
                    File.WriteAllText(report,log.ToString());
                }
                if(samples<5)throw new Exception("Insufficient real game FPS samples: "+samples);
                log.AppendLine("PASS: real game frames captured using a pre-created native session");
                reader.SetTarget(null); if(reader.Read(app.Key).HasValue)throw new Exception("Stale FPS after target cleared");
                Thread.Sleep(1200); reader.SetTarget(app);
                bool resumed=false;
                for(int i=0;i<10&&!resumed;i++){Thread.Sleep(1000);resumed=reader.Read(app.Key).HasValue;}
                if(!resumed)throw new Exception("FPS did not resume with the game still running");
                log.AppendLine("PASS: resume reuses existing session while EA game is running");
                log.AppendLine("ETW buffer bytes: "+reader.SessionBufferBytes);
                reader.SetTarget(null);
            }
            if(game.HasExited)throw new Exception("Game exited before verification finished");
            log.AppendLine("PASS: diagnostic session closed; game remains running");
            File.WriteAllText(report,log.ToString());return 0;
        }
        catch(Exception ex){File.WriteAllText(report,log+"\n"+ex);return 1;}
        finally{if(game!=null)game.Dispose();}
    }
}
