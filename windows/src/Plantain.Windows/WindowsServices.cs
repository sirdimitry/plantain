using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Plantain.Core;

namespace Plantain.Windows;

public interface IWindowsServices
{
    string DefaultDestination { get; }
    string? ChooseFolder(Window owner, string current);
    void OpenFolder(string folder);
    IDisposable KeepAwake();
}

public sealed class WindowsServices : IWindowsServices
{
    private static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");
    private static readonly Guid DialogId = new("04C97413-3FF4-4E56-A953-923F65552356");
    public string DefaultDestination
    {
        get
        {
            var id = DownloadsId;
            var result = SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var pointer);
            try
            {
                var path = result == 0 ? Marshal.PtrToStringUni(pointer) : null;
                return path is not null && System.IO.Directory.Exists(path) ? path : "";
            }
            finally { if (pointer != IntPtr.Zero) Marshal.FreeCoTaskMem(pointer); }
        }
    }

    public string? ChooseFolder(Window owner, string current)
    {
        ClearDialogHistory();
        try
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Куда сохранить файлы", Multiselect = false, AddToRecent = false,
                ClientGuid = DialogId, InitialDirectory = current
            };
            return dialog.ShowDialog(owner) == true ? dialog.FolderName : null;
        }
        finally { ClearDialogHistory(); }
    }

    // Remove only this application's file-dialog state; never touch global Explorer history.
    internal static bool ClearDialogHistory()
    {
        object? instance = null;
        try
        {
            instance = Activator.CreateInstance(Type.GetTypeFromCLSID(new("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7"), true)!);
            var dialog = (IFileDialog)instance!;
            var id = DialogId;
            dialog.SetClientGuid(ref id);
            dialog.ClearClientData();
            return true;
        }
        // Clearing is best effort on shells that report E_FAIL. AddToRecent=false
        // still applies; do not turn an unavailable cleanup into an unusable picker.
        catch (COMException) { return false; }
        finally { if (instance is not null) Marshal.FinalReleaseComObject(instance); }
    }

    public void OpenFolder(string folder)
    {
        if (!System.IO.Directory.Exists(folder)) throw new LoaderException("Папка недоступна. Выберите другое место сохранения.");
        Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
    }

    public IDisposable KeepAwake() => new SleepLease();

    private sealed class SleepLease : IDisposable
    {
        private readonly Dispatcher owner = Dispatcher.CurrentDispatcher;
        private readonly uint previous = SetThreadExecutionState(0x80000001);
        private bool disposed;
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (previous == 0) return;
            void Restore() => SetThreadExecutionState(previous | 0x80000000);
            if (owner.CheckAccess()) Restore(); else owner.Invoke(Restore);
        }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);
    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);

    [ComImport, Guid("42F85136-DB7E-439C-85F1-E4075D135FC8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog
    {
        [PreserveSig] int Show(IntPtr owner);
        void SetFileTypes(uint count, IntPtr specs); void SetFileTypeIndex(uint index); void GetFileTypeIndex(out uint index);
        void Advise(IntPtr events, out uint cookie); void Unadvise(uint cookie); void SetOptions(uint options); void GetOptions(out uint options);
        void SetDefaultFolder(IntPtr folder); void SetFolder(IntPtr folder); void GetFolder(out IntPtr folder); void GetCurrentSelection(out IntPtr item);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name); void GetFileName(out IntPtr name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title); void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label); void GetResult(out IntPtr item); void AddPlace(IntPtr item, uint placement);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension); void Close(int result);
        void SetClientGuid(ref Guid guid); void ClearClientData(); void SetFilter(IntPtr filter);
    }
}
