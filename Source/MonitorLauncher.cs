using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

sealed class LauncherSlots
{
    public const int Count = 4;
    readonly string[] paths = new string[Count];
    public string this[int index] { get { return paths[index]; } }
    public static bool Supported(string path)
    {
        if (String.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) return false;
        string extension = Path.GetExtension(path).ToLowerInvariant();
        return extension == ".exe" || extension == ".lnk" || extension == ".url" || extension == ".appref-ms";
    }
    public void Set(int index, string path)
    {
        if (path != null && !Supported(path)) throw new ArgumentException("Pilih aplikasi .exe atau shortcut .lnk, .url, .appref-ms.");
        paths[index] = path;
    }
    public string Serialize()
    {
        var parts = new string[Count];
        for (int i = 0; i < Count; i++) parts[i] = String.IsNullOrEmpty(paths[i]) ? "" : Convert.ToBase64String(Encoding.UTF8.GetBytes(paths[i]));
        return String.Join(";", parts);
    }
    public void Restore(string value)
    {
        Array.Clear(paths, 0, Count);
        string[] parts = (value ?? "").Split(';');
        for (int i = 0; i < Count && i < parts.Length; i++) {
            if (parts[i].Length == 0 || parts[i].Length > 16384) continue;
            try { string path = Encoding.UTF8.GetString(Convert.FromBase64String(parts[i])); if (Supported(path)) paths[i] = path; }
            catch (ArgumentException) { } catch (FormatException) { }
        }
    }
}

sealed class LauncherButton : Button
{
    public Color Surface, Ink, Border;
    public Bitmap AppImage;
    public LauncherButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand; TabStop = true; AccessibleRole = AccessibleRole.PushButton;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics; g.Clear(Surface);
        using (var pen = new Pen(Focused ? Ink : Border)) g.DrawRectangle(pen, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
        if (AppImage != null) {
            int side = Math.Max(12, Math.Min(32, Math.Min(Width, Height) - 10));
            g.DrawImage(AppImage, new Rectangle((Width - side) / 2, (Height - side) / 2, side, side));
        } else {
            int cx = Width / 2, cy = Height / 2, arm = Math.Max(3, Math.Min(Width, Height) / 8);
            using (var pen = new Pen(Ink, Math.Max(1, Width / 32f))) { g.DrawLine(pen, cx - arm, cy, cx + arm, cy); g.DrawLine(pen, cx, cy - arm, cx, cy + arm); }
        }
        if (Focused) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(ClientRectangle, -4, -4), Ink, Surface);
    }
}

static class LauncherIcon
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct FileInfo
    {
        public IntPtr Icon; public int Index; public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SHGetFileInfo(string path, uint attributes, ref FileInfo info, uint size, uint flags);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);
    public static Bitmap Read(string path)
    {
        var info = new FileInfo();
        try {
            if (SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf(typeof(FileInfo)), 0x100) != IntPtr.Zero && info.Icon != IntPtr.Zero)
                using (var icon = Icon.FromHandle(info.Icon)) return icon.ToBitmap();
        } catch (ExternalException) { } catch (ArgumentException) { }
        finally { if (info.Icon != IntPtr.Zero) DestroyIcon(info.Icon); }
        return SystemIcons.Application.ToBitmap();
    }
}

sealed partial class MonitorWindow
{
    readonly LauncherSlots launcherSlots = new LauncherSlots();
    readonly LauncherButton[] launcherButtons = new LauncherButton[LauncherSlots.Count];
    readonly ContextMenuStrip launcherMenu = new ContextMenuStrip { ShowItemToolTips = false };
    int launcherMenuIndex;

