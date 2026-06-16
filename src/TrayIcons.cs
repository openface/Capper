using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Clipfoo;

/// <summary>Generates the tray icons at runtime so no .ico asset needs to ship.</summary>
internal static class TrayIcons
{
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    public static Icon Create(bool recording)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            var ring = recording ? Color.FromArgb(220, 60, 60) : Color.FromArgb(72, 132, 204);
            using var ringBrush = new SolidBrush(ring);
            g.FillEllipse(ringBrush, 3, 3, 26, 26);

            using var inner = new SolidBrush(Color.White);
            g.FillEllipse(inner, 9, 9, 14, 14);

            if (recording)
            {
                using var dot = new SolidBrush(Color.FromArgb(220, 60, 60));
                g.FillEllipse(dot, 11, 11, 10, 10);
            }
        }

        IntPtr h = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(h);
            return (Icon)tmp.Clone();
        }
        finally
        {
            DestroyIcon(h);
        }
    }
}
