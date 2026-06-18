using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Vortice.MediaFoundation;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using static Vortice.Direct3D11.D3D11;

namespace Capper;

public sealed record RecordingResult(bool Success, string OutputPath, double Seconds, string? Error);

/// <summary>
/// Captures a single window via Windows.Graphics.Capture and encodes it to an H.264 MP4
/// with Media Foundation at a fixed average bitrate (so output size is predictable).
///
/// Frames are pulled from the capture pool into a CPU buffer; a separate encode thread
/// writes that latest frame at the configured frame rate (constant frame rate), which keeps
/// the clip valid even when the window content is static and makes the size estimate accurate.
/// </summary>
public sealed class WindowCaptureRecorder : IDisposable
{
    private static readonly Guid ID3D11Texture2DIid = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    // MF_MT_DEFAULT_STRIDE: positive value => top-down image orientation.
    private static readonly Guid MF_MT_DEFAULT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");

    /// <summary>Raised once when recording ends (manual stop, auto-stop, or error). Marshalled by caller.</summary>
    public event Action<RecordingResult>? Stopped;

    public bool IsRecording { get; private set; }

    /// <summary>Diagnostic: true once recording starts if the GPU video-processor scaler is in use
    /// (as opposed to no scaling or the Media Foundation fallback).</summary>
    public bool UsedGpuScaler => _scaler != null;

    /// <summary>Diagnostic: which encoder tunings applied (B-frames, quality-vs-speed, …).</summary>
    public string EncoderConfigLog { get; private set; } = "";

    // D3D / capture
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDirect3DDevice? _d3dDevice;
    private ID3D11Texture2D? _staging;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private GraphicsCaptureItem? _item;
    private SizeInt32 _poolSize;

    // MF
    private IMFSinkWriter? _writer;
    private int _streamIndex;
    private readonly object _writerLock = new(); // serialize WriteSample/Finalize across A/V threads

    // Audio (WASAPI loopback -> AAC)
    private WasapiLoopbackCapture? _audioCapture;
    private int _audioStreamIndex = -1;
    private int _audioSampleRate;
    private int _audioChannels;
    private WaveFormat? _audioSrcFormat;
    private long _audioTimeHns;
    private bool _audioEnabled;
    private readonly Stopwatch _audioClock = new();

    // Encode state
    private int _encW, _encH, _fps;   // capture canvas (the size we read back + feed to MF)
    private int _outW, _outH;         // encoded output size (preset target)
    private FrameScaler? _scaler;     // GPU downscaler; null = no scaling / MF scales instead
    private string _outputPath = "";
    private byte[] _frameBuffer = Array.Empty<byte>();
    private readonly object _bufLock = new();
    private readonly ManualResetEventSlim _firstFrame = new(false);
    private volatile bool _stop;
    private Thread? _encodeThread;
    private double _recordedSeconds;

    private readonly object _finishLock = new();
    private bool _finalized;

    public void Start(GraphicsCaptureItem item, AppConfig cfg, string outputPath)
    {
        var size = item.Size;
        if (size.Width <= 0 || size.Height <= 0)
            throw new InvalidOperationException("The capture target has no visible area.");

        int srcW = Math.Max(2, size.Width & ~1);
        int srcH = Math.Max(2, size.Height & ~1);
        _fps = Math.Clamp(cfg.Fps, 1, 60);
        _outputPath = outputPath;

        // Encoded output resolution from the preset's target height (aspect preserved, never upscaled).
        int outH = cfg.TargetHeight <= 0 ? srcH : Math.Min(cfg.TargetHeight, srcH);
        int outW = (int)Math.Round((double)srcW * outH / srcH);
        _outH = Math.Max(2, outH & ~1);
        _outW = Math.Max(2, outW & ~1);

        // --- Direct3D device (BGRA support required for WGC interop) ---
        var levels = new[]
        {
            FeatureLevel.Level_11_1, FeatureLevel.Level_11_0,
            FeatureLevel.Level_10_1, FeatureLevel.Level_10_0,
        };
        D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            levels, out _device, out _context).CheckError();
        using (var dxgi = _device!.QueryInterface<IDXGIDevice>())
        {
            _d3dDevice = Native.CreateDirect3DDevice(dxgi.NativePointer);
        }

