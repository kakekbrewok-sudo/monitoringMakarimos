using System;
using System.IO;
using System.Security.Principal;
using System.Text;

// Silent short-lived fixture: no user application/window is opened by tests.
static class LauncherFixture
{
    static void Main(string[] args)
    {
        var text=new StringBuilder();
        using(var identity=WindowsIdentity.GetCurrent()) text.AppendLine("Admin="+new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator));
        text.AppendLine("Directory="+Environment.CurrentDirectory);
        foreach(string arg in args) text.AppendLine("Arg="+arg);
        File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"launched.txt"),text.ToString());
    }
}
