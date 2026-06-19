using System.Drawing;
using System.Runtime.InteropServices;

namespace Capper;

/// <summary>
/// The app's shared dark visual theme. A single source of truth for colors so the recording pill,
/// the trim dialog, and the settings window all match (and a restyle only happens here).
/// </summary>
internal static class Theme
{
    public static readonly Color Bg = Color.FromArgb(28, 29, 33);          // window background
    public static readonly Color Surface = Color.FromArgb(36, 38, 44);     // inputs, header bar
    public static readonly Color SurfaceHover = Color.FromArgb(46, 48, 55);
    public static readonly Color Chip = Color.FromArgb(52, 54, 61);        // buttons
    public static readonly Color ChipHover = Color.FromArgb(62, 64, 72);
    public static readonly Color Fg = Color.FromArgb(238, 239, 242);       // primary text
    public static readonly Color Muted = Color.FromArgb(150, 153, 160);    // secondary text
    public static readonly Color Border = Color.FromArgb(60, 62, 70);
    public static readonly Color Accent = Color.FromArgb(232, 72, 72);     // record red
    public static readonly Color AccentHover = Color.FromArgb(244, 96, 96);
    public static readonly Color Good = Color.FromArgb(96, 204, 124);      // trim start handle

    // --- Dark native title bar (DWM) for windows that keep their system chrome ---

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    /// <summary>Paint a window's native title bar dark so it matches the dark client area.
    /// Best-effort: a no-op on Windows builds that don't support the attribute.</summary>
    public static void UseDarkTitleBar(IntPtr handle)
    {
        try
        {
            int on = 1;
            DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
        }
        catch { }
    }
}
