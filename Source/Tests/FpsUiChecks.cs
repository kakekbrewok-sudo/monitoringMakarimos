using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

static class FpsUiChecks
{
    static readonly BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
    static readonly StringBuilder log=new StringBuilder();
    static object Get(object o,string n){return o.GetType().GetField(n,flags).GetValue(o);}
    static void Set(object o,string n,object value){o.GetType().GetField(n,flags).SetValue(o,value);}
    static void Call(object o,string n,params object[] args){o.GetType().GetMethod(n,flags).Invoke(o,args);}
    static void Check(bool ok,string message){if(!ok)throw new Exception("FAIL: "+message);log.AppendLine("PASS: "+message);}
    static void Click(ToolStripItem item){typeof(ToolStripItem).GetMethod("HandleClick",flags).Invoke(item,new object[]{EventArgs.Empty});}
    [STAThread] static int Main(string[] args)
    {
        if(args.Length<3||args[2]!="--child")return DesktopTestRunner.Run(args);
        int result=0;
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        using(var form=new MonitorWindow(true))
        {
            Set(form,"settingsPath",Path.Combine(args[0],"fps-ui-settings.txt"));
            Set(form,"clockSound",false);Set(form,"appSound",false);Set(form,"keepMenuOpen",true);
            ((Timer)Get(form,"timer")).Interval=1000;
            form.Shown+=async(s,e)=>
            {
                try
                {
                    for(int i=0;i<100&&(bool)Get(form,"busy");i++)await Task.Delay(100);
                    var app=new AppIdentity {Name="CONTROLResonant",Key="fps-fixture",ProcessId=-1,ExecutablePath=@"D:\Games\CONTROL Resonant\CONTROLResonant.exe",OpenedUtc=DateTime.UtcNow.AddHours(-1)};
                    var tracker=(ActivityTracker)Get(form,"activity");tracker.Advance(app,0,false);tracker.Selected.ActiveSeconds=2540;
                    var tray=(NotifyIcon)Get(form,"tray");Call(tray,"ShowContextMenu");
                    var menu=(ContextMenuStrip)Get(form,"menu");var fpsMenu=(ToolStripMenuItem)Get(form,"fpsMenu");
                    fpsMenu.ShowDropDown();Application.DoEvents();
                    var enabled=(ToolStripMenuItem)Get(form,"fpsEnabledItem");var appChoice=(ToolStripMenuItem)Get(form,"fpsAppItem");
                    Check(appChoice.Checked,"automatic custom Games installation checked in FPS menu");
                    IntPtr handle=form.Handle;Rectangle bounds=form.Bounds;
                    DateTime previous=(DateTime)Get(form,"lastPoll");await Task.Delay(3400);
                    Check((DateTime)Get(form,"lastPoll")>previous && menu.Visible && fpsMenu.DropDown.Visible,"FPS submenu survives live sensor and clock updates");
                    Click(appChoice);Check(!appChoice.Checked&&menu.Visible&&fpsMenu.DropDown.Visible,"disable game without dismissing menu");
                    Click(appChoice);Check(appChoice.Checked&&menu.Visible&&fpsMenu.DropDown.Visible,"enable game without dismissing menu");
                    Click(enabled);Check(!enabled.Checked&&menu.Visible,"FPS off keeps menu open");
                    Click(enabled);Check(enabled.Checked&&menu.Visible,"FPS on keeps menu open");
                    Check(form.Handle==handle&&form.Bounds==bounds,"FPS toggles do not resize, move or recreate overlay");
                    string settings=File.ReadAllText(Path.Combine(args[0],"fps-ui-settings.txt"));
                    Check(settings.Contains("fpsEnabled=1")&&settings.Contains("fpsApps="),"FPS selections saved alongside existing preferences");
                    Call(form,"ReadSettings");Check(((FpsRules)Get(form,"fpsRules")).Tracks(app),"game preference restores through settings parser");
                    menu.Close();((Timer)Get(form,"clockTimer")).Stop();
                    tracker.Advance(app,0,false);var snapshot=new Snapshot {MemoryValid=true,Load=38,Used=12,Total=31.6};
                    snapshot.Cpu.Load=58;snapshot.Cpu.Value=46;snapshot.Gpu.Load=86;snapshot.Gpu.Value=61;
                    snapshot.Disks.Add(new Reading("SSD","PNY CS2340"){Value=42});snapshot.Disks.Add(new Reading("SSD","XPG GAMMIX S70 BLADE"){Value=46});
                    Set(form,"data",snapshot);Set(form,"lastPoll",DateTime.Now);Call(form,"ApplyLayout");
                    foreach(MonitorTheme theme in Enum.GetValues(typeof(MonitorTheme)))
                    {
                        Call(form,"SetTheme",theme,false);Set(form,"fpsValue",144.0);
                        using(var image=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));image.Save(Path.Combine(args[0],ThemeCatalog.Keys[(int)theme]+"-fps.png"));}
                        app.Name="Very long game name to verify space for FPS";Set(form,"fpsValue",9999.0);
                        using(var image=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));image.Save(Path.Combine(args[0],ThemeCatalog.Keys[(int)theme]+"-fps-stress.png"));}
                        app.Name="CONTROLResonant";
                    }
                    Check(!menu.ShowItemToolTips,"no hover tooltips added");
                    log.AppendLine("PASS: four themes rendered with simulated FPS and long-name cases");
                }
                catch(Exception ex){result=1;log.AppendLine(ex.ToString());}
                finally{form.Close();}
            };
            Application.Run(form);
        }
        File.WriteAllText(Path.Combine(args[0],args[1]),log.ToString());return result;
    }
}
