using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

static class FireworksChecks
{
    static readonly BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
    static readonly StringBuilder log = new StringBuilder();
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern int GetGuiResources(IntPtr process, int kind);
    static object Get(object o, string name) { return o.GetType().GetField(name, flags).GetValue(o); }
    static void Set(object o, string name, object value) { o.GetType().GetField(name, flags).SetValue(o, value); }
    static object Call(object o, string name, params object[] args) { return o.GetType().GetMethod(name, flags).Invoke(o, args); }
    static void Check(bool ok, string text) { if (!ok) throw new Exception(text); log.AppendLine("PASS: " + text); }
    static bool Running(MonitorWindow form) { return ((Timer)Get(form, "fireworksTimer")).Enabled; }
    static Bitmap Render(MonitorWindow form) {
        var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); return bitmap;
    }
    static void GeometryChecks(string output) {
        using (var effect = new PixelFireworks()) {
            Check(PixelFireworks.ParticleCount == 96, "particle storage is bounded to 96");
            foreach (Size size in new[] {new Size(1080,300), new Size(312,423)}) {
                using (var image = new Bitmap(size.Width,size.Height)) using (Graphics g=Graphics.FromImage(image)) {
                    g.Clear(Color.Black); effect.Paint(g,size,0,Color.Red,Color.White);
                    int minX=size.Width, maxX=0, minY=size.Height, maxY=0;
                    for(int y=0;y<size.Height;y++) for(int x=0;x<size.Width;x++) if(image.GetPixel(x,y).ToArgb()!=Color.Black.ToArgb()) { minX=Math.Min(minX,x);maxX=Math.Max(maxX,x);minY=Math.Min(minY,y);maxY=Math.Max(maxY,y); }
                    Check(maxX-minX<=12 && maxY-minY<=12, size+": particles originate at center");
                    g.Clear(Color.Black); effect.Paint(g,size,2.4,Color.Red,Color.White);
                    minX=size.Width; maxX=0; minY=size.Height; maxY=0;
                    for(int y=0;y<size.Height;y++) for(int x=0;x<size.Width;x++) if(image.GetPixel(x,y).ToArgb()!=Color.Black.ToArgb()) { minX=Math.Min(minX,x);maxX=Math.Max(maxX,x);minY=Math.Min(minY,y);maxY=Math.Max(maxY,y); }
                    Check(maxX-minX>size.Width*.8 && maxY-minY>size.Height*.7,size+": expansion fills width and height");
                    g.Clear(Color.Black); effect.Paint(g,size,6,Color.Red,Color.White);
                    bool empty=true;
                    for(int y=0;y<size.Height;y++) for(int x=0;x<size.Width;x++) if(image.GetPixel(x,y).ToArgb()!=Color.Black.ToArgb()) empty=false;
                    Check(empty,size+": exactly six seconds draws no particles");
                }
            }
        }
    }
    static async Task Run(MonitorWindow form, string output) {
        for(int i=0;i<150 && (bool)Get(form,"busy");i++) await Task.Delay(100);
        Check(!(bool)Get(form,"busy"),"initial sensor read completes");
        ((Timer)Get(form,"timer")).Stop(); ((Timer)Get(form,"clockTimer")).Stop();
        var tracker=(ActivityTracker)Get(form,"activity");
        var app=new AppIdentity {Key="fireworks-fixture",Name="Game fixture",ProcessId=-1};
        tracker.Advance(app,0,false);
        var data=new Snapshot {MemoryValid=true, Load=36, Used=11.4, Total=31.6};
        data.Cpu.Name="CPU fixture"; data.Cpu.Load=59; data.Cpu.Value=63;
        data.Gpu.Name="NVIDIA fixture"; data.Gpu.Load=8; data.Gpu.Value=41;
        data.Disks.Add(new Reading("SSD","SSD fixture") {Value=39});
        Set(form,"data",data); Set(form,"lastPoll",DateTime.Now); Call(form,"UpdateCooling");
        Set(form,"pixelReminder",true); Set(form,"clockSound",false); Set(form,"appSound",false);
        Check(!Running(form),"animation timer starts idle");
        tracker.Selected.ActiveSeconds=3598;
        tracker.Advance(app,1,false); Call(form,"HandleVisualReminder",tracker.ActiveHourReached);
        Check(!Running(form),"no early effect at 59:59");
        tracker.Advance(app,1,false); Call(form,"HandleVisualReminder",tracker.ActiveHourReached);
        Check(Running(form),"hour milestone triggers even with both sounds muted");
        Call(form,"AcknowledgeReminder");
        Check(!Running(form) && tracker.Selected.ActiveSeconds==3600 && !tracker.Selected.ReminderDue,"acknowledgement stops effect and preserves active total");
        tracker.Advance(app,1,false); Call(form,"HandleVisualReminder",tracker.ActiveHourReached);
        Check(!Running(form),"next tick does not retrigger");
        tracker.Selected.ActiveSeconds=7199;
        tracker.Advance(app,1,false); Call(form,"HandleVisualReminder",tracker.ActiveHourReached);
        Check(Running(form),"next full hour since acknowledgement triggers again"); Call(form,"StopFireworks");
        var other=new AppIdentity {Key="other",Name="Other",ProcessId=-2};
        tracker.Advance(other,1,false); tracker.Advance(app,1,false); Call(form,"HandleVisualReminder",tracker.ActiveHourReached);
        Check(!Running(form),"switching applications does not replay overdue effect");
        tracker.Selected.ActiveSeconds=10800;
        foreach(MonitorTheme theme in Enum.GetValues(typeof(MonitorTheme))) for(int mode=0;mode<3;mode++) {
            Call(form,"SetTheme",theme,false); Set(form,"overlay",mode==2); Set(form,"windowInitialized",false); Set(form,"windowMaximized",false);
            Set(form,"windowSize",mode==0?new Size(1080,300):new Size(340,423)); Call(form,"ApplyLayout");
            using(var bitmap=Render(form)) {
                Color ink=theme==MonitorTheme.DotBlack?Color.FromArgb(245,245,240):Color.FromArgb(28,28,27);
                using(Graphics g=Graphics.FromImage(bitmap)) ((PixelFireworks)Get(form,"fireworks")).Paint(g,form.ClientSize,2.4,(Color)Get(form,"accent"),ink);
                bitmap.Save(Path.Combine(output,theme+"-"+mode+"-fireworks.png"));
            }
            Rectangle hit=(Rectangle)Get(form,"reminderActionBounds");
            Call(form,"StartFireworks",true);
            Check(Running(form) && (bool)Call(form,"TryAcknowledgeReminder",new Point(hit.Left+hit.Width/2,hit.Top+hit.Height/2)) && !Running(form),theme+" / "+mode+": reminder remains clickable during animation");
            tracker.Selected.ReminderBaselineSeconds=0;
        }
        Set(form,"overlay",false); Set(form,"windowInitialized",false); Set(form,"windowSize",new Size(1080,300)); Call(form,"ApplyLayout");
        form.Hide(); Call(form,"HandleVisualReminder",true); Check(!Running(form),"hidden milestone is skipped"); form.Show();
        Check(!Running(form),"restoring window does not replay skipped milestone");
        Call(form,"StartFireworks",true); form.Hide(); Check(!Running(form),"hiding cancels a running effect"); form.Show();
        Call(form,"StartFireworks",true); form.WindowState=FormWindowState.Minimized; Check(!Running(form),"minimizing cancels running effect"); Call(form,"HandleVisualReminder",true); Check(!Running(form),"minimized milestone is skipped"); form.WindowState=FormWindowState.Normal;
        Set(form,"sessionLocked",true); Call(form,"HandleVisualReminder",true); Check(!Running(form),"locked milestone is skipped"); Set(form,"sessionLocked",false);
        Call(form,"StartFireworks",true); Set(form,"powerSuspended",true); Call(form,"AdvanceFireworks",1.0); Check(!Running(form),"suspend stops animation"); Set(form,"powerSuspended",false);
        var toggle=(ToolStripMenuItem)Get(form,"pixelReminderItem");
        toggle.PerformClick(); Check(!(bool)Get(form,"pixelReminder") && !Running(form),"tray toggle disables effect");
        Set(form,"pixelReminder",true); Call(form,"ReadSettings"); Check(!(bool)Get(form,"pixelReminder"),"disabled setting persists");
        Call(form,"HandleVisualReminder",true); Check(!Running(form),"disabled effect ignores milestones");
        Call(form,"StartFireworks",true); Check(Running(form),"explicit preview works with automatic effect disabled"); Call(form,"StopFireworks");
        toggle.PerformClick(); Check((bool)Get(form,"pixelReminder"),"tray toggle re-enables effect");
        var menu=(ContextMenuStrip)Get(form,"menu");
        Set(form,"keepMenuOpen",true);
        Call(Get(form,"tray"),"ShowContextMenu");
        Check(menu.Visible,"actual tray menu opens before animation");
        var watch=Stopwatch.StartNew(); IntPtr focus=GetForegroundWindow(), handle=form.Handle; Rectangle bounds=form.Bounds;
        using(var process=Process.GetCurrentProcess()) {
            int paints=0; PaintEventHandler counter=(s,e)=>paints++; form.Paint+=counter;
            TimeSpan cpu=process.TotalProcessorTime; long ram=process.PrivateMemorySize64;
            int gui=GetGuiResources(process.Handle,0);
            double total=tracker.Selected.ActiveSeconds, baseline=tracker.Selected.ReminderBaselineSeconds;
            Call(form,"StartFireworks",true);
            await Task.Delay(2500);
            Check(Running(form) && menu.Visible,"animation and tray menu survive multiple clock seconds");
            Call(form,"StartFireworks",true); // must not restart the six-second clock
            while(Running(form) && watch.Elapsed.TotalSeconds<7) await Task.Delay(25);
            Check(!Running(form) && watch.Elapsed.TotalSeconds>=6 && watch.Elapsed.TotalSeconds<6.5,"timer stops at six seconds; duplicate requests do not extend effect");
            Check(form.Handle==handle && form.Bounds==bounds && GetForegroundWindow()==focus && menu.Visible,"animation preserves focus, handle, bounds and tray menu");
            Check(total==tracker.Selected.ActiveSeconds && baseline==tracker.Selected.ReminderBaselineSeconds,"preview does not change activity or reminder baseline");
            Check(!((Timer)Get(form,"timer")).Enabled && !((Timer)Get(form,"clockTimer")).Enabled,"animation does not start sensor or clock timers");
            double cpuMs=(process.TotalProcessorTime-cpu).TotalMilliseconds;
            process.Refresh(); log.AppendLine("MEASURE: six-second animation CPU="+cpuMs.ToString("0.0")+" ms; private memory delta="+(process.PrivateMemorySize64-ram)+" bytes; paints="+paints);
            Check(paints>=90 && paints<=220,"animation paints at a bounded rate near 30 FPS");
            int settled=paints; await Task.Delay(200); Check(paints<=settled+1,"no animation repaint loop after completion");
            for(int i=0;i<100;i++) { Call(form,"StartFireworks",true); using(var bitmap=Render(form)) {} Call(form,"AdvanceFireworks",6.0); }
            Check(GetGuiResources(process.Handle,0)<=gui+10,"repeated effects do not leak GDI handles");
            form.Paint-=counter;
        }
        menu.Close();
        int clockTicks=0, sensorTicks=0;
        var clockTimer=(Timer)Get(form,"clockTimer"); var sensorTimer=(Timer)Get(form,"timer");
        clockTimer.Tick+=(s,e)=>clockTicks++; sensorTimer.Tick+=(s,e)=>sensorTicks++;
        sensorTimer.Interval=1000;
        Call(Get(form,"tray"),"ShowContextMenu"); clockTimer.Start(); sensorTimer.Start();
        DateTime lastPoll=(DateTime)Get(form,"lastPoll");
        Call(form,"StartFireworks",true); await Task.Delay(6300);
        clockTimer.Stop(); sensorTimer.Stop();
        Check(clockTicks>=5 && clockTicks<=7 && sensorTicks>=5 && sensorTicks<=7 && (DateTime)Get(form,"lastPoll")>lastPoll,"live clock and sensors retain their own polling rate during animation");
        Check(menu.Visible && !Running(form),"tray menu remains usable across live polling and animation completion"); menu.Close();
        Call(form,"StartFireworks",true); Set(form,"closing",true); Call(form,"AdvanceFireworks",1.0); Check(!Running(form),"closing stops effect"); Set(form,"closing",false);
    }
    [STAThread] static int Main(string[] args) {
        string output=Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
        if(args.Length<3 || args[2]!="--child") return DesktopTestRunner.Run(args);
        int code=0;
        try {
            GeometryChecks(output); Application.EnableVisualStyles();
            using(var form=new MonitorWindow(false)) {
                Set(form,"settingsPath",Path.Combine(output,"fireworks-settings.txt")); Set(form,"fpsEnabled",false); Set(form,"clockSound",false); Set(form,"appSound",false); Set(form,"dpiScale",1f);
                form.Shown+=async (s,e)=> { try { await Run(form,output); } catch(Exception ex) {code=1;log.AppendLine("FAIL: "+ex);} finally {File.WriteAllText(Path.Combine(output,"fireworks-checks.txt"),log.ToString());form.Close();} };
                Application.Run(form);
            }
        } catch(Exception ex) {code=1;log.AppendLine("FAIL: "+ex); File.WriteAllText(Path.Combine(output,"fireworks-checks.txt"),log.ToString());}
        return code;
    }
}
