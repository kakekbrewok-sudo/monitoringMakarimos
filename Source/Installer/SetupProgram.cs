using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

static class SetupProgram
{
    internal static bool Admin { get { using (var id = WindowsIdentity.GetCurrent()) return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator); } }
    internal static bool DriverInstalled { get { using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO")) return key != null; } }
    static string UserSid { get { using (var id = WindowsIdentity.GetCurrent()) return id.User.Value; } }
    [DllImport("kernel32.dll")] static extern void GetNativeSystemInfo(IntPtr info);
    static void CheckArchitecture()
    {
        IntPtr info = Marshal.AllocHGlobal(64);
        try { GetNativeSystemInfo(info); if (Marshal.ReadInt16(info) != 9) throw new InvalidOperationException("Installer ini untuk Windows x64 pada prosesor Intel/AMD."); }
        finally { Marshal.FreeHGlobal(info); }
    }

    [STAThread] static int Main(string[] args)
    {
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        try {
            if (args.Length == 2 && args[0] == "--preview") {
                using (var form = new SetupWindow(false, true)) { form.RenderPreview(args[1]); }
                return 0;
            }
            bool uninstall = args.Contains("--uninstall"), quiet = args.Contains("--quiet");
            CheckArchitecture();
            int userIndex = Array.IndexOf(args, "--user-sid");
            if (userIndex >= 0 && (userIndex + 1 >= args.Length || args[userIndex + 1] != UserSid))
                throw new InvalidOperationException("Gunakan akun Windows administrator milik pengguna yang akan memakai Mini Monitor. Pemasangan dengan kredensial akun lain dibatalkan agar startup dan pengaturan tidak masuk ke profil yang salah.");
            if (!Admin) {
                string command = (uninstall ? "--uninstall " : "") + (quiet ? "--quiet " : "") + "--user-sid " + UserSid;
                using (var child = Process.Start(new ProcessStartInfo(Application.ExecutablePath, command) { UseShellExecute = true, Verb = "runas" })) { child.WaitForExit(); return child.ExitCode; }
            }
            using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full")) {
                if (key == null || Convert.ToInt32(key.GetValue("Release", 0)) < 461808)
                    throw new InvalidOperationException("Pasang .NET Framework 4.7.2 atau lebih baru sebelum memasang Mini Monitor.");
            }
            using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion")) {
                int build;
                if (key == null || !int.TryParse(Convert.ToString(key.GetValue("CurrentBuildNumber", "0")), out build) || build < 19041)
                    throw new InvalidOperationException("Installer ini memerlukan Windows 10 versi 2004 atau lebih baru, atau Windows 11, 64-bit (Intel/AMD).");
            }
            bool created;
            using (var mutex = new Mutex(true, "Global\\MiniMonitor-Setup", out created)) {
                if (!created) throw new InvalidOperationException("Installer Mini Monitor lain sedang berjalan. Selesaikan proses itu terlebih dahulu.");
                try {
                    if (quiet) {
                        if (uninstall) SetupEngine.Uninstall(delegate { });
                        else InstallPayload(SetupEngine.GetStartupEnabled(), File.Exists(DesktopShortcut), delegate { });
                        return 0;
                    }
                    using (var form = new SetupWindow(uninstall, false)) Application.Run(form);
                } finally { mutex.ReleaseMutex(); }
            }
            return 0;
        } catch (System.ComponentModel.Win32Exception ex) {
            if (ex.NativeErrorCode == 1223) return 2;
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "MiniMonitor-Setup-error.txt"), ex.ToString()); } catch { }
            if (!args.Contains("--quiet")) MessageBox.Show(ex.Message, "Mini Monitor Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        } catch (Exception ex) {
            string detail = ex.GetBaseException().Message;
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "MiniMonitor-Setup-error.txt"), ex.ToString()); } catch { }
            if (!args.Contains("--quiet")) MessageBox.Show(detail, "Mini Monitor Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    internal static string DesktopShortcut { get { return SetupEngine.DesktopShortcut; } }

    internal static void InstallPayload(bool startup, bool desktop, Action<string> status)
    {
        // Program Files staging inherits an administrator-controlled ACL.
        string stage = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "MiniMonitor.Setup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        string path = Path.Combine(stage, "payload.zip");
        try {
            using (var source = Assembly.GetExecutingAssembly().GetManifestResourceStream("MiniMonitor.Payload")) {
                if (source == null) throw new InvalidDataException("Paket aplikasi tidak ditemukan dalam installer.");
                using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) source.CopyTo(file);
            }
            using (var sha = SHA256.Create()) using (var file = File.OpenRead(path)) {
                string actual = BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "");
                if (!String.Equals(actual, EmbeddedPayload.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Paket installer rusak. Gunakan salinan installer yang utuh.");
            }
            SetupEngine.Install(path, startup, desktop, status);
        } finally {
            // Cleanup cannot turn a successful committed installation into a failure.
            try {
                if (File.Exists(path)) File.Delete(path);
                if (Directory.Exists(stage)) Directory.Delete(stage, false);
            } catch (Exception ex) { Trace.WriteLine("Setup staging cleanup: " + ex.Message); }
        }
    }
}

