using System;
using System.IO;
using System.Threading;

static class FpsStartupChecks
{
    static int Main(string[] args)
    {
        string log="";
        try
        {
            using(var monitor=new FpsMonitor())
            {
                monitor.Configure(true,null); Thread.Sleep(1800);
                log="Status: "+monitor.Status+Environment.NewLine;
                if(args.Length>1 && args[1]=="--blocked" && !monitor.Status.StartsWith("Tutup game;"))
                    throw new Exception("Expected clear EA blocked-session recovery status");
                if(monitor.Read("none").HasValue)throw new Exception("FPS without a target");
                log+="PASS: blocked or idle capture has no invented FPS"+Environment.NewLine;
                monitor.Configure(false,null);
                if(!monitor.StopAsync().Wait(8000))throw new Exception("Shutdown timed out");
                log+="PASS: FPS toggle and shutdown finish after startup failure"+Environment.NewLine;
            }
            File.WriteAllText(args[0],log);return 0;
        }
        catch(Exception ex){File.WriteAllText(args[0],log+ex);return 1;}
    }
}
