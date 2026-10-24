using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
static class ReminderUiChecks {
    static BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
    static void Set(object o,string n,object v) { o.GetType().GetField(n,flags).SetValue(o,v); }
    static object Get(object o,string n) { return o.GetType().GetField(n,flags).GetValue(o); }
    static object Call(object o,string n,params object[] a) { return o.GetType().GetMethod(n,flags).Invoke(o,a); }
    static StringBuilder log=new StringBuilder();
    static void Check(bool ok,string message) { if(!ok) throw new Exception(message); log.AppendLine("PASS: "+message); }
    [STAThread] static int Main(string[] args) {
        string output=Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
        try {
            Application.EnableVisualStyles();
            using(var form=new MonitorWindow(false)) {
                Set(form,"settingsPath",Path.Combine(output,"settings-test.txt"));
                ((NotifyIcon)Get(form,"tray")).Visible=false;
                Set(form,"dpiScale",1f); Set(form,"locked",false);
                var data=new Snapshot { MemoryValid=true, Load=36, Used=11.4, Total=31.6 };
                data.Cpu.Name="AMD Ryzen"; data.Cpu.Load=59; data.Cpu.Value=63;
                data.Gpu.Name="NVIDIA RTX 3060"; data.Gpu.Load=8; data.Gpu.Value=41;
                data.Disks.Add(new Reading("SSD","PNY CS2340 1TB SSD") {Value=39});
                data.Disks.Add(new Reading("SSD","XPG GAMMIX S70 BLADE") {Value=37});
                data.Disks.Add(new Reading("HDD","TOSHIBA MQ01ABD050") {Value=38});
                Set(form,"data",data); Set(form,"lastPoll",DateTime.Now);
                var tracker=(ActivityTracker)Get(form,"activity");
                tracker.Advance(new AppIdentity {Key="fixture",Name="Google Chrome",ProcessId=-1,OpenedUtc=DateTime.UtcNow.AddHours(-4)},0,false);
                foreach(MonitorTheme theme in Enum.GetValues(typeof(MonitorTheme))) for(int mode=0;mode<3;mode++) {
                    Call(form,"SetTheme",theme,false);
                    Set(form,"overlay",mode==2); Set(form,"windowInitialized",false); Set(form,"windowMaximized",false);
                    Set(form,"windowSize",mode==0?new Size(1080,300):new Size(340,423)); Call(form,"ApplyLayout");
                    tracker.Selected.ActiveSeconds=5211; tracker.Selected.ReminderBaselineSeconds=0;
                    string name=theme+"-"+mode;
                    using(var bitmap=new Bitmap(form.Width,form.Height)) { form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size)); bitmap.Save(Path.Combine(output,name+"-before.png")); }
                    Rectangle hit=(Rectangle)Get(form,"reminderActionBounds");
                    Check(!hit.IsEmpty && form.ClientRectangle.Contains(hit),name+": reminder action is visible and inside window");
                    float scale=(float)Get(form,"scale");
                    int barY=mode==0 ? (theme==MonitorTheme.GShock?203:204) : (theme==MonitorTheme.KanjiArt?283:theme==MonitorTheme.GShock?261:247);
                    Check(hit.Contains(new Point(hit.Left+hit.Width/2,(int)Math.Round(barY*scale)+1)),name+": reminder bar is also clickable");
                    Check(!(bool)Call(form,"TryAcknowledgeReminder",new Point(-1,-1)),name+": clicks outside action preserve reminder");
                    Point target=new Point(hit.Left+hit.Width/2,hit.Top+hit.Height/2);
                    Set(form,"locked",true); Check(!(bool)Call(form,"TryAcknowledgeReminder",target),name+": locked overlay does not acknowledge"); Set(form,"locked",false);
                    Check((bool)Call(form,"TryAcknowledgeReminder",target),name+": direct click acknowledges without popup");
                    Check(tracker.Selected.ActiveSeconds==5211 && tracker.Selected.ReminderProgress==0 && !tracker.Selected.ReminderDue,name+": active total survives and progress resets");
                    using(var bitmap=new Bitmap(form.Width,form.Height)) { form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size)); bitmap.Save(Path.Combine(output,name+"-after.png")); }
                    Check(((Rectangle)Get(form,"reminderActionBounds")).IsEmpty,name+": acknowledgement target clears when not due");
                }
                Check(Get(form,"reader")==null && !((Timer)Get(form,"timer")).Enabled,"preview does not start sensor polling");
            }
            File.WriteAllText(Path.Combine(output,"reminder-ui-checks.txt"),log.ToString()); return 0;
        } catch(Exception ex) {File.WriteAllText(Path.Combine(output,"reminder-ui-checks.txt"),log+"\nFAIL: "+ex); return 1;}
    }
}
