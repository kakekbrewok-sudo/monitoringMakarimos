using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;

static class NativeFpsChecks
{
    [STAThread] static int Main(string[] args)
    {
        if(args.Length>1 && args[1]=="--renderer")
            return (int)typeof(FpsLiveChecks).GetMethod("Renderer",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{args[0]});
        var log=new StringBuilder(); Process fixture=null; string folder=args[0];
        try
        {
            using(var self=Process.GetCurrentProcess())
            using(var reader=new FpsTrace())
            {
                log.AppendLine("PASS: native session prepared before launching a graphics process");
                log.AppendLine("Session: "+reader.SessionName);
                var idleCpu=self.TotalProcessorTime; Thread.Sleep(2500);
                log.AppendLine("Idle CPU seconds: "+(self.TotalProcessorTime-idleCpu).TotalSeconds.ToString("0.000",CultureInfo.InvariantCulture));
                log.AppendLine("ETW buffer bytes: "+reader.SessionBufferBytes);
                fixture=Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,"\""+folder+"\" --renderer") {UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden});
                for(int i=0;i<100&&!File.Exists(Path.Combine(folder,"renderer-ready.txt"));i++){if(fixture.HasExited)throw new Exception("Fixture failed");Thread.Sleep(100);}
                var app=new AppIdentity{ProcessId=fixture.Id,Key="fixture",OpenedUtc=fixture.StartTime.ToUniversalTime()};
                reader.SetTarget(app); int samples=0; double total=0; var activeCpu=self.TotalProcessorTime;
                for(int i=0;i<10;i++) {Thread.Sleep(1000);double? value=reader.Read(app.Key);if(value.HasValue){samples++;total+=value.Value;log.AppendLine("FPS "+value.Value.ToString("0.00",CultureInfo.InvariantCulture));}}
                if(samples<5||Math.Abs(total/samples-60)>5)throw new Exception("Expected 60 FPS from native capture, samples="+samples);
                log.AppendLine("PASS: Direct3D9 native FPS agrees with paced 60 FPS renderer");
                log.AppendLine("Active CPU seconds over 10 seconds: "+(self.TotalProcessorTime-activeCpu).TotalSeconds.ToString("0.000",CultureInfo.InvariantCulture));
                reader.SetTarget(null); if(reader.Read(app.Key).HasValue)throw new Exception("Stale FPS after deselect");
                Thread.Sleep(1500); reader.SetTarget(app); Thread.Sleep(3500);
                if(!reader.Read(app.Key).HasValue)throw new Exception("Capture did not resume on existing session");
                log.AppendLine("PASS: idle disables providers; returning to game reuses the same session");
                File.WriteAllText(Path.Combine(folder,"renderer-stop.txt"),"stop");
                if(!fixture.WaitForExit(3000))throw new Exception("Fixture failed to stop");
                Thread.Sleep(3500); if(reader.Read(app.Key).HasValue)throw new Exception("FPS did not expire after game exit");
                reader.SetTarget(null);
                log.AppendLine("PASS: game exit expires frames and clears target");
            }
            log.AppendLine("PASS: native session and consumer close cleanly");
            File.WriteAllText(Path.Combine(folder,"native-fps.txt"),log.ToString());return 0;
        }
        catch(Exception ex){File.WriteAllText(Path.Combine(folder,"native-fps.txt"),log+"\n"+ex);return 1;}
        finally
        {
            if(fixture!=null){File.WriteAllText(Path.Combine(folder,"renderer-stop.txt"),"stop");if(!fixture.WaitForExit(3000))fixture.Kill();fixture.Dispose();}
        }
    }
}