    void InitializeLauncher()
    {
        var open = new ToolStripMenuItem("Buka aplikasi", null, (s, e) => OpenLauncher(launcherMenuIndex));
        var change = new ToolStripMenuItem("Pilih aplikasi...", null, (s, e) => ChooseLauncher(launcherMenuIndex));
        var remove = new ToolStripMenuItem("Hapus dari Mini Monitor", null, (s, e) => SetLauncher(launcherMenuIndex, null));
        launcherMenu.Items.Add(open); launcherMenu.Items.Add(change); launcherMenu.Items.Add(remove);
        launcherMenu.Opening += (s, e) => {
            var source = launcherMenu.SourceControl as LauncherButton;
            if (source == null) { e.Cancel = true; return; }
            launcherMenuIndex = (int)source.Tag;
            string path = launcherSlots[launcherMenuIndex];
            open.Text = path == null ? "Slot kosong" : "Buka " + Path.GetFileNameWithoutExtension(path);
            open.Enabled = remove.Enabled = path != null;
            change.Text = path == null ? "Tambah aplikasi..." : "Ganti aplikasi...";
        };
        launcherMenu.Closed += (s, e) => {
            if (!closing && IsHandleCreated) BeginInvoke(new Action(() => { if (!closing && !menu.Visible && !launcherMenu.Visible) { ApplyLayout(); Invalidate(); } }));
        };
        for (int i = 0; i < launcherButtons.Length; i++) {
            int index = i;
            var button = new LauncherButton { Tag = index, ContextMenuStrip = launcherMenu };
            button.Click += (s, e) => { if (launcherSlots[index] == null) ChooseLauncher(index); else OpenLauncher(index); };
            launcherButtons[i] = button; Controls.Add(button); RefreshLauncher(index);
        }
        var shortcuts = new ToolStripMenuItem("Aplikasi favorit");
        for (int i = 0; i < LauncherSlots.Count; i++) {
            int index = i;
            var slot = new ToolStripMenuItem("Slot " + (i + 1));
            slot.DropDownItems.Add("Pilih / ganti aplikasi...", null, (s, e) => ChooseLauncher(index));
            slot.DropDownItems.Add("Hapus dari Mini Monitor", null, (s, e) => SetLauncher(index, null));
            slot.DropDownOpening += (s, e) => slot.DropDownItems[1].Enabled = launcherSlots[index] != null;
            shortcuts.DropDownItems.Add(slot);
        }
        shortcuts.DropDownOpening += (s, e) => {
            for (int i = 0; i < LauncherSlots.Count; i++) shortcuts.DropDownItems[i].Text = (i + 1) + " · " + (launcherSlots[i] == null ? "Tambah aplikasi" : Path.GetFileNameWithoutExtension(launcherSlots[i]));
        };
        menu.Items.Insert(menu.Items.IndexOf(startupItem), shortcuts);
    }
    void RefreshLauncher(int index)
    {
        LauncherButton button = launcherButtons[index]; if (button == null) return;
        Bitmap old = button.AppImage;
        string path = launcherSlots[index];
        button.AppImage = path == null ? null : LauncherIcon.Read(path);
        button.AccessibleName = path == null ? "Tambah aplikasi favorit " + (index + 1) : "Buka " + Path.GetFileNameWithoutExtension(path);
        if (old != null) old.Dispose(); button.Invalidate();
    }
    void SetLauncher(int index, string path)
    {
        launcherSlots.Set(index, path); RefreshLauncher(index); SaveSettings();
    }
    void ChooseLauncher(int index)
    {
        menu.Close(); launcherMenu.Close();
        using (var dialog = new OpenFileDialog { Title = "Aplikasi favorit " + (index + 1),
            Filter = "Aplikasi dan shortcut|*.exe;*.lnk;*.url;*.appref-ms", CheckFileExists = true, DereferenceLinks = false,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.StartMenu) })
            if (dialog.ShowDialog(this) == DialogResult.OK) SetLauncher(index, dialog.FileName);
    }
    void OpenLauncher(int index)
    {
        string path = launcherSlots[index]; if (path == null || closing) return;
        try {
            if (!File.Exists(path)) throw new FileNotFoundException("Aplikasi atau shortcut sudah pindah / dihapus. Klik kanan ikon > Ganti aplikasi.");
            LauncherShell.Open(path);
        } catch (Exception ex) { MessageBox.Show(this, ex.GetBaseException().Message, "Tidak bisa membuka aplikasi", MessageBoxButtons.OK, MessageBoxIcon.Information); }
    }
    void PlaceLauncherButtons(int x, int y, int width, int size)
    {
        for (int i = 0; i < launcherButtons.Length; i++) {
            LauncherButton button = launcherButtons[i]; if (button == null) continue;
            var bounds = new Rectangle(S(x + i * (width - size) / 3), S(y), Math.Max(18, S(size)), Math.Max(18, S(size)));
            if (!launcherMenu.Visible && button.Bounds != bounds) button.Bounds = bounds;
            Color surface = theme == MonitorTheme.GShock ? Color.FromArgb(189, 197, 166) : theme == MonitorTheme.DotWhite && !overlay && ClientSize.Width >= 680 * dpiScale && ClientSize.Width >= ClientSize.Height * 2 ? Color.FromArgb(243, 237, 230) : BackColor;
            if (button.Surface != surface || button.Ink != accent || button.Border != rule) {
                button.Surface = surface; button.Ink = accent; button.Border = rule; button.Invalidate();
            }
        }
    }
    bool InLauncher(Point point)
    {
        foreach (LauncherButton button in launcherButtons) if (button != null && button.Visible && button.Bounds.Contains(point)) return true;
        return false;
    }
    void DisposeLauncher()
    {
        launcherMenu.Dispose();
        foreach (LauncherButton button in launcherButtons) if (button != null && button.AppImage != null) { button.AppImage.Dispose(); button.AppImage = null; }
    }
}
