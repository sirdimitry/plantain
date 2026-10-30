using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Plantain.Core.Tests;

internal static class WindowsFixtures
{
    internal static void HardLink(string name, string target)
    { if (!CreateHardLinkW(name, target, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error()); }

    internal static void Junction(string path, string target)
    {
        Directory.CreateDirectory(path);
        SetJunction(path, target);
    }

    internal static void SetJunction(string path, string target)
    {
        using var handle = CreateFileW(path, 0x40000000, 7, IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var substitute = Encoding.Unicode.GetBytes(@"\??\" + Path.GetFullPath(target));
        var display = Encoding.Unicode.GetBytes(Path.GetFullPath(target));
        var bytes = new byte[16 + substitute.Length + 2 + display.Length + 2];
        BitConverter.GetBytes(0xA0000003u).CopyTo(bytes, 0);
        BitConverter.GetBytes((ushort)(bytes.Length - 8)).CopyTo(bytes, 4);
        BitConverter.GetBytes((ushort)substitute.Length).CopyTo(bytes, 10);
        BitConverter.GetBytes((ushort)(substitute.Length + 2)).CopyTo(bytes, 12);
        BitConverter.GetBytes((ushort)display.Length).CopyTo(bytes, 14);
        substitute.CopyTo(bytes, 16);
        display.CopyTo(bytes, 16 + substitute.Length + 2);
        if (!DeviceIoControl(handle, 0x000900A4, bytes, bytes.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string name, string existing, IntPtr security);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint mode, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint control, byte[] input, int inputSize,
        IntPtr output, int outputSize, out int returned, IntPtr overlapped);
}
