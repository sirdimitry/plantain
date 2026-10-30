using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Plantain.Core;

// All handles are opened without following the final reparse point. Parent directories
// remain pinned without delete sharing until every operation below them completes.
// Every child is opened relative to its parent's handle, never by looking up the full path again.
internal static class WindowsFiles
{
    private const uint Read = 0x80000000, Write = 0x40000000, Delete = 0x10000;
    private const uint OpenReparse = 0x00200000, Backup = 0x02000000;

    internal static SafeFileHandle OpenDirectory(string path, bool exclusive = false)
    {
        var handle = Open(path, Read | (exclusive ? Delete : 0), 3, 3, Backup | OpenReparse);
        return CheckDirectory(handle);
    }

    internal static SafeFileHandle OpenDirectory(SafeFileHandle parent, string name, bool create, bool exclusive)
        => CheckDirectory(OpenRelative(parent, name, Read | (exclusive ? Delete : 0), 3, create ? 3u : 1u, 0x1));

    private static SafeFileHandle CheckDirectory(SafeFileHandle handle)
    {
        try
        {
            var info = Info(handle);
            if ((info.Attributes & (uint)FileAttributes.Directory) == 0)
                throw new LocalFileException("На месте папки находится файл.");
            RejectReparse(info);
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    internal static FileStream OpenFile(SafeFileHandle parent, string name, bool partial)
    {
        var handle = OpenRelative(parent, name, Read | (partial ? Write | Delete : 0), partial ? 0u : 1u,
            partial ? 3u : 1u, 0x40);
        try
        {
            var info = Info(handle);
            RejectReparse(info);
            if ((info.Attributes & (uint)FileAttributes.Directory) != 0)
                throw new LocalFileException("На месте файла находится папка.");
            if (partial && info.Links != 1)
                throw new LocalFileException("Частичный файл имеет жёсткие ссылки. Он сохранён без изменений.");
            // Native metadata operations use synchronous handles so their status/buffers
            // are final on return. FileStream still provides cancellable async byte IO.
            return new FileStream(handle, partial ? FileAccess.ReadWrite : FileAccess.Read, 64 * 1024, isAsync: false);
        }
        catch { handle.Dispose(); throw; }
    }

    private static SafeFileHandle OpenRelative(SafeFileHandle parent, string name, uint access, uint share,
        uint disposition, uint options)
    {
        PathSafety.ValidateName(name);
        RejectReparse(Info(parent));
        var buffer = Marshal.StringToHGlobalUni(name);
        var unicodePtr = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        var parentRef = false;
        try
        {
            parent.DangerousAddRef(ref parentRef);
            Marshal.StructureToPtr(new UnicodeString
            {
                Length = checked((ushort)(name.Length * 2)), MaximumLength = checked((ushort)(name.Length * 2 + 2)), Buffer = buffer
            }, unicodePtr, false);
            var attributes = new ObjectAttributes
            {
                Length = (uint)Marshal.SizeOf<ObjectAttributes>(), RootDirectory = parent.DangerousGetHandle(),
                ObjectName = unicodePtr, Attributes = 0x40 | 0x1000 // CASE_INSENSITIVE | DONT_REPARSE
            };
            var status = NtCreateFile(out var handle, access | 0x100000, ref attributes, out _, IntPtr.Zero, 0x80,
                share, disposition, options | OpenReparse | 0x20, IntPtr.Zero, 0); // SYNCHRONOUS_IO_NONALERT
            if (status < 0) { handle?.Dispose(); ThrowError((int)RtlNtStatusToDosError(status)); }
            return handle!;
        }
        finally
        {
            if (parentRef) parent.DangerousRelease();
            Marshal.FreeHGlobal(unicodePtr);
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static void Rename(FileStream file, SafeFileHandle parent, string name)
    {
        RejectReparse(Info(parent));
        // Rename the verified object, not a path looked up again. ReplaceIfExists remains FALSE.
        var bytes = Encoding.Unicode.GetBytes(name + "\0");
        var nameOffset = IntPtr.Size == 8 ? 20 : 12;
        var memory = Marshal.AllocHGlobal(nameOffset + bytes.Length);
        var parentRef = false;
        try
        {
            parent.DangerousAddRef(ref parentRef);
            Marshal.Copy(new byte[nameOffset + bytes.Length], 0, memory, nameOffset + bytes.Length);
            Marshal.WriteIntPtr(memory, IntPtr.Size == 8 ? 8 : 4, parent.DangerousGetHandle());
            Marshal.WriteInt32(memory, IntPtr.Size == 8 ? 16 : 8, bytes.Length - 2);
            Marshal.Copy(bytes, 0, memory + nameOffset, bytes.Length);
            var status = NtSetInformationFile(file.SafeFileHandle, out _, memory, (uint)(nameOffset + bytes.Length), 10);
            if (status < 0) ThrowError((int)RtlNtStatusToDosError(status));
        }
        finally
        {
            if (parentRef) parent.DangerousRelease();
            Marshal.FreeHGlobal(memory);
        }
    }

    internal static void RemoveEmptyDirectory(SafeFileHandle handle)
    {
        var memory = Marshal.AllocHGlobal(1);
        try
        {
            Marshal.WriteByte(memory, 1);
            // Nonempty or externally busy cache stays on disk. Never recurse or delete by path.
            _ = SetFileInformationByHandle(handle, 4, memory, 1);
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    private static SafeFileHandle Open(string path, uint access, uint share, uint mode, uint flags)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Файловый движок предназначен для Windows.");
        var handle = CreateFileW(NativePath(path), access, share, IntPtr.Zero, mode, flags, IntPtr.Zero);
        if (handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); ThrowError(error); }
        return handle;
    }

    private static string NativePath(string path)
    {
        var full = Path.GetFullPath(path);
        return full.StartsWith(@"\\?\", StringComparison.Ordinal) ? full :
            full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
    }

    private static HandleInfo Info(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info)) ThrowLastError();
        return info;
    }

    private static void RejectReparse(HandleInfo info)
    {
        if ((info.Attributes & (uint)FileAttributes.ReparsePoint) != 0)
            throw new LocalFileException("Путь содержит символическую ссылку или точку перенаправления.");
    }

    private static void ThrowLastError() => ThrowError(Marshal.GetLastWin32Error());
    private static void ThrowError(int code)
    {
        if (code is 2 or 3) throw new FileNotFoundException("Файл или папка не найдены.");
        throw new LocalFileException(code switch
        {
            32 or 33 => "Папка или файл уже используются другой загрузкой или программой.",
            5 => "Нет доступа к файлу или папке.",
            80 or 183 => "Файл уже существует и сохранён без изменений.",
            39 or 112 => "На диске недостаточно свободного места.",
            19 => "Носитель защищён от записи.",
            267 => "На месте папки находится файл или путь не является каталогом.",
            681 or 1920 or 4390 or 4393 => "Путь содержит недопустимую точку перенаправления.",
            206 => "Имя или путь слишком длинные для этого тома.",
            _ => $"Файловая операция не поддержана или не выполнена (Windows {code})."
        });
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HandleInfo
    {
        internal uint Attributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
        internal uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock { internal IntPtr Status; internal UIntPtr Information; }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString { internal ushort Length, MaximumLength; internal IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        internal uint Length;
        internal IntPtr RootDirectory, ObjectName;
        internal uint Attributes;
        internal IntPtr SecurityDescriptor, SecurityQualityOfService;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(out SafeFileHandle handle, uint access, ref ObjectAttributes attributes,
        out IoStatusBlock ioStatus, IntPtr allocationSize, uint fileAttributes, uint share, uint disposition,
        uint options, IntPtr eaBuffer, uint eaLength);

    [DllImport("ntdll.dll")]
    private static extern int NtSetInformationFile(SafeFileHandle file, out IoStatusBlock ioStatus,
        IntPtr info, uint size, int infoClass);
    [DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(int status);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
        uint mode, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out HandleInfo info);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, IntPtr info, uint size);
}

internal sealed class DirectoryPins : IDisposable
{
    private readonly Dictionary<string, SafeFileHandle> handles = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SafeFileHandle> ordered = [];
    private SafeFileHandle? cache;
    private bool disposed;

    internal SafeFileHandle Pin(string path, bool create = false, bool exclusiveCache = false)
    {
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (handles.TryGetValue(path, out var existing)) return existing;
        var parentPath = Path.GetDirectoryName(path);
        SafeFileHandle handle;
        if (parentPath is null) handle = WindowsFiles.OpenDirectory(path, exclusiveCache);
        else handle = WindowsFiles.OpenDirectory(Pin(parentPath, create), Path.GetFileName(path), create, exclusiveCache);
        handles.Add(path, handle);
        ordered.Add(handle);
        if (exclusiveCache) cache = handle;
        return handle;
    }

    internal FileStream OpenPartial(string path)
    {
        var parent = Pin(Path.GetDirectoryName(path)!);
        return WindowsFiles.OpenFile(parent, Path.GetFileName(path), partial: true);
    }

    internal FileStream OpenRead(string path)
    {
        var parent = Pin(Path.GetDirectoryName(path)!);
        return WindowsFiles.OpenFile(parent, Path.GetFileName(path), partial: false);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { if (cache is not null) WindowsFiles.RemoveEmptyDirectory(cache); }
        finally { for (var i = ordered.Count - 1; i >= 0; i--) ordered[i].Dispose(); }
    }
}
