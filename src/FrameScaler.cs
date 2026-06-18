using Vortice;                 // RawRect
using Vortice.Direct3D11;
using Vortice.DXGI;            // Rational
using Vortice.Mathematics;     // Color4

namespace Capper;

/// <summary>
/// GPU-scales a captured frame down to a fixed output size using the Direct3D 11 video processor,
/// preserving aspect ratio (letterboxed on black). Doing the scale on the GPU keeps the per-frame
/// CPU readback at the small output size instead of the full source size.
/// </summary>
internal sealed class FrameScaler : IDisposable
{
    private readonly ID3D11VideoDevice _vdev;
    private readonly ID3D11VideoContext _vctx;
    private readonly ID3D11DeviceContext _ctx;
    private readonly ID3D11Texture2D _output;
    private readonly ID3D11RenderTargetView _rtv;
    private readonly int _outW, _outH, _fps;

    private ID3D11VideoProcessorEnumerator? _enum;
    private ID3D11VideoProcessor? _proc;
    private ID3D11VideoProcessorOutputView? _outView;
    private int _srcW, _srcH;

    /// <summary>The scaled output texture (output size, BGRA). Valid after <see cref="Blit"/>.</summary>
    public ID3D11Texture2D Output => _output;

    public FrameScaler(ID3D11Device device, ID3D11DeviceContext context, int outW, int outH, int fps)
    {
        _ctx = context;
        _outW = outW; _outH = outH; _fps = Math.Max(1, fps);
        _vdev = device.QueryInterface<ID3D11VideoDevice>();
        _vctx = context.QueryInterface<ID3D11VideoContext>();

        _output = device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)outW, Height = (uint)outH, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None, MiscFlags = ResourceOptionFlags.None,
        });
        _rtv = device.CreateRenderTargetView(_output);
    }

    private void EnsureProcessor(int srcW, int srcH)
    {
        if (_proc != null && srcW == _srcW && srcH == _srcH) return;
        _outView?.Dispose(); _proc?.Dispose(); _enum?.Dispose();
        _outView = null; _proc = null; _enum = null;

        var content = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputFrameRate = new Rational((uint)_fps, 1u),
            InputWidth = (uint)srcW, InputHeight = (uint)srcH,
            OutputFrameRate = new Rational((uint)_fps, 1u),
            OutputWidth = (uint)_outW, OutputHeight = (uint)_outH,
            Usage = VideoUsage.PlaybackNormal,
        };
        _enum = _vdev.CreateVideoProcessorEnumerator(content);
        _proc = _vdev.CreateVideoProcessor(_enum, 0);
        _outView = _vdev.CreateVideoProcessorOutputView(_output, _enum, new VideoProcessorOutputViewDescription
        {
            ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 },
        });

        _vctx.VideoProcessorSetStreamFrameFormat(_proc, 0, VideoFrameFormat.Progressive);
        _vctx.VideoProcessorSetStreamAutoProcessingMode(_proc, 0, false); // plain scale, no extras
        _srcW = srcW; _srcH = srcH;
    }

    /// <summary>Scale <paramref name="source"/>'s top-left (srcW x srcH) region into <see cref="Output"/>.</summary>
    public void Blit(ID3D11Texture2D source, int srcW, int srcH)
    {
        if (srcW <= 0 || srcH <= 0) return;
        EnsureProcessor(srcW, srcH);

        using var inView = _vdev.CreateVideoProcessorInputView(source, _enum!, new VideoProcessorInputViewDescription
        {
            FourCC = 0,
            ViewDimension = VideoProcessorInputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 },
        });

        var dest = FitRect(srcW, srcH, _outW, _outH);
        _vctx.VideoProcessorSetStreamSourceRect(_proc!, 0, true, new RawRect(0, 0, srcW, srcH));
        _vctx.VideoProcessorSetStreamDestRect(_proc!, 0, true, dest);

        _ctx.ClearRenderTargetView(_rtv, new Color4(0f, 0f, 0f, 1f)); // black letterbox bars

        var stream = new VideoProcessorStream { Enable = true, InputSurface = inView };
        _vctx.VideoProcessorBlt(_proc!, _outView!, 0, 1, new[] { stream });
    }

    /// <summary>Largest centered rect inside (dw x dh) preserving the source aspect ratio.</summary>
    private static RawRect FitRect(int sw, int sh, int dw, int dh)
    {
        double sa = (double)sw / sh, da = (double)dw / dh;
        int w, h;
        if (sa > da) { w = dw; h = (int)Math.Round(dw / sa); }
        else { h = dh; w = (int)Math.Round(dh * sa); }
        w = Math.Clamp(w & ~1, 2, dw);
        h = Math.Clamp(h & ~1, 2, dh);
        int x = (dw - w) / 2, y = (dh - h) / 2;
        return new RawRect(x, y, x + w, y + h);
    }

    public void Dispose()
    {
        try { _outView?.Dispose(); } catch { }
        try { _proc?.Dispose(); } catch { }
        try { _enum?.Dispose(); } catch { }
        try { _rtv.Dispose(); } catch { }
        try { _output.Dispose(); } catch { }
        try { _vctx.Dispose(); } catch { }
        try { _vdev.Dispose(); } catch { }
    }
}
