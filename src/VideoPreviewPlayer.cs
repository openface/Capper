using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace Capper;

/// <summary>
/// Real audio + video preview of a clip, using <see cref="MediaPlayer"/> in frame-server mode.
/// Audio plays through the default output automatically; each decoded video frame is copied into a
/// small D3D11 texture, read back, and raised as raw BGRA via <see cref="FrameReady"/> (no GDI
/// dependency here, so the pipeline is unit-testable). The dialog turns those bytes into a bitmap.
/// </summary>
internal sealed class VideoPreviewPlayer : IDisposable
{
    private readonly MediaPlayer _player;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _ctx;
    private ID3D11Texture2D? _rt;       // CopyFrameToVideoSurface destination (render-target + SRV)
    private ID3D11Texture2D? _staging;  // CPU-readable copy
    private IDirect3DSurface? _surface;
    private readonly int _w, _h;
    private readonly object _gate = new();
    private volatile bool _disposed;

    /// <summary>Raised on a background thread with a fresh BGRA buffer (w*h*4) for each frame.</summary>
    public event Action<byte[], int, int>? FrameReady;
    public event Action? Ended;

    /// <summary>False if the GPU frame copy failed; audio still plays and the caller can fall back
    /// to thumbnail frames for the picture.</summary>
    public bool FrameServerOk { get; private set; } = true;

    public VideoPreviewPlayer(string path, int width, int height)
    {
        _w = Math.Max(2, width & ~1);
        _h = Math.Max(2, height & ~1);
        InitD3D();

        _player = new MediaPlayer { IsVideoFrameServerEnabled = true, AutoPlay = false };
        _player.CommandManager.IsEnabled = false; // we drive transport ourselves
        _player.Source = MediaSource.CreateFromUri(new Uri(path));
        _player.VideoFrameAvailable += OnVideoFrameAvailable;
        _player.MediaEnded += (_, _) => Ended?.Invoke();
    }

    public TimeSpan Position
    {
        get { try { return _player.PlaybackSession.Position; } catch { return TimeSpan.Zero; } }
        set { try { _player.PlaybackSession.Position = value; } catch { } }
    }

    public bool IsPlaying
    {
        get { try { return _player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing; } catch { return false; } }
    }

    public void Play() { try { _player.Play(); } catch { } }
    public void Pause() { try { _player.Pause(); } catch { } }

    private void InitD3D()
    {
        Direct3DHelpers.CreateBgraDevice(out _device, out _ctx);

        _rt = _device!.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)_w, Height = (uint)_h, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None, MiscFlags = ResourceOptionFlags.None,
        });
        _staging = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)_w, Height = (uint)_h, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging, BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read, MiscFlags = ResourceOptionFlags.None,
        });

        using var dxgiSurface = _rt.QueryInterface<IDXGISurface>();
        _surface = Native.CreateDirect3DSurface(dxgiSurface.NativePointer);
    }

    private void OnVideoFrameAvailable(MediaPlayer sender, object args)
    {
        if (_disposed || !FrameServerOk) return;
        try
        {
            lock (_gate)
            {
                if (_disposed || _surface == null || _ctx == null || _staging == null || _rt == null) return;

                sender.CopyFrameToVideoSurface(_surface);                  // scales the frame to _w x _h
                _ctx.CopyResource(_staging, _rt);

                var buf = new byte[_h * _w * 4];
                Direct3DHelpers.ReadStagingRows(_ctx, _staging, _w, _h, buf);
                FrameReady?.Invoke(buf, _w, _h);
            }
        }
        catch
        {
            FrameServerOk = false; // audio keeps playing; caller falls back to thumbnails for video
        }
    }

    public void Dispose()
    {
        _disposed = true;
        try { _player.VideoFrameAvailable -= OnVideoFrameAvailable; } catch { }
        try { _player.Pause(); } catch { }
        try { _player.Source = null; } catch { }
        try { _player.Dispose(); } catch { }
        lock (_gate)
        {
            try { _surface?.Dispose(); } catch { }
            try { _staging?.Dispose(); } catch { }
            try { _rt?.Dispose(); } catch { }
            try { _ctx?.Dispose(); } catch { }
            try { _device?.Dispose(); } catch { }
            _surface = null; _staging = null; _rt = null; _ctx = null; _device = null;
        }
    }
}
