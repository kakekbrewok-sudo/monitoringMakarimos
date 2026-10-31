using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

static class LauncherShellChecks
{
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface ShellLink
    {
        void GetPath(); void GetIDList(); void SetIDList(); void GetDescription(); void SetDescription(); void GetWorkingDirectory();
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string path);
        void GetArguments(); void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(); void SetHotkey(); void GetShowCmd(); void SetShowCmd(); void GetIconLocation(); void SetIconLocation(); void SetRelativePath(); void Resolve();
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }
    [STAThread] static int Main(string[] args)
    {
        string output=Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
        var log=new StringBuilder();
        try {
            if(!Native.Admin) throw new Exception("Run this shell integration test elevated to verify normal-user launch.");
            string fixture=Path.Combine(output,"Fixture app & 日本.exe"), link=Path.Combine(output,"Favorite & 日本.lnk"), report=Path.Combine(output,"launched.txt");
            if(!File.Exists(fixture)) throw new Exception("Build LauncherFixture.cs to "+fixture);
            object shortcut=null;
            try {
                shortcut=Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046")));
                var definition=(ShellLink)shortcut;
                definition.SetPath(fixture); definition.SetArguments("\"value with spaces & = 日本\""); definition.SetWorkingDirectory(output);
                ((System.Runtime.InteropServices.ComTypes.IPersistFile)shortcut).Save(link,true);
            } finally { if(shortcut!=null) Marshal.ReleaseComObject(shortcut); }
            if(File.Exists(report)) File.Delete(report);
            LauncherShell.Open(link);
            for(int i=0;i<100 && !File.Exists(report);i++) Thread.Sleep(100);
            string text=File.ReadAllText(report);
            if(!text.Contains("Admin=False") || !text.Contains("Arg=value with spaces & = 日本") || !text.Contains("Directory="+output)) throw new Exception("Shortcut launch mismatch: "+text);
            log.AppendLine("PASS: elevated Mini Monitor launches shortcut as normal user; Unicode args and working directory preserved");
            File.Delete(report); LauncherShell.Open(fixture);
            for(int i=0;i<100 && !File.Exists(report);i++) Thread.Sleep(100);
            text=File.ReadAllText(report);
            if(!text.Contains("Admin=False") || text.Contains("Arg=")) throw new Exception("EXE launch mismatch: "+text);
            log.AppendLine("PASS: direct executable launch is also unelevated and receives no injected arguments");
            File.WriteAllText(Path.Combine(output,"launcher-shell-checks.txt"),log.ToString()); return 0;
        } catch(Exception ex) { File.WriteAllText(Path.Combine(output,"launcher-shell-checks.txt"),log+"\nFAIL: "+ex); return 1; }
    }
}
