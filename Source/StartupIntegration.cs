using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;

static class StartupIntegration
{
    public static string TaskName { get { using (var identity = WindowsIdentity.GetCurrent()) return "MiniMonitor-" + identity.User.Value; } }
    static void Release(object obj) { if (obj != null && Marshal.IsComObject(obj)) Marshal.FinalReleaseComObject(obj); }
    public static bool IsEnabled()
    {
        object service = null, folder = null, task = null;
        try
        {
            dynamic s = service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")); s.Connect();
            dynamic f = folder = s.GetFolder("\\"); dynamic t = task = f.GetTask(TaskName); return (bool)t.Enabled;
        }
        catch { return false; }
        finally { Release(task); Release(folder); Release(service); }
    }
    public static void Toggle()
    {
        object service = null, folder = null, task = null;
        try
        {
            dynamic s = service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")); s.Connect();
            dynamic f = folder = s.GetFolder("\\"); dynamic t = task = f.GetTask(TaskName); t.Enabled = !(bool)t.Enabled;
        }
        catch (FileNotFoundException ex)
        {
            throw new InvalidOperationException("Pasang Mini Monitor melalui installer atau Pasang-Otomatis.cmd untuk menyiapkan startup Windows.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException("Jalankan Mini Monitor sebagai administrator untuk mengubah startup Windows.", ex);
        }
        catch (COMException ex)
        {
            if (ex.ErrorCode == unchecked((int)0x80070005))
                throw new InvalidOperationException("Jalankan Mini Monitor sebagai administrator untuk mengubah startup Windows.", ex);
            throw new InvalidOperationException("Pasang Mini Monitor melalui installer atau Pasang-Otomatis.cmd untuk menyiapkan startup Windows.", ex);
        }
        finally { Release(task); Release(folder); Release(service); }
    }
}
