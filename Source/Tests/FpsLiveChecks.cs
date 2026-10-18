using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// A small Direct3D9 fixture, not a commercial game. Runs offscreen and never
// attaches to or injects into the user's applications.
static class FpsLiveChecks
{
    [StructLayout(LayoutKind.Sequential)] struct PresentParameters
    {
        public uint Width, Height, Format, Buffers, Multisample, Quality, Swap;
        public IntPtr Window; public int Windowed, AutoDepth;
        public uint DepthFormat, Flags, Refresh, Interval;
    }
    [DllImport("d3d9.dll")] static extern IntPtr Direct3DCreate9(uint version);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateDevice(IntPtr self,uint adapter,uint type,IntPtr focus,uint flags,ref PresentParameters settings,out IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int Present(IntPtr self,IntPtr source,IntPtr destination,IntPtr window,IntPtr dirty);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate uint Release(IntPtr self);
    static T Method<T>(IntPtr pointer,int slot) where T:class { return Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(pointer),slot*IntPtr.Size),typeof(T)) as T; }
    sealed class FixtureWindow:Form
    {
        public FixtureWindow() { ShowInTaskbar=false; StartPosition=FormStartPosition.Manual; Location=new Point(-12000,-12000); ClientSize=new Size(320,180); }
        protected override bool ShowWithoutActivation { get { return true; } }
    }
    static int Renderer(string folder)
    {
        IntPtr d3d=IntPtr.Zero, device=IntPtr.Zero;
        try
        {
            using(var form=new FixtureWindow())
            {
                form.Show(); d3d=Direct3DCreate9(32);
                if(d3d==IntPtr.Zero) throw new Exception("Direct3D9 unavailable");
                var p=new PresentParameters { Width=320,Height=180,Buffers=1,Swap=1,Window=form.Handle,Windowed=1,Interval=0x80000000 };
                Marshal.ThrowExceptionForHR(Method<CreateDevice>(d3d,16)(d3d,0,1,form.Handle,0x20,ref p,out device));
                File.WriteAllText(Path.Combine(folder,"renderer-ready.txt"),Process.GetCurrentProcess().Id.ToString());
                var present=Method<Present>(device,17); var clock=Stopwatch.StartNew(); long count=0;
                while(clock.Elapsed.TotalSeconds<65 && !File.Exists(Path.Combine(folder,"renderer-stop.txt")))
                {
                    Application.DoEvents();
                    int result=present(device,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);
                    if(result<0) Marshal.ThrowExceptionForHR(result);
                    count++;
                    while(clock.Elapsed.TotalSeconds<count/60.0) Thread.Sleep(1);
                }
                File.WriteAllText(Path.Combine(folder,"renderer-result.txt"),String.Format(CultureInfo.InvariantCulture,"Presents: {0}; seconds: {1:0.000}; actual average: {2:0.00}",count,clock.Elapsed.TotalSeconds,count/clock.Elapsed.TotalSeconds));
            }
            return 0;
        }
        catch(Exception ex) { File.WriteAllText(Path.Combine(folder,"renderer-error.txt"),ex.ToString());return 1; }
        finally { if(device!=IntPtr.Zero) Method<Release>(device,2)(device); if(d3d!=IntPtr.Zero) Method<Release>(d3d,2)(d3d); }
    }
    [STAThread] static int Main(string[] args)
    {
        if(args.Length>1 && args[1]=="--renderer") return Renderer(args[0]);
        string folder=args[0]; var log=new StringBuilder(); Process game=null;
        try
        {
            game=Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,"\""+folder+"\" --renderer") { UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden });
            for(int i=0;i<100&&!File.Exists(Path.Combine(folder,"renderer-ready.txt"));i++) { if(game.HasExited) throw new Exception("Renderer failed"); Thread.Sleep(100); }
            var target=new AppIdentity { ProcessId=game.Id, Key="fixture",OpenedUtc=game.StartTime.ToUniversalTime() };
            using(var reader=new FpsReader(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"fps","PresentMon.exe")))
            {
                var own=Process.GetCurrentProcess(); TimeSpan ownStart=own.TotalProcessorTime; var watch=Stopwatch.StartNew();
                reader.SetTarget(target); double sum=0; int samples=0, helperId=0; long peakMemory=0; TimeSpan helperCpu=TimeSpan.Zero;
                for(int i=0;i<18;i++)
                {
                    Thread.Sleep(1000); double? value=reader.Read(target.Key);
                    if(value.HasValue) { sum+=value.Value;samples++; log.AppendLine("FPS "+value.Value.ToString("0.00",CultureInfo.InvariantCulture)); }
                    helperId=reader.HelperId;
                    if(helperId>0) using(var helper=Process.GetProcessById(helperId)) { peakMemory=Math.Max(peakMemory,helper.WorkingSet64);helperCpu=helper.TotalProcessorTime; }
                }
                log.AppendLine("Status: "+reader.Status);
                if(samples<8 || Math.Abs(sum/samples-60)>5) throw new Exception("Expected live 60 FPS, received "+samples+" samples");
                log.AppendLine("PASS: real Direct3D9 frame capture agrees with paced 60 FPS fixture");
                log.AppendLine(String.Format(CultureInfo.InvariantCulture,"Helper peak working set: {0:0.0} MB; helper CPU averaged across all logical processors: {1:0.000}%; host CPU: {2:0.000}%",peakMemory/1048576.0,helperCpu.TotalSeconds/watch.Elapsed.TotalSeconds/Environment.ProcessorCount*100,(own.TotalProcessorTime-ownStart).TotalSeconds/watch.Elapsed.TotalSeconds/Environment.ProcessorCount*100));
                reader.SetTarget(null);
                if(reader.Read(target.Key).HasValue) throw new Exception("Stale result after leaving game");
                Thread.Sleep(5000);
                if(reader.HelperId!=0) throw new Exception("Helper still running after deselect");
                try { using(var previous=Process.GetProcessById(helperId)) if(!previous.HasExited) throw new Exception("Old helper survived stop"); } catch(ArgumentException) { }
                log.AppendLine("PASS: leaving game clears FPS and stops helper");
                reader.SetTarget(target); Thread.Sleep(4500);
                if(!reader.Read(target.Key).HasValue) throw new Exception("Resume failed: "+reader.Status);
                log.AppendLine("PASS: returning to game resumes frame capture");
                File.WriteAllText(Path.Combine(folder,"renderer-stop.txt"),"stop");
                if(!game.WaitForExit(3000)) throw new Exception("Renderer did not stop");
                for(int i=0;i<70 && reader.HelperId!=0;i++) Thread.Sleep(100);
                if(reader.HelperId!=0 || reader.Read(target.Key).HasValue) throw new Exception("Helper or stale FPS survived game exit");
                log.AppendLine("PASS: closing the game terminates capture and clears FPS");
                reader.StopAsync().GetAwaiter().GetResult();
                log.AppendLine("PASS: asynchronous shutdown completes");
                log.AppendLine("Session: "+reader.SessionName);
                own.Dispose();
            }
            File.WriteAllText(Path.Combine(folder,"fps-live.txt"),log.ToString());return 0;
        }
        catch(Exception ex) { File.WriteAllText(Path.Combine(folder,"fps-live.txt"),log+"\n"+ex);return 1; }
        finally
        {
            File.WriteAllText(Path.Combine(folder,"renderer-stop.txt"),"stop");
            if(game!=null) { if(!game.WaitForExit(3000)) game.Kill();game.Dispose(); }
        }
    }
}
