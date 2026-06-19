using System.Drawing;
using System.Drawing.Drawing2D;

namespace Capper;

/// <summary>Small GDI+ drawing helpers shared by the WinForms surfaces.</summary>
internal static class GdiHelpers
{
    /// <summary>A rounded-rectangle path with the given corner radius, used both to clip a form's
    /// region and to stroke its border.</summary>
    public static GraphicsPath RoundedRectangle(Rectangle r, int radius)
    {
        int d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
