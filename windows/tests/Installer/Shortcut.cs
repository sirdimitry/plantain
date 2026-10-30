using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace Plantain.InstallerTests
{
    // Explicit Unicode interface: legacy WScript.Shell can lose Cyrillic paths.
    public static class Shortcut
    {
        [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path,
                int characters, IntPtr findData, uint flags);
        }

        public static string Target(string path)
        {
            var instance = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046")));
            try
            {
                ((IPersistFile)instance).Load(path, 0);
                var result = new StringBuilder(32768);
                ((IShellLinkW)instance).GetPath(result, result.Capacity, IntPtr.Zero, 0);
                return result.ToString();
            }
            finally { Marshal.FinalReleaseComObject(instance); }
        }
    }
}
