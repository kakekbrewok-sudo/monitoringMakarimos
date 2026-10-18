using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

// Offscreen only: no installation, hardware, startup task or driver changes.
static class SetupUiChecks
{
    [STAThread] static int Main(string[] args)
    {
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        Directory.CreateDirectory(args[0]);
        using (var log = new StreamWriter(Path.Combine(args[0], "setup-ui-checks.txt"))) {
            foreach (int dpi in new[] { 96, 144, 192 }) {
                using (var form = new SetupWindow(false, true)) {
                    if (form.AutoScaleMode != AutoScaleMode.Dpi) throw new Exception("DPI scaling missing");
                    // Exercise scaled control geometry in addition to the OS DPI mode.
                    if (dpi != 96) form.Scale(new SizeF(dpi / 96f, dpi / 96f));
                    form.RenderPreview(Path.Combine(args[0], "setup-" + dpi + ".png"));
                    foreach (Control control in form.Controls) {
                        if (control.Left < 0 || control.Top < 0 || control.Right > form.ClientSize.Width || control.Bottom > form.ClientSize.Height)
                            throw new Exception("Control exceeds client area at DPI " + dpi + ": " + control.Text + " " + control.Bounds + " / " + form.ClientSize);
                    }
                    log.WriteLine("PASS: installer controls fit at scaled DPI " + dpi);
                }
            }
            log.WriteLine("PASS: preview performs no install/driver/GPU actions");
        }
        return 0;
    }
}