sealed class SetupWindow : Form
{
    readonly bool uninstall;
    readonly bool preview;
    readonly CheckBox startup = new CheckBox(), desktop = new CheckBox(), driver = new CheckBox();
    readonly Button primary = new Button(), cancel = new Button();
    readonly Label progress = new Label(), detail = new Label();
    readonly ProgressBar bar = new ProgressBar();
    bool busy, complete;
    readonly Color ink = Color.FromArgb(235, 235, 230), muted = Color.FromArgb(165, 165, 160), red = Color.FromArgb(236, 55, 57);

    internal SetupWindow(bool remove, bool preview)
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        uninstall = remove;
        this.preview = preview;
        Text = remove ? "Hapus Mini Monitor" : "Mini Monitor Setup";
        ClientSize = new Size(600, 510); Font = new Font("Segoe UI", 10); BackColor = Color.FromArgb(19, 19, 19); ForeColor = ink;
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; StartPosition = FormStartPosition.CenterScreen;
        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MiniMonitor.Icon")) if (stream != null) Icon = new Icon(stream);
        Label brand = LabelAt("\u25cf  MINI / MONITOR", 30, 25, 540, 28, 11, FontStyle.Bold); brand.ForeColor = red;
        LabelAt(remove ? "Sampai jumpa." : "Semua dalam satu pandangan.", 30, 74, 540, 42, 21, FontStyle.Bold);
        detail.SetBounds(32, 125, 530, 58); detail.ForeColor = muted;
        detail.Text = remove ? "Hapus aplikasi, shortcut, dan startup Windows.\nTema serta pengaturanmu tetap disimpan." : "Jam, durasi aplikasi, FPS, dan sensor perangkat.\nVersi 3.5.5  /  Windows 64-bit  /  Paket offline";
        Controls.Add(detail);
        Panel line = new Panel { BackColor = Color.FromArgb(50, 50, 50) }; line.SetBounds(32, 200, 536, 1); Controls.Add(line);
        if (!remove) {
            AddCheck(startup, "Muncul saat login Windows", 223); startup.Checked = preview || SetupEngine.GetStartupEnabled();
            AddCheck(desktop, "Buat shortcut di desktop", 259); desktop.Checked = !preview && File.Exists(SetupProgram.DesktopShortcut);
            AddCheck(driver, "Pasang driver sensor CPU (PawnIO)", 295); driver.Checked = !preview && !SetupProgram.DriverInstalled;
            if (preview || SetupProgram.DriverInstalled) { driver.Checked = false; driver.Enabled = false; driver.Visible = false; LabelAt("Driver sensor CPU sudah tersedia", 32, 299, 536, 28, 10, FontStyle.Regular).ForeColor = muted; }
            LabelAt("Lokasi: " + SetupEngine.InstallRoot, 32, 342, 536, 28, 9, FontStyle.Regular).ForeColor = muted;
        } else {
            LabelAt("Driver PawnIO tetap terpasang karena dapat digunakan\naplikasi lain. Hapus terpisah melalui Installed Apps jika perlu.", 32, 228, 536, 65, 10, FontStyle.Regular).ForeColor = muted;
        }
        progress.SetBounds(32, 380, 536, 34); progress.Text = remove ? "Siap menghapus aplikasi." : "Pilihan yang tersimpan dipertahankan saat diperbarui."; progress.ForeColor = muted; progress.Font = new Font("Segoe UI", 9); Controls.Add(progress);
        bar.SetBounds(32, 416, 536, 4); bar.Style = ProgressBarStyle.Marquee; bar.MarqueeAnimationSpeed = 25; bar.Visible = false; Controls.Add(bar);
        primary.SetBounds(378, 442, 190, 40); primary.Text = remove ? "Hapus aplikasi" : "Pasang / perbarui"; primary.BackColor = red; primary.ForeColor = Color.White; primary.FlatStyle = FlatStyle.Flat; primary.FlatAppearance.BorderSize = 0; primary.Font = new Font("Segoe UI", 10, FontStyle.Bold); primary.Click += Run; Controls.Add(primary);
        cancel.SetBounds(256, 442, 108, 40); cancel.Text = "Batal"; cancel.FlatStyle = FlatStyle.Flat; cancel.FlatAppearance.BorderColor = Color.FromArgb(70, 70, 70); cancel.Click += delegate { Close(); }; Controls.Add(cancel);
        FormClosing += delegate(object sender, FormClosingEventArgs e) { if (busy) e.Cancel = true; };
        AcceptButton = primary; CancelButton = cancel;
        ResumeLayout(true);
    }

    protected override bool ShowWithoutActivation { get { return preview || base.ShowWithoutActivation; } }
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { int dark = 1; DwmSetWindowAttribute(Handle, 20, ref dark, 4); } catch { }
    }
    internal void RenderPreview(string path)
    {
        ShowInTaskbar = false; StartPosition = FormStartPosition.Manual; Location = new Point(-30000, -30000);
        Show(); Refresh(); Application.DoEvents();
        using (var image = new Bitmap(Width, Height)) { DrawToBitmap(image, new Rectangle(Point.Empty, Size)); image.Save(path); }
        Hide();
    }

    Label LabelAt(string text, int x, int y, int w, int h, float size, FontStyle style)
    { var l = new Label { Text = text, Font = new Font("Segoe UI", size, style), ForeColor = ink }; l.SetBounds(x, y, w, h); Controls.Add(l); return l; }
    void AddCheck(CheckBox check, string text, int y) { check.Text = text; check.SetBounds(32, y, 536, 30); Controls.Add(check); }
    void Status(string value) { if (!IsDisposed && IsHandleCreated) BeginInvoke((Action)delegate { progress.Text = value; }); }

    async void Run(object sender, EventArgs e)
    {
        if (complete) {
            if (!uninstall) {
                try { Process.Start(new ProcessStartInfo(Path.Combine(SetupEngine.InstallRoot, "MiniMonitor.exe"), "--startup") { UseShellExecute = true }); }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, Text); return; }
            }
            Close(); return;
        }
        bool wantStartup = startup.Checked, wantDesktop = desktop.Checked, wantDriver = driver.Checked;
        busy = true; primary.Enabled = cancel.Enabled = startup.Enabled = desktop.Enabled = driver.Enabled = false; bar.Visible = true;
        try {
            if (uninstall) await Task.Run(() => SetupEngine.Uninstall(Status));
            else await Task.Run(() => SetupProgram.InstallPayload(wantStartup, wantDesktop, Status));
            string driverNotice = "";
            if (!uninstall && wantDriver && !SetupProgram.DriverInstalled) {
                Status("Memasang driver sensor CPU...");
                try {
                    int result = await Task.Run(() => {
                        using (var p = Process.Start(new ProcessStartInfo(Path.Combine(SetupEngine.InstallRoot, "setup", "PawnIO_setup.exe"), "-install -silent") { UseShellExecute = false, CreateNoWindow = true })) {
                            // The window stays responsive, but never offer Finish while
                            // the shared driver installer is still modifying the system.
                            p.WaitForExit();
                            return p.ExitCode;
                        }
                    });
                    if (result != 0 || !SetupProgram.DriverInstalled) driverNotice = " Driver CPU belum siap; gunakan menu Perbaiki / pasang driver CPU.";
                } catch { driverNotice = " Driver CPU belum siap; gunakan menu Perbaiki / pasang driver CPU."; }
            }
            complete = true; primary.Text = uninstall ? "Selesai" : "Buka Mini Monitor"; cancel.Text = "Tutup";
            progress.Text = (uninstall ? "Aplikasi dihapus. Pengaturan pribadi tetap tersimpan." : "Mini Monitor berhasil dipasang.") + driverNotice;
            detail.Text = uninstall ? (SetupEngine.RestartRequired ? "File uninstaller yang sedang dipakai dibersihkan saat restart Windows.\nTidak perlu restart untuk memasang ulang." : "Mini Monitor sudah dihapus dari Windows.\nInstaller dapat digunakan kembali kapan saja.") : "Siap dipakai. Installer ini bisa disimpan untuk pemasangan berikutnya.\nTema, ukuran jendela, dan preferensi tetap tersimpan.";
        } catch (Exception ex) {
            progress.Text = "Belum selesai. " + ex.GetBaseException().Message;
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            startup.Enabled = desktop.Enabled = true; driver.Enabled = !SetupProgram.DriverInstalled;
        } finally { busy = false; bar.Visible = false; primary.Enabled = cancel.Enabled = true; }
    }
}
