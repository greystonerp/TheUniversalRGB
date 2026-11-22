using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace UniversalRGB.Desktop.Services;

/// <summary>Creates Windows .lnk shortcuts through the shell's IShellLink COM object.</summary>
internal static class ShellLink
{
    public static void Create(string shortcutPath, string targetPath, string description, string? arguments = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);
        var link = (IShellLinkW)new ShellLinkObject();
        try
        {
            link.SetPath(targetPath);
            link.SetWorkingDirectory(Path.GetDirectoryName(targetPath)!);
            link.SetDescription(description);
            link.SetIconLocation(targetPath, 0);
            if (!string.IsNullOrEmpty(arguments))
            {
                link.SetArguments(arguments);
            }

            ((IPersistFile)link).Save(shortcutPath, true);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkObject
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maxPath, IntPtr findData, int flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int maxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int maxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int maxIconPath, out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relativePath, int reserved);
        void Resolve(IntPtr hwnd, int flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}
