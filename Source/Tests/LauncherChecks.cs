using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

static class LauncherChecks
{
    static readonly BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
    static readonly StringBuilder log = new StringBuilder();
    [DllImport("user32.dll")] static extern int GetGuiResources(IntPtr process, int kind);
    static object Get(object o, string name) { return o.GetType().GetField(name, flags).GetValue(o); }
    static void Set(object o, string name, object value) { o.GetType().GetField(name, flags).SetValue(o, value); }
    static object Call(object o, string name, params object[] args) { return o.GetType().GetMethod(name, flags).Invoke(o, args); }
    static void Check(bool ok, string text) { if (!ok) throw new Exception(text); log.AppendLine("PASS: " + text); }
    [STAThread] static int Main(string[] args)
    {
        string output=Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
        try {
            string app=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"System32","notepad.exe");
            var slots=new LauncherSlots();
            slots.Set(0,app); slots.Set(1,@"D:\Games\A & B;= 日本\Play game.lnk"); slots.Set(3,@"C:\Users\Test\Game.url");
            string saved=slots.Serialize(); slots.Restore(saved);
            Check(slots[0]==app && slots[1]==@"D:\Games\A & B;= 日本\Play game.lnk" && slots[2]==null && slots[3].EndsWith("Game.url"),"four slots round-trip Unicode, punctuation, missing files and empty positions");
            slots.Restore("%%%;;"+Convert.ToBase64String(Encoding.UTF8.GetBytes("https://example.com/")));
            Check(slots[0]==null && slots[1]==null && slots[2]==null,"malformed preferences and unsupported schemes stay empty");
            Check(!LauncherSlots.Supported("app.exe") && !LauncherSlots.Supported(@"C:\test.cmd") && LauncherSlots.Supported(@"C:\App.appref-ms"),"only application/shortcut paths are accepted");
            Application.EnableVisualStyles();
            using(var form=new MonitorWindow(false)) {
                Set(form,"settingsPath",Path.Combine(output,"launcher-settings.txt")); ((NotifyIcon)Get(form,"tray")).Visible=false;
                Set(form,"dpiScale",1f); Set(form,"windowMaximized",false);
                var data=new Snapshot {MemoryValid=true, Load=39, Used=12.6, Total=31.6};
                data.Cpu.Name="AMD Ryzen"; data.Cpu.Load=28; data.Cpu.Value=59;
                data.Gpu.Name="NVIDIA GeForce RTX 4060 Ti"; data.Gpu.Load=10; data.Gpu.Value=45;
                data.Disks.Add(new Reading("SSD","PNY CS2340 1TB SSD") {Value=45});
                data.Disks.Add(new Reading("SSD","XPG GAMMIX S70 BLADE") {Value=43});
                data.Disks.Add(new Reading("HDD","TOSHIBA MQ01ABD050") {Value=39});
                Set(form,"data",data); Set(form,"lastPoll",DateTime.Now); Call(form,"UpdateCooling");
                var tracker=(ActivityTracker)Get(form,"activity"); tracker.Advance(new AppIdentity {Key="launcher-fixture",Name="Codex",ProcessId=-1},0,false); tracker.Selected.ActiveSeconds=3647;
                Call(form,"SetLauncher",0,app); Call(form,"SetLauncher",1,Application.ExecutablePath); Call(form,"SetLauncher",2,app);
                var buttons=(LauncherButton[])Get(form,"launcherButtons");
                Check(buttons.Length==4 && buttons[0].AppImage!=null && buttons[3].AppImage==null,"three real cached icons and one empty plus slot");
                Bitmap cached=buttons[0].AppImage;
                foreach(MonitorTheme theme in Enum.GetValues(typeof(MonitorTheme))) for(int mode=0;mode<4;mode++) {
                    Call(form,"SetTheme",theme,false); Set(form,"overlay",mode==3); Set(form,"windowInitialized",false);
                    Set(form,"windowSize",mode==0?new Size(1080,360):mode==1?new Size(1080,300):new Size(340,423)); Call(form,"ApplyLayout");
                    using(var image=new Bitmap(form.Width,form.Height)) {
                        form.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));
                        using(Graphics g=Graphics.FromImage(image)) foreach(var button in buttons) using(var icon=new Bitmap(button.Width,button.Height)) {
                            button.DrawToBitmap(icon,new Rectangle(Point.Empty,icon.Size));
                            Point origin=form.PointToScreen(Point.Empty); g.DrawImageUnscaled(icon,origin.X-form.Left+button.Left,origin.Y-form.Top+button.Top);
                        }
                        image.Save(Path.Combine(output,theme+"-"+mode+".png"));
                    }
                    Rectangle reminder=(Rectangle)Get(form,"reminderActionBounds");
                    for(int i=0;i<4;i++) {
                        Check(form.ClientRectangle.Contains(buttons[i].Bounds),theme+" / "+mode+" / "+i+": icon stays inside client area");
                        Check(!buttons[i].Bounds.IntersectsWith(reminder) && !buttons[i].Bounds.IntersectsWith(((Control)Get(form,"coolingSleepButton")).Bounds) && !buttons[i].Bounds.IntersectsWith(((Control)Get(form,"coolingShutdownButton")).Bounds) && !buttons[i].Bounds.IntersectsWith(((Control)Get(form,"coolingButton")).Bounds),theme+" / "+mode+" / "+i+": icon is separated from reminder and power/cooling controls");
                        if(i>0) Check(buttons[i-1].Right<buttons[i].Left,theme+" / "+mode+": slots do not overlap");
                    }
                    Check(Object.ReferenceEquals(cached,buttons[0].AppImage),theme+" / "+mode+": painting/theme changes reuse cached icon");
                }
                Check(buttons[0].TabStop && buttons[0].AccessibleName.Contains("notepad"),"launcher supports keyboard focus and accessible app name");
                Check(!((ContextMenuStrip)Get(form,"launcherMenu")).ShowItemToolTips,"no hover tooltips");
                string before=File.ReadAllText((string)Get(form,"settingsPath"));
                ((LauncherSlots)Get(form,"launcherSlots")).Restore(""); Call(form,"ReadSettings");
                Check(((LauncherSlots)Get(form,"launcherSlots"))[0]==app,"favorites reload from user settings");
                using(var process=Process.GetCurrentProcess()) {
                    int handles=GetGuiResources(process.Handle,0);
                    for(int i=0;i<100;i++) { Call(form,"SetLauncher",0,app); Call(form,"SetLauncher",0,null); }
                    Check(GetGuiResources(process.Handle,0)<=handles+5,"replacing/removing favorites releases icon handles");
                }
                Check(File.Exists(app) && ((LauncherSlots)Get(form,"launcherSlots"))[0]==null,"remove only unpins app and preserves original executable");
                Check(tracker.Selected.ActiveSeconds==3647 && tracker.Selected.ReminderBaselineSeconds==0,"editing favorites does not alter activity/reminder time");
                Check(Get(form,"reader")==null && !((Timer)Get(form,"timer")).Enabled && !((Timer)Get(form,"clockTimer")).Enabled,"launcher adds no polling, sensor read or timer");
                // Client-only release preview from the actual application paint
                // path, including native child controls, not a separate mockup.
                string[] previewApps={@"C:\Program Files\Google\Chrome\Application\chrome.exe",Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"explorer.exe"),@"C:\Program Files (x86)\Steam\steam.exe",null};
                for(int i=0;i<4;i++) Call(form,"SetLauncher",i,previewApps[i]!=null && File.Exists(previewApps[i])?previewApps[i]:null);
                Set(form,"overlay",false); Set(form,"windowInitialized",false); Set(form,"windowSize",new Size(1080,360)); Call(form,"SetTheme",MonitorTheme.DotBlack,false); Call(form,"ApplyLayout");
                tracker.Selected.ActiveSeconds=47;
                using(var image=new Bitmap(form.ClientSize.Width,form.ClientSize.Height)) using(Graphics g=Graphics.FromImage(image)) {
                    g.Clear(form.BackColor); Call(form,"OnPaint",new PaintEventArgs(g,form.ClientRectangle));
                    foreach(Control control in form.Controls) using(var child=new Bitmap(control.Width,control.Height)) { control.DrawToBitmap(child,new Rectangle(Point.Empty,child.Size)); g.DrawImageUnscaled(child,control.Left,control.Top); }
                    image.Save(Path.Combine(output,"MiniMonitor-Launcher.png"));
                }
            }
            File.WriteAllText(Path.Combine(output,"launcher-checks.txt"),log.ToString()); return 0;
        } catch(Exception ex) { File.WriteAllText(Path.Combine(output,"launcher-checks.txt"),log+"\nFAIL: "+ex); return 1; }
    }
}
