using Vortice.Direct3D;
using Vortice.Direct3D11;
using static Vortice.Direct3D11.D3D11;

namespace Capper;

/// <summary>Shared Direct3D 11 plumbing used by both the recorder and the preview player:
/// creating a BGRA-capable hardware device and reading a CPU-mapped staging texture back into a
/// managed buffer (row-by-row, accounting for the mapped row pitch).</summary>
internal static class Direct3DHelpers
{
    /// <summary>Create a hardware D3D11 device + immediate context with BGRA support (required for
    /// Windows.Graphics.Capture interop and MediaPlayer frame-server copies).</summary>
    public static void CreateBgraDevice(out ID3D11Device device, out ID3D11DeviceContext context)
    {
        var levels = new[]
        {
            FeatureLevel.Level_11_1, FeatureLevel.Level_11_0,
            FeatureLevel.Level_10_1, FeatureLevel.Level_10_0,
        };
        D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            levels, out device, out context).CheckError();
    }

    /// <summary>Map <paramref name="staging"/> for read and copy its first <paramref name="height"/>
    /// rows (each <paramref name="width"/>*4 BGRA bytes) into <paramref name="dest"/>, which must be
    /// at least width*height*4 bytes. The caller owns any locking around <paramref name="dest"/>.</summary>
    public static unsafe void ReadStagingRows(
        ID3D11DeviceContext context, ID3D11Texture2D staging, int width, int height, byte[] dest)
    {
        int stride = width * 4;
        var map = context.Map((ID3D11Resource)staging, 0u, MapMode.Read, MapFlags.None);
        try
        {
            fixed (byte* dst = dest)
            {
                byte* src = (byte*)map.DataPointer;
                for (int y = 0; y < height; y++)
                    Buffer.MemoryCopy(src + (long)y * map.RowPitch, dst + (long)y * stride, stride, stride);
            }
        }
        finally { context.Unmap((ID3D11Resource)staging, 0u); }
    }
}
