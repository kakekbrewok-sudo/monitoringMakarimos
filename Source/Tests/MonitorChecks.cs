using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

static class MonitorChecks
{
    static readonly StringBuilder log = new StringBuilder();
    static void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL: " + name); log.AppendLine("PASS: " + name); }
    static object Field(object o, string name) { return o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(o); }
    static object Call(object o, string name, params object[] args) { return o.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(o, args); }
    static void Pump(Task t)
    {
        var watch = Stopwatch.StartNew();
        while (!t.IsCompleted && watch.ElapsedMilliseconds < 45000) { Application.DoEvents(); Thread.Sleep(20); }
        if (!t.IsCompleted) throw new TimeoutException("Sensor poll timed out"); t.GetAwaiter().GetResult();
    }
    [STAThread] static int Main(string[] args)
    {
        string output = args[0];
        try
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            Check(!SensorReader.Valid(0) && !SensorReader.Valid(float.NaN) && !SensorReader.Valid(null), "invalid temperatures rejected");
            Check(SensorReader.Rank("SSD", "Warning Temperature") < 0 && SensorReader.Rank("SSD", "Critical Temperature") < 0, "NVMe thresholds excluded");
            Check(SensorReader.Rank("SSD", "Composite Temperature") > SensorReader.Rank("SSD", "Temperature #2"), "SSD composite temperature preferred");
            Check(SensorReader.Rank("GPU", "GPU Core") > SensorReader.Rank("GPU", "GPU Hot Spot"), "GPU core preferred over hot spot");
            Check(SensorReader.Rank("CPU", "Core (Tctl/Tdie)") > SensorReader.Rank("CPU", "CCD1 (Tdie)"), "CPU package temperature preferred");
            using (var form = new MonitorWindow(true))
            {
                IntPtr handle = form.Handle;
                Pump((Task)Call(form, "PollAsync"));
                var data = (Snapshot)Field(form, "data");
                Check(data.Cpu.Value.HasValue && data.Gpu.Value.HasValue, "live CPU and GPU temperatures");
                Check(data.Disks.Count(d => d.Kind == "SSD" && d.Value.HasValue) >= 2, "both live SSD temperatures");
                Check(data.Disks.Any(d => d.Kind == "HDD" && d.Value.HasValue), "external HDD temperature");
                Check(data.MemoryValid && data.Total > data.Used && data.Used > 0, "live RAM reading");
                log.AppendLine(((SensorReader)Field(form, "reader")).Report(data));
                form.Render(data, true, Path.Combine(output, "overlay-preview.png"));
                Call(form, "SetLocked", true);
                Check((bool)Field(form, "locked") && (Native.GetWindowLong(form.Handle, -20) & 0x20) != 0, "overlay click-through enabled");
                Native.SendMessage(form.Handle, 0x312, new IntPtr(1), IntPtr.Zero);
                Check(!(bool)Field(form, "locked") && (Native.GetWindowLong(form.Handle, -20) & 0x20) == 0, "unlock hotkey clears click-through");
                form.Hide();
                Call(form, "SetOverlay", false);
                Check(form.FormBorderStyle == FormBorderStyle.FixedSingle && form.ShowInTaskbar, "normal window mode");
                form.Render(data, false, Path.Combine(output, "window-preview.png"));
                Native.SendMessage(form.Handle, 0x312, new IntPtr(2), IntPtr.Zero);
                Check(form.FormBorderStyle == FormBorderStyle.None && form.TopMost, "overlay hotkey and always-on-top");
                form.Hide();
                using (var process = Process.GetCurrentProcess())
                {
                    var clock = Stopwatch.StartNew(); TimeSpan cpu = process.TotalProcessorTime;
                    long maxWorking = 0;
                    for (int i = 0; i < 10; i++)
                    {
                        var tick = Stopwatch.StartNew(); Pump((Task)Call(form, "PollAsync"));
                        while (tick.ElapsedMilliseconds < 3000) { Application.DoEvents(); Thread.Sleep(25); }
                        process.Refresh(); maxWorking = Math.Max(maxWorking, process.WorkingSet64);
                    }
                    process.Refresh();
                    log.AppendLine(String.Format(System.Globalization.CultureInfo.InvariantCulture, "Measured 30s: RAM peak {0:0.0} MB; average total CPU {1:0.000}%; CPU time {2:0.000}s", maxWorking / 1048576.0, (process.TotalProcessorTime - cpu).TotalSeconds / clock.Elapsed.TotalSeconds / Environment.ProcessorCount * 100, (process.TotalProcessorTime - cpu).TotalSeconds));
                }
                Task pending = (Task)Call(form, "PollAsync"); form.Close(); Pump(pending);
                var closing = Stopwatch.StartNew(); while (!form.IsDisposed && closing.ElapsedMilliseconds < 10000) { Application.DoEvents(); Thread.Sleep(20); }
                Check(form.IsDisposed, "clean shutdown while sampling");
            }
            File.WriteAllText(Path.Combine(output, "checks.txt"), log.ToString()); return 0;
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(output, "checks.txt"), log + "\n" + ex); return 1; }
    }
}
