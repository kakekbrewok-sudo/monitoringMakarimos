using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

static class UsageChecks
{
    static StringBuilder log = new StringBuilder();
    static void Check(bool ok, string text) { if (!ok) throw new Exception("FAIL: " + text); log.AppendLine("PASS: " + text); }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            Check(SensorReader.ValidLoad(0) && SensorReader.ValidLoad(100) && !SensorReader.ValidLoad(null) && !SensorReader.ValidLoad(float.NaN) && !SensorReader.ValidLoad(-1) && !SensorReader.ValidLoad(101), "load validation preserves real idle 0% and rejects invalid values");
            Check(SensorReader.LoadRank("CPU", "CPU Total") > 0 && SensorReader.LoadRank("CPU", "CPU Core Max") < 0, "CPU load is total, not busiest core");
            Check(SensorReader.LoadRank("GPU", "GPU Core") > SensorReader.LoadRank("GPU", "D3D 3D") && SensorReader.LoadRank("GPU", "GPU Memory") < 0, "GPU core utilization is not VRAM occupancy");
            var r = new Reading("CPU", "Example") { Load = 58, Value = 46 };
            Check(r.UsageAndTemperature == "58% / 46°C", "requested compact usage/temperature format");
            r.Load = 0; Check(r.UsageAndTemperature == "0% / 46°C", "zero load is visible");
            r.Load = null; Check(r.UsageAndTemperature == "— / 46°C", "missing load is not fabricated as zero");
            using (var reader = new SensorReader())
            {
                reader.Poll(); System.Threading.Thread.Sleep(1500); Snapshot sample = reader.Poll();
                Check(sample.Cpu.Load.HasValue && sample.Gpu.Load.HasValue, "live CPU and GPU usage read without extra helper processes");
                log.AppendLine(reader.Report(sample));
                // Worst-case display fixture, not measured temperatures.
                sample.Cpu.Value = 100; sample.Cpu.Load = 100; sample.Gpu.Value = 100; sample.Gpu.Load = 100;
                sample.Disks.Add(new Reading("SSD", "PNY CS2340 1TB SSD") { Value = 42 });
                sample.Disks.Add(new Reading("SSD", "XPG GAMMIX S70 BLADE") { Value = 46 });
                using (var form = new MonitorWindow(true))
                {
                    var font = (Font)typeof(MonitorWindow).GetField("usageFont", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
                    int needed = TextRenderer.MeasureText("100% / 100°C", font, new Size(Int32.MaxValue, Int32.MaxValue), TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width;
                    Check(needed <= 104, "100% / 100°C fits the compact metric column (" + needed + "px)");
                    form.Render(sample, true, Path.Combine(args[0], "usage-layout-example.png"));
                    form.Render(sample, false, Path.Combine(args[0], "usage-window-example.png"));
                }
            }
            using (var chimes = new SoftChimes()) Check((bool)typeof(SoftChimes).GetField("loaded", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(chimes), "all short WAV streams load successfully without playing them");
            File.WriteAllText(Path.Combine(args[0], "usage-checks.txt"), log.ToString()); return 0;
        }
        catch (Exception e) { File.WriteAllText(Path.Combine(args[0], "usage-checks.txt"), log + "\n" + e); return 1; }
    }
}
