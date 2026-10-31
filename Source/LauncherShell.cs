using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

// Ask the desktop Explorer to launch apps with the user's normal token even
// when Mini Monitor is elevated for sensors. No persistent helper or service.
// https://learn.microsoft.com/windows/win32/shell/samples-execinexplorer
static class LauncherShell
{
    public static void Open(string path)
    {
        if (!LauncherSlots.Supported(path)) throw new ArgumentException("Jenis aplikasi tidak didukung.");
        if (!Native.Admin) { using (Process process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path) })) { } return; }
        object windows = null, desktop = null, browser = null, view = null, folder = null, shell = null;
        try {
            windows = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39")));
            object location = 0, root = null; int hwnd;
            desktop = ((ShellWindows)windows).FindWindowSW(ref location, ref root, 8, out hwnd, 1);
            if (desktop == null) throw new InvalidOperationException("Windows Explorer belum siap. Coba lagi setelah desktop selesai dimuat.");
            Guid service = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837"), iid = typeof(ShellBrowser).GUID;
            browser = ((ShellServices)desktop).QueryService(ref service, ref iid);
            view = ((ShellBrowser)browser).QueryActiveShellView();
            Guid dispatch = new Guid("00020400-0000-0000-C000-000000000046");
            folder = ((ShellView)view).GetItemObject(0, ref dispatch);
            shell = ((dynamic)folder).Application;
            ((dynamic)shell).ShellExecute(path, "", Path.GetDirectoryName(path), "", 1);
        }
        finally { Release(shell); Release(folder); Release(view); Release(browser); Release(desktop); Release(windows); }
    }
    static void Release(object value) { if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)] interface ShellWindows
    {
        [return: MarshalAs(UnmanagedType.IDispatch)] object FindWindowSW([MarshalAs(UnmanagedType.Struct)] ref object location, [MarshalAs(UnmanagedType.Struct)] ref object root, int kind, out int hwnd, int options);
    }
    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface ShellServices
    {
        [return: MarshalAs(UnmanagedType.Interface)] object QueryService(ref Guid service, ref Guid iid);
    }
    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface ShellBrowser
    {
        void GetWindow(); void ContextSensitiveHelp(); void InsertMenusSB(); void SetMenuSB(); void RemoveMenusSB(); void SetStatusTextSB();
        void EnableModelessSB(); void TranslateAcceleratorSB(); void BrowseObject(); void GetViewStateStream(); void GetControlWindow(); void SendControlMsg();
        [return: MarshalAs(UnmanagedType.Interface)] object QueryActiveShellView();
    }
    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface ShellView
    {
        void GetWindow(); void ContextSensitiveHelp(); void TranslateAccelerator(); void EnableModeless(); void UIActivate(); void Refresh();
        void CreateViewWindow(); void DestroyViewWindow(); void GetCurrentInfo(); void AddPropertySheetPages(); void SaveViewState(); void SelectItem();
        [return: MarshalAs(UnmanagedType.Interface)] object GetItemObject(uint aspect, ref Guid iid);
    }
}
