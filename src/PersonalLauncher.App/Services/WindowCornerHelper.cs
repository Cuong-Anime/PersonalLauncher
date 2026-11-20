using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PersonalLauncher.App.Services;

/// <summary>
/// Enables native Windows 11 DWM rounded corners (DWMWCP_ROUND) on borderless WPF windows and dialogs.
/// </summary>
public static class WindowCornerHelper
{
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    public static void EnableRoundedCorners(Window window)
    {
        if (window is null)
            return;

        window.SourceInitialized += (_, _) => Apply(window);
        if (PresentationSource.FromVisual(window) is HwndSource)
            Apply(window);
    }

    private static void Apply(Window window)
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
                return;

            int preference = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
        }
        catch
        {
            // Silently ignore on older Windows versions that do not support DWMWA_WINDOW_CORNER_PREFERENCE
        }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int attr,
        ref int attrValue,
        int attrSize);
}