        // GPU-scale the source down to the output size when downscaling is needed. If the video
        // processor can't be created, fall back to feeding the source-sized canvas and letting
        // Media Foundation scale during encode.
        if (_outW != srcW || _outH != srcH)
        {
            try { _scaler = new FrameScaler(_device, _context!, _outW, _outH, _fps); }
            catch { _scaler = null; }
        }

        // Capture canvas = what we read back and feed to MF. With the scaler it's already the output
        // size (MF doesn't rescale); otherwise it's the source size (MF rescales to the output).
        if (_scaler != null) { _encW = _outW; _encH = _outH; }
        else { _encW = srcW; _encH = srcH; }

        // --- CPU-readable staging texture (encode size) ---
        var desc = new Texture2DDescription
        {
            Width = (uint)_encW,
            Height = (uint)_encH,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        };
        _staging = _device.CreateTexture2D(desc);
        _frameBuffer = new byte[_encW * _encH * 4];

        // --- Media Foundation sink writer ---
        MediaFactory.MFStartup(false).CheckError();
        _writer = MediaFactory.MFCreateSinkWriterFromURL(outputPath, null, null);

        var outType = MediaFactory.MFCreateMediaType();
        outType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        outType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
        outType.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)(cfg.VideoBitrateKbps * 1000));
        outType.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)2); // progressive
        // High profile (100) instead of the encoder's Baseline default → CABAC + 8x8 transform +
        // B-frame support, i.e. noticeably better quality at the same bitrate.
        outType.Set(MediaTypeAttributeKeys.Mpeg2Profile, (uint)100);
        outType.Set(MediaTypeAttributeKeys.FrameSize, Pack((uint)_outW, (uint)_outH)); // scaled target
        outType.Set(MediaTypeAttributeKeys.FrameRate, Pack((uint)_fps, 1u));
        outType.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1u, 1u));
        _streamIndex = _writer.AddStream(outType);
        outType.Dispose();

        var inType = MediaFactory.MFCreateMediaType();
        inType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        inType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32);
        inType.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)2);
        inType.Set(MediaTypeAttributeKeys.FrameSize, Pack((uint)_encW, (uint)_encH));
        inType.Set(MediaTypeAttributeKeys.FrameRate, Pack((uint)_fps, 1u));
        inType.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1u, 1u));
        inType.Set(MF_MT_DEFAULT_STRIDE, (uint)(_encW * 4));

        // VBR encoding (default): for short clips, variable bitrate gives better quality per byte.
        // Output size isn't capped here — the user trims to a length after recording.
        _writer.SetInputMediaType(_streamIndex, inType, null);
        inType.Dispose();

        // Tune the now-instantiated encoder MFT (B-frames, quality-vs-speed) for better quality/byte.
        try
        {
            IntPtr codecPtr = _writer.GetServiceForStream(_streamIndex, Guid.Empty, H264EncoderConfig.IID_ICodecAPI);
            EncoderConfigLog = codecPtr != IntPtr.Zero ? H264EncoderConfig.Apply(codecPtr, cfg.VideoBitrateKbps) : "codecapi: null";
        }
        catch (Exception ex) { EncoderConfigLog = "codecapi: " + ex.Message; }

        // Optional system-audio (loopback) stream, added before BeginWriting.
        if (cfg.AudioSource == AudioSource.System)
            SetupAudio(cfg);

        _writer.BeginWriting();

        // --- Start capture ---
        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _d3dDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
        _session = _framePool.CreateCaptureSession(item);
        _framePool.FrameArrived += OnFrameArrived;
        _item = item;
        _poolSize = size;
        _item.Closed += OnItemClosed; // window closed -> auto-stop and finalize

        IsRecording = true;
        _session.StartCapture();

        _encodeThread = new Thread(EncodeLoop) { IsBackground = true, Name = "Capper.Encode" };
        _encodeThread.Start();

        if (_audioEnabled)
        {
            try { _audioClock.Restart(); _audioCapture!.StartRecording(); }
            catch { _audioEnabled = false; }
        }
    }

    /// <summary>
    /// Configure a WASAPI loopback capture and a matching AAC output stream on the sink writer.
    /// Audio is best-effort: if the device format is unsupported it's silently skipped so the
    /// video clip still records.
    /// </summary>
    private void SetupAudio(AppConfig cfg)
    {
        try
        {
            var capture = new WasapiLoopbackCapture();
            var wf = capture.WaveFormat;

            // The MF AAC encoder accepts 16-bit PCM at 44.1 or 48 kHz, mono/stereo.
            int rate = wf.SampleRate;
            if (rate != 44100 && rate != 48000)
            {
                capture.Dispose();
                return;
            }
            int channels = Math.Clamp(wf.Channels, 1, 2);

            var aacOut = MediaFactory.MFCreateMediaType();
            aacOut.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
            aacOut.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac);
            aacOut.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
            aacOut.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)rate);
            aacOut.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)channels);
            aacOut.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)(cfg.AudioBitrateKbps * 1000 / 8));
            _audioStreamIndex = _writer!.AddStream(aacOut);
            aacOut.Dispose();

            var pcmIn = MediaFactory.MFCreateMediaType();
            pcmIn.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
            pcmIn.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm);
            pcmIn.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
            pcmIn.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)rate);
            pcmIn.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)channels);
            pcmIn.Set(MediaTypeAttributeKeys.AudioBlockAlignment, (uint)(channels * 2));
            pcmIn.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)(rate * channels * 2));
            _writer.SetInputMediaType(_audioStreamIndex, pcmIn, null);
            pcmIn.Dispose();

            _audioCapture = capture;
            _audioSrcFormat = wf;
            _audioSampleRate = rate;
            _audioChannels = channels;
            _audioCapture.DataAvailable += OnAudioData;
            _audioEnabled = true;
        }
        catch
        {
            _audioEnabled = false;
        }
    }

    private void OnAudioData(object? sender, WaveInEventArgs e)
    {
        if (_stop || _finalized || e.BytesRecorded <= 0) return;

        byte[] pcm = ConvertToPcm16(e.Buffer, e.BytesRecorded, _audioSrcFormat!, _audioChannels, out int frames);
        if (frames <= 0) return;

        long durationHns = frames * 10_000_000L / _audioSampleRate;

        // WASAPI loopback delivers no packets during total silence, which would let the audio
        // timeline fall behind the wall-clock-paced video. This buffer's audio ends at ~now, so
        // it should start at (now - duration); if that's meaningfully ahead of where we've written
        // to, fill the gap with silence to keep A/V in sync.
        long bufStartHns = _audioClock.Elapsed.Ticks - durationHns; // TimeSpan.Ticks are 100 ns
        long gapHns = bufStartHns - _audioTimeHns;
        if (gapHns > 150_000) // > 15 ms
            WriteSilence(gapHns);

        WriteAudioSample(pcm, durationHns);
    }

    /// <summary>Write a PCM16 buffer at the current audio position, advancing the audio clock.</summary>
    private void WriteAudioSample(byte[] pcm, long durationHns)
    {
        var buffer = MediaFactory.MFCreateMemoryBuffer(pcm.Length);
        buffer.Lock(out IntPtr dst, out _, out _);
        Marshal.Copy(pcm, 0, dst, pcm.Length);
        buffer.Unlock();
        buffer.CurrentLength = pcm.Length;

        var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = _audioTimeHns;
        sample.SampleDuration = durationHns;
        _audioTimeHns += durationHns;

        lock (_writerLock)
        {
            if (!_finalized) _writer!.WriteSample(_audioStreamIndex, sample);
        }
        sample.Dispose();
        buffer.Dispose();
    }

    /// <summary>Fill <paramref name="gapHns"/> of the audio timeline with silence (chunked so a
    /// long quiet stretch doesn't allocate one huge buffer).</summary>
    private void WriteSilence(long gapHns)
    {
        const long ChunkHns = 1_000_000; // 100 ms
        while (gapHns > 0 && !_stop && !_finalized)
        {
            long thisHns = Math.Min(gapHns, ChunkHns);
            int frames = (int)(thisHns * _audioSampleRate / 10_000_000L);
            if (frames <= 0) break;
            long durHns = frames * 10_000_000L / _audioSampleRate;
            WriteAudioSample(new byte[frames * _audioChannels * 2], durHns); // zeroed = silence
            gapHns -= durHns;
        }
    }

    /// <summary>Convert a captured buffer (float or 16-bit PCM, N channels) to interleaved
    /// 16-bit PCM with the target channel count.</summary>
    private static byte[] ConvertToPcm16(byte[] src, int bytes, WaveFormat fmt, int dstChannels, out int frames)
    {
        int srcChannels = Math.Max(1, fmt.Channels);
        bool isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat && fmt.BitsPerSample == 32;
        bool isPcm16 = fmt.Encoding == WaveFormatEncoding.Pcm && fmt.BitsPerSample == 16;
        if (!isFloat && !isPcm16) { frames = 0; return Array.Empty<byte>(); }

        int srcSampleBytes = fmt.BitsPerSample / 8;
        frames = bytes / (srcSampleBytes * srcChannels);
        var outBytes = new byte[frames * dstChannels * 2];

        for (int f = 0; f < frames; f++)
        {
            for (int dc = 0; dc < dstChannels; dc++)
            {
                // Map destination channel to a source channel (or downmix to mono).
                short value;
                if (dstChannels == 1)
                {
                    float acc = 0;
                    for (int sc = 0; sc < srcChannels; sc++)
                        acc += ReadSample(src, f, sc, srcChannels, srcSampleBytes, isFloat);
                    value = ToShort(acc / srcChannels);
                }
                else
                {
                    int sc = Math.Min(dc, srcChannels - 1);
                    value = ToShort(ReadSample(src, f, sc, srcChannels, srcSampleBytes, isFloat));
                }
                int o = (f * dstChannels + dc) * 2;
                outBytes[o] = (byte)(value & 0xFF);
                outBytes[o + 1] = (byte)((value >> 8) & 0xFF);
            }
        }
        return outBytes;
    }

    private static float ReadSample(byte[] src, int frame, int ch, int srcChannels, int sampleBytes, bool isFloat)
    {
        int idx = (frame * srcChannels + ch) * sampleBytes;
        if (isFloat) return BitConverter.ToSingle(src, idx);
        short s = (short)(src[idx] | (src[idx + 1] << 8));
        return s / 32768f;
    }

    private static short ToShort(float v)
    {
        v = Math.Clamp(v, -1f, 1f);
        return (short)(v * 32767f);
    }

    private unsafe void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        if (_stop || _finalized) return;
        using var frame = sender.TryGetNextFrame();
        if (frame == null) return;

        var content = frame.ContentSize;
        // Follow the target as it resizes (e.g. a window toggling fullscreen). The encoder's canvas
        // stays fixed, but the frame pool is recreated to the new size so frames keep flowing.
        if (content.Width > 0 && content.Height > 0 &&
            (content.Width != _poolSize.Width || content.Height != _poolSize.Height))
        {
            _poolSize = content;
            try { sender.Recreate(_d3dDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, content); }
            catch { /* next frame arrives at the new size */ }
            return;
        }

        using var surface = frame.Surface;
        IntPtr texPtr = Native.GetDxgiInterface(surface, ID3D11Texture2DIid);
        if (texPtr == IntPtr.Zero) return;
        using var srcTex = new ID3D11Texture2D(texPtr);

        var ctx = _context;
        var staging = _staging;
        if (ctx == null || staging == null) return;

        // GPU path: scale the source down to the output size, then read back the (small) result.
        if (_scaler != null)
        {
            try { _scaler.Blit(srcTex, content.Width, content.Height); }
            catch { return; }
            ctx.CopyResource(staging, _scaler.Output);

            var sm = ctx.Map((ID3D11Resource)staging, 0u, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                int stride = _encW * 4;
                lock (_bufLock)
                {
                    fixed (byte* dstBase = _frameBuffer)
                    {
                        byte* s = (byte*)sm.DataPointer;
                        for (int y = 0; y < _encH; y++)
                            Buffer.MemoryCopy(s + (long)y * sm.RowPitch, dstBase + (long)y * stride, stride, stride);
                    }
                }
            }
            finally { ctx.Unmap((ID3D11Resource)staging, 0u); }
            _firstFrame.Set();
            return;
        }

        // CPU path (no scaler): copy as much of the frame as fits the fixed canvas; black-fill any
        // remainder so a smaller frame (e.g. window shrank) letterboxes cleanly instead of stale pixels.
        int cw = Math.Min(_encW, Math.Max(0, content.Width) & ~1);
        int ch = Math.Min(_encH, Math.Max(0, content.Height) & ~1);
        if (cw <= 0 || ch <= 0) return;
        ctx.CopySubresourceRegion(staging, 0u, 0u, 0u, 0u, srcTex, 0u, new Box(0, 0, 0, cw, ch, 1));

        var map = ctx.Map((ID3D11Resource)staging, 0u, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            int dstStride = _encW * 4;
            int copyBytes = cw * 4;
            lock (_bufLock)
            {
                fixed (byte* dstBase = _frameBuffer)
                {
                    byte* src = (byte*)map.DataPointer;
                    for (int y = 0; y < _encH; y++)
                    {
                        byte* dstRow = dstBase + (long)y * dstStride;
                        if (y < ch)
                        {
                            Buffer.MemoryCopy(src + (long)y * map.RowPitch, dstRow, dstStride, copyBytes);
                            if (copyBytes < dstStride)
                                new Span<byte>(dstRow + copyBytes, dstStride - copyBytes).Clear();
                        }
                        else
                        {
                            new Span<byte>(dstRow, dstStride).Clear();
                        }
                    }
                }
            }
        }
        finally
        {
            ctx.Unmap((ID3D11Resource)staging, 0u);
        }
        _firstFrame.Set();
    }

    private void OnItemClosed(GraphicsCaptureItem sender, object args)
    {
        // The captured window was closed mid-recording: finalize what we have.
        try { Stop(); } catch { }
    }

    private void EncodeLoop()
    {
        // Give the first real frame a moment so the clip doesn't start on a black frame.
        _firstFrame.Wait(2000);

        long durationHns = 10_000_000L / _fps;
        var local = new byte[_frameBuffer.Length];
        var sw = Stopwatch.StartNew();
        long idx = 0;
        string? error = null;

        try
        {
            while (!_stop)
            {
                lock (_bufLock) Array.Copy(_frameBuffer, local, local.Length);

                var buffer = MediaFactory.MFCreateMemoryBuffer(local.Length);
                buffer.Lock(out IntPtr dst, out _, out _);
                Marshal.Copy(local, 0, dst, local.Length);
                buffer.Unlock();
                buffer.CurrentLength = local.Length;

                var sample = MediaFactory.MFCreateSample();
                sample.AddBuffer(buffer);
                sample.SampleTime = idx * durationHns;
                sample.SampleDuration = durationHns;
                lock (_writerLock)
                {
                    if (!_finalized) _writer!.WriteSample(_streamIndex, sample);
                }
                sample.Dispose();
                buffer.Dispose();

                idx++;

                // Pace to wall-clock so the file plays back at real speed.
                double nextMs = idx * 1000.0 / _fps;
                int sleep = (int)(nextMs - sw.Elapsed.TotalMilliseconds);
                if (sleep > 1) Thread.Sleep(sleep);
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        _recordedSeconds = idx / (double)_fps;
        FinishInternal(error);
    }

    /// <summary>Manual stop. Blocks until the file is finalized.</summary>
    public void Stop()
    {
        if (!IsRecording) return;
        _stop = true;
        _encodeThread?.Join(8000);
        FinishInternal(null);
    }

    private void FinishInternal(string? error)
    {
        lock (_finishLock)
        {
            if (_finalized) return;
            _finalized = true;
        }

        try
        {
            if (_audioCapture != null)
            {
                _audioCapture.DataAvailable -= OnAudioData;
                _audioCapture.StopRecording();
                _audioCapture.Dispose();
            }
        }
        catch { }

        try { if (_item != null) _item.Closed -= OnItemClosed; } catch { }
        try { if (_framePool != null) _framePool.FrameArrived -= OnFrameArrived; } catch { }
        try { _session?.Dispose(); } catch { }
        try { _framePool?.Dispose(); } catch { }

        try
        {
            if (_writer != null)
            {
                lock (_writerLock)
                {
                    if (error == null) _writer.Finalize();
                }
                _writer.Dispose();
            }
        }
        catch (Exception ex) { error ??= ex.Message; }

        try { _scaler?.Dispose(); } catch { }
        try { _staging?.Dispose(); } catch { }
        try { _context?.Dispose(); } catch { }
        try { _device?.Dispose(); } catch { }
        try { _d3dDevice?.Dispose(); } catch { }
        try { MediaFactory.MFShutdown(); } catch { }

        _session = null; _framePool = null; _writer = null;
        _staging = null; _context = null; _device = null; _d3dDevice = null; _item = null;
        _audioCapture = null; _scaler = null;

        IsRecording = false;
        Stopped?.Invoke(new RecordingResult(error == null, _outputPath, _recordedSeconds, error));
    }

    private static ulong Pack(uint high, uint low) => ((ulong)high << 32) | low;

    public void Dispose()
    {
        try { Stop(); } catch { }
        _firstFrame.Dispose();
    }
}
