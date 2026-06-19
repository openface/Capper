using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

namespace Capper;

/// <summary>Loads the tray icon from the embedded multi-size <c>Capper.ico</c>, so the tray matches
/// the app's exe icon and stays crisp at any DPI. The same icon is shown idle and while recording.</summary>
internal static class TrayIcons
{
    /// <param name="recording">Unused — idle and recording share one icon; the recording state is
    /// signalled by the overlay pill and the tray tooltip instead.</param>
    public static Icon Create(bool recording)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream("Capper.ico")
            ?? throw new InvalidOperationException("Embedded resource 'Capper.ico' was not found.");
        // Pick the frame closest to the current small-icon size for a crisp tray render.
        return new Icon(stream, SystemInformation.SmallIconSize);
    }
}
