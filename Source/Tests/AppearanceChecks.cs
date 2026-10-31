using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

static class AppearanceChecks
{
    static readonly BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
    static readonly StringBuilder log=new StringBuilder();
    static object Get(object o,string name) { return o.GetType().GetField(name,flags).GetValue(o); }
    static void Set(object o,string name,object value) { o.GetType().GetField(name,flags).SetValue(o,value); }
    static object Call(object o,string name,params object[] args) { return o.GetType().GetMethod(name,flags).Invoke(o,args); }
    static void Check(bool ok,string text) { if(!ok) throw new Exception(text); log.AppendLine("PASS: "+text); }
    [STAThread] static int Main(string[] args)
    {
        string output=Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
        if(args.Length<3 || args[2]!="--child") return DesktopTestRunner.Run(args);
        try {
            Application.EnableVisualStyles();
            using(var form=new MonitorWindow(false)) {
                Set(form,"settingsPath",Path.Combine(output,"appearance-settings.txt")); Set(form,"fpsEnabled",false); Set(form,"clockSound",false); Set(form,"appSound",false); ((NotifyIcon)Get(form,"tray")).Visible=false;
                Set(form,"dpiScale",1f); Set(form,"windowMaximized",false);
                var sample=new Snapshot {MemoryValid=true,Load=43,Used=13.6,Total=31.6};
                sample.Cpu.Name="AMD Ryzen";sample.Cpu.Load=28;sample.Cpu.Value=55;sample.Gpu.Name="NVIDIA GPU";sample.Gpu.Load=10;sample.Gpu.Value=48;
                sample.Disks.Add(new Reading("SSD","PNY CS2340 1TB SSD") {Value=44});
                sample.Disks.Add(new Reading("SSD","XPG GAMMIX S70 BLADE") {Value=41});
                sample.Disks.Add(new Reading("HDD","TOSHIBA MQ01ABD050") {Value=42});
                Set(form,"data",sample);Set(form,"lastPoll",DateTime.Now);Call(form,"UpdateCooling");
                var tracker=(ActivityTracker)Get(form,"activity");tracker.Advance(new AppIdentity {Key="layout-fixture",Name="Windows Explorer",ProcessId=-1,OpenedUtc=DateTime.UtcNow.AddMinutes(-22)},0,false);
                tracker.Selected.ActiveSeconds=3611;
                string app=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"explorer.exe");
                for(int i=0;i<4;i++) Call(form,"SetLauncher",i,app);
                var buttons=(LauncherButton[])Get(form,"launcherButtons");var tip=(ToolTip)Get(form,"launcherTip");
                foreach(MonitorTheme theme in Enum.GetValues(typeof(MonitorTheme))) foreach(int height in new[]{300,369,450}) {
                    Call(form,"SetTheme",theme,false);Set(form,"overlay",false);Set(form,"windowInitialized",false);Set(form,"windowSize",new Size(1080,height));Call(form,"ApplyLayout");
                    using(var bitmap=new Bitmap(form.ClientSize.Width,form.ClientSize.Height)) using(Graphics g=Graphics.FromImage(bitmap)) {
                        g.Clear(form.BackColor);Call(form,"OnPaint",new PaintEventArgs(g,form.ClientRectangle));
                        foreach(Control control in form.Controls) using(var child=new Bitmap(control.Width,control.Height)) { control.DrawToBitmap(child,new Rectangle(Point.Empty,child.Size));g.DrawImageUnscaled(child,control.Left,control.Top); }
                        bitmap.Save(Path.Combine(output,theme+"-"+height+".png"));
                    }
                    Rectangle action=(Rectangle)Get(form,"reminderActionBounds");
                    Check(action.Bottom>form.ClientSize.Height*.85 && form.ClientRectangle.Contains(action),theme+" / "+height+": reminder fills lower panel and remains inside window");
                    foreach(var button in buttons) {
                        Check(form.ClientRectangle.Contains(button.Bounds),theme+" / "+height+": favorite stays inside panel");
                        using(var image=new Bitmap(button.Width,button.Height)) {
                            button.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));
                            Color surface=button.Surface;
                            Check(image.GetPixel(0,0).ToArgb()==surface.ToArgb() && image.GetPixel(button.Width-1,button.Height-1).ToArgb()==surface.ToArgb(),theme+" / "+height+": icon has no outer border");
                        }
                        int normal=button.IconSide; Rectangle bounds=button.Bounds; Bitmap icon=button.AppImage;
                        Call(button,"OnMouseEnter",EventArgs.Empty);
                        Check(button.IconSide>normal && button.Bounds==bounds && Object.ReferenceEquals(icon,button.AppImage),theme+" / "+height+": hover enlarges cached icon without moving click target");
                        Call(button,"OnMouseLeave",EventArgs.Empty);Check(button.IconSide==normal,"leaving icon restores normal size");
                        Check(tip.GetToolTip(button)=="explorer" && tip.OwnerDraw,"only icon hover shows a themed application name");
                    }
                }
                Call(form,"SetTheme",MonitorTheme.DotBlack,false); Set(form,"overlay",false);Set(form,"windowInitialized",false);Set(form,"windowSize",new Size(1078,369));Call(form,"ApplyLayout");
                form.Location=new Point(-1087,0);Call(form,"SaveSettings");string path=(string)Get(form,"settingsPath");
                string before=File.ReadAllText(path);
                form.WindowState=FormWindowState.Minimized;Call(form,"SaveSettings");string minimized=File.ReadAllText(path);
                Check(minimized.Contains("x=-1087") && minimized.Contains("y=0") && minimized.Contains("windowWidth=1078") && minimized.Contains("windowHeight=369"),"minimize preserves desktop position and exact wide-window size");
                form.WindowState=FormWindowState.Normal;Set(form,"windowSize",Size.Empty);Set(form,"overlay",true);Set(form,"windowInitialized",false);Call(form,"ReadSettings");Call(form,"ApplyLayout");
                Check(!(bool)Get(form,"overlay") && form.ClientSize==new Size(1078,369) && form.Location==new Point(-1087,0),"reopening restores saved mode, size and portrait-monitor position");
                form.WindowState=FormWindowState.Maximized;Call(form,"CaptureWindowLayout");form.WindowState=FormWindowState.Minimized;Call(form,"RestoreWindow");
                Check(form.WindowState==FormWindowState.Maximized,"tray restore retains maximized state");
                foreach(string field in new[]{"coolingButton","coolingSleepButton","coolingShutdownButton"}) {
                    var button=(CoolingButton)Get(form,field); Check(button.Focus() && button.Focused,field+": focus test does not click or activate hardware");
                    using(var image=new Bitmap(button.Width,button.Height)) {
                        button.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size)); Color surface=button.Surface;
                        bool clear=true;
                        for(int x=2;x<button.Width/4;x++) if(image.GetPixel(x,2).ToArgb()!=surface.ToArgb() || image.GetPixel(2,x).ToArgb()!=surface.ToArgb() || image.GetPixel(button.Width-3,x).ToArgb()!=surface.ToArgb()) clear=false;
                        Check(clear,field+": focused control has no white dotted box");
                        image.Save(Path.Combine(output,field+"-focused.png"));
                    }
                }
                form.Hide();form.WindowState=FormWindowState.Normal;
            }
            File.WriteAllText(Path.Combine(output,"appearance-checks.txt"),log.ToString());return 0;
        } catch(Exception ex) {File.WriteAllText(Path.Combine(output,"appearance-checks.txt"),log+"\nFAIL: "+ex);return 1;}
    }
}
