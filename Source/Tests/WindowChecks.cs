using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using System.Text;
class WindowChecks {
static BindingFlags F=BindingFlags.Instance|BindingFlags.NonPublic;
static void Set(object o,string n,object v){o.GetType().GetField(n,F).SetValue(o,v);}
static object Get(object o,string n){return o.GetType().GetField(n,F).GetValue(o);}
static void Call(object o,string n,params object[] a){o.GetType().GetMethod(n,F).Invoke(o,a);}
static StringBuilder log=new StringBuilder();
static void Check(bool b,string s){if(!b)throw new Exception(s);log.AppendLine("PASS: "+s);}
[STAThread] static int Main(string[] args){try{
Application.EnableVisualStyles();
using(var f=new MonitorWindow(false)){
Set(f,"settingsPath",Path.Combine(args[0],"settings-test.txt"));
((NotifyIcon)Get(f,"tray")).Visible=false;
var sample=new Snapshot{MemoryValid=true,Load=54,Used=17.1,Total=31.6};sample.Cpu.Load=35;sample.Cpu.Value=58;sample.Gpu.Load=98;sample.Gpu.Value=78;
sample.Disks.Add(new Reading("SSD","PNY CS2340 1TB SSD"){Value=54});
sample.Disks.Add(new Reading("SSD","XPG GAMMIX S70 BLADE"){Value=63});
sample.Disks.Add(new Reading("HDD","TOSHIBA MQ01ABD050"){Value=44});
Set(f,"data",sample);Set(f,"lastPoll",DateTime.Now);
var app=new AppIdentity{Name="CONTROL Resonant",Key="preview",ProcessId=-1,ExecutablePath=@"D:\Games\Control\Control.exe",OpenedUtc=DateTime.UtcNow.AddSeconds(-4508)};
var tracker=(ActivityTracker)Get(f,"activity");tracker.Advance(app,0,false);tracker.Selected.ActiveSeconds=2537;
((FpsRules)Get(f,"fpsRules")).Set(app,true);Set(f,"fpsEnabled",true);Set(f,"fpsValue",(double?)118);
Check(f.FormBorderStyle==FormBorderStyle.Sizable && f.MaximizeBox && f.ShowInTaskbar,"resizable window with maximize and taskbar");
foreach(MonitorTheme theme in Enum.GetValues(typeof(MonitorTheme))){Call(f,"SetTheme",theme,false);
foreach(Size size in new[]{new Size(1080,300),new Size(680,300),new Size(420,450),new Size(540,900),new Size(1000,1650),new Size(340,320)}){
f.ClientSize=size;Call(f,"ApplyLayout");Check(f.ClientSize==size,theme+" sensor refresh preserves "+size);
using(var bmp=new Bitmap(f.Width,f.Height)){f.DrawToBitmap(bmp,new Rectangle(Point.Empty,bmp.Size));bmp.Save(Path.Combine(args[0],theme+"-"+size.Width+".png"));}
Check((float)Get(f,"scale")*(size.Width>=680 && size.Width>=size.Height*2 ? 240 : 423) <= f.ClientSize.Height+1,"content fits height "+size);
}}
f.ClientSize=new Size(540,900);Call(f,"SaveSettings");Call(f,"SetOverlay",true);
Check(f.FormBorderStyle==FormBorderStyle.None && !f.MaximizeBox,"overlay remains borderless and compact");
Call(f,"SetOverlay",false);Check(f.ClientSize==new Size(540,900),"return from overlay restores window size");
Set(f,"windowSize",Size.Empty);Call(f,"ReadSettings");Check((Size)Get(f,"windowSize")==new Size(540,900),"saved window size reloads");
f.WindowState=FormWindowState.Maximized;Call(f,"ApplyLayout");Check(f.WindowState==FormWindowState.Maximized,"sensor refresh preserves maximize");
f.WindowState=FormWindowState.Normal;
}
File.WriteAllText(Path.Combine(args[0],"window-checks.txt"),log.ToString());return 0;
}catch(Exception e){File.WriteAllText(Path.Combine(args[0],"window-checks.txt"),log+"\nFAIL: "+e);return 1;}}
}
