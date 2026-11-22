using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace UniversalRGB.Desktop.Services;

internal static class WindowHelpers
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_CAPTION_COLOR = 35;

    /// <summary>Dark title bar on Windows 10 (20H1+) and 11; caption color on Windows 11. Failures are ignored.</summary>
    public static void ApplyDarkTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var dark = 1;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        var caption = 0x000F0C0C; // COLORREF 0x00BBGGRR → #0C0C0F
        _ = DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
