using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Vortice.MediaFoundation;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace Capper;

public sealed record RecordingResult(bool Success, string OutputPath, double Seconds, string? Error);

/// <summary>
/// Captures a single window via Windows.Graphics.Capture and encodes it to an H.264 MP4
/// with Media Foundation at a fixed average bitrate (so output size is predictable).
///
/// Frames are read back from the capture pool into CPU buffers stamped with their capture time and
/// queued to a separate encode thread, which places them on a constant-frame-rate grid
/// (<see cref="FrameSlotter"/>) by capture time, repeating the last frame while the window is static.
/// Constant frame rate keeps the clip valid during static stretches and the size estimate accurate.
/// </summary>
public sealed class WindowCaptureRecorder : IDisposable
{
    private static readonly Guid ID3D11Texture2DIid = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    // MF_MT_DEFAULT_STRIDE: positive value => top-down image orientation.
    private static readonly Guid MF_MT_DEFAULT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");

    /// <summary>Raised once when recording ends (manual stop, auto-stop, or error). Marshalled by caller.</summary>
    public event Action<RecordingResult>? Stopped;

    private volatile bool _isRecording;
    public bool IsRecording => _isRecording;

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
    // Captured frames handed from the capture callback to the encode thread, each stamped with its
    // capture time. Buffers are recycled through _freeBuffers; _pending is bounded so a stalled
    // encoder can't grow memory without limit (when full, the newest pending frame is overwritten).
    private readonly object _queueLock = new();
    private readonly List<(byte[] Buffer, long TimeHns)> _pending = new();
    private readonly Stack<byte[]> _freeBuffers = new();
    private readonly AutoResetEvent _frameSignal = new(false);
    private int _frameBytes, _maxBuffers, _allocatedBuffers;
    private int _captureClockState; // 0 = undecided, 1 = frame.SystemRelativeTime, 2 = arrival time

    /// <summary>Diagnostics: frames received from the capture pool, and total time spent handling them.</summary>
    public int CapturedFrames { get; private set; }
    public double CaptureHandlerMs { get; private set; }
    /// <summary>Diagnostics: longest gap between consecutive captured frames (capture timestamps).</summary>
    public double MaxCaptureGapMs { get; private set; }
    /// <summary>Diagnostics: output frames that repeated the previous frame (no newer capture).</summary>
    public long RepeatedSlots { get; private set; }
    private long _lastCaptureHns = -1;
    private byte[]? _lastWrittenFrame;
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
        Direct3DHelpers.CreateBgraDevice(out _device, out _context);
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
        _frameBytes = _encW * _encH * 4;
        // Up to ~8 frames of slack for encoder hiccups, capped at ~256 MB for huge CPU-path canvases.
        int maxPending = Math.Clamp((int)(256L * 1024 * 1024 / _frameBytes), 2, 8);
        _maxBuffers = maxPending + 2; // + the frame the encoder is holding + one being filled

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
        // Best-effort: if the codec API is unavailable the encoder's defaults still produce a valid clip.
        try
        {
            IntPtr codecPtr = _writer.GetServiceForStream(_streamIndex, Guid.Empty, H264EncoderConfig.IID_ICodecAPI);
            if (codecPtr != IntPtr.Zero) H264EncoderConfig.Apply(codecPtr, cfg.VideoBitrateKbps);
        }
        catch { /* leave the encoder at its defaults */ }

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

        UncapCaptureRate(_session);

        _isRecording = true;
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
    /// By default Windows.Graphics.Capture delivers at most ~60 frames/s (a 16.7 ms minimum interval),
    /// rounded up to whole vsyncs, so on a 144 Hz display it delivers every 3rd vsync = 48 Hz. 48 Hz
    /// can't be sampled evenly at 30 fps: output frames alternate between advancing 1 and 2 source
    /// frames, so motion lurches between half and double speed. Asking for every vsync lets each 30 fps
    /// slot pick a frame within one vsync of its time (4-5 vsyncs apart at 144 Hz, exactly 2 at 60 Hz).
    /// Skipped for very large CPU-path canvases, where reading back every vsync costs too much.
    /// Best-effort: MinUpdateInterval needs Windows 11 24H2+; older builds keep the default rate.
    /// </summary>
    private void UncapCaptureRate(GraphicsCaptureSession session)
    {
        const int MaxUncappedFrameBytes = 1920 * 1080 * 4;
        if (_frameBytes > MaxUncappedFrameBytes) return;
        try
        {
            if (Windows.Foundation.Metadata.ApiInformation.IsPropertyPresent(
                    "Windows.Graphics.Capture.GraphicsCaptureSession", "MinUpdateInterval"))
                session.MinUpdateInterval = TimeSpan.FromMilliseconds(1);
        }
        catch { /* keep the default rate */ }
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

        long handlerStartHns = QpcNowHns();
        long captureHns = CaptureTimeHns(frame);

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

            var buf = RentBuffer();
            Direct3DHelpers.ReadStagingRows(ctx, staging, _encW, _encH, buf);
            EnqueueFrame(buf, captureHns, handlerStartHns);
            return;
        }

        // CPU path (no scaler): copy as much of the frame as fits the fixed canvas; black-fill any
        // remainder so a smaller frame (e.g. window shrank) letterboxes cleanly instead of stale pixels.
        int cw = Math.Min(_encW, Math.Max(0, content.Width) & ~1);
        int ch = Math.Min(_encH, Math.Max(0, content.Height) & ~1);
        if (cw <= 0 || ch <= 0) return;
        ctx.CopySubresourceRegion(staging, 0u, 0u, 0u, 0u, srcTex, 0u, new Box(0, 0, 0, cw, ch, 1));

        var dstBuf = RentBuffer();
        var map = ctx.Map((ID3D11Resource)staging, 0u, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            int dstStride = _encW * 4;
            int copyBytes = cw * 4;
            fixed (byte* dstBase = dstBuf)
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
        finally
        {
            ctx.Unmap((ID3D11Resource)staging, 0u);
        }
        EnqueueFrame(dstBuf, captureHns, handlerStartHns);
    }

    /// <summary>QueryPerformanceCounter "now" in 100 ns units — the clock frame.SystemRelativeTime uses.</summary>
    private static long QpcNowHns() =>
        (long)((Int128)Stopwatch.GetTimestamp() * 10_000_000 / Stopwatch.Frequency);

    /// <summary>
    /// When the frame was composed. Prefers the system's own capture timestamp (exact, vsync-aligned);
    /// if that turns out not to be on the QPC clock (sanity-checked on the first frame), falls back to
    /// arrival time for the whole recording so timestamps never mix clocks.
    /// </summary>
    private long CaptureTimeHns(Direct3D11CaptureFrame frame)
    {
        long now = QpcNowHns();
        long sys = frame.SystemRelativeTime.Ticks;
        if (_captureClockState == 0)
            _captureClockState = Math.Abs(now - sys) < 10_000_000 ? 1 : 2; // within 1 s of now?
        return _captureClockState == 1 ? Math.Min(sys, now) : now;
    }

    /// <summary>
    /// A frame-sized buffer for the capture thread to fill: a recycled one, a new one (up to the cap),
    /// or, if the encoder has fallen so far behind that every buffer is queued, the newest pending
    /// frame is reclaimed and overwritten (dropping it; the encoder then repeats the frame before it).
    /// </summary>
    private byte[] RentBuffer()
    {
        lock (_queueLock)
        {
            if (_freeBuffers.Count > 0) return _freeBuffers.Pop();
            if (_allocatedBuffers < _maxBuffers || _pending.Count == 0)
            {
                _allocatedBuffers++;
                return new byte[_frameBytes];
            }
            var last = _pending[^1];
            _pending.RemoveAt(_pending.Count - 1);
            return last.Buffer;
        }
    }

    private void ReturnBuffer(byte[] buf)
    {
        lock (_queueLock) _freeBuffers.Push(buf);
    }

    private void EnqueueFrame(byte[] buf, long captureHns, long handlerStartHns)
    {
        CapturedFrames++;
        if (_lastCaptureHns >= 0) MaxCaptureGapMs = Math.Max(MaxCaptureGapMs, (captureHns - _lastCaptureHns) / 10_000.0);
        _lastCaptureHns = captureHns;
        CaptureHandlerMs += (QpcNowHns() - handlerStartHns) / 10_000.0;
        lock (_queueLock) _pending.Add((buf, captureHns));
        try { _frameSignal.Set(); } catch (ObjectDisposedException) { }
    }

    private bool TryDequeueFrame(out (byte[] Buffer, long TimeHns) frame)
    {
        lock (_queueLock)
        {
            if (_pending.Count == 0) { frame = default; return false; }
            frame = _pending[0];
            _pending.RemoveAt(0);
            return true;
        }
    }

    private void OnItemClosed(GraphicsCaptureItem sender, object args)
    {
        // The captured window was closed mid-recording: finalize what we have.
        try { Stop(); } catch { }
    }

    /// <summary>
    /// Turns the irregular stream of captured frames (Windows.Graphics.Capture only delivers one when
    /// the window changes) into constant-frame-rate video. <see cref="FrameSlotter"/> lays a fixed
    /// 1/fps grid over the capture timeline; each slot shows the newest frame captured at or before
    /// its time. Slots are closed by the <i>next frame's capture timestamp</i>, not by when this thread
    /// wakes up, so Windows' ~15.6 ms sleep granularity can't jitter the sampling. (It used to: the
    /// loop overslept, then grabbed twice in a row, duplicating one frame and skipping the next, which
    /// plays back as stutter.) The wall clock only closes slots while the window is static.
    /// Timestamps come from the slot index, so playback speed stays true even if the encoder lags.
    /// </summary>
    private void EncodeLoop()
    {
        // How late a frame can reach us after its capture timestamp (GPU readback + scheduling). Only
        // used to close slots during static stretches, where a little extra latency is invisible.
        const long CaptureLatencyHns = 1_000_000; // 100 ms

        string? error = null;
        FrameSlotter? slotter = null;

        try
        {
            // Give the first real frame a moment so the clip doesn't start on a black frame.
            var wait = Stopwatch.StartNew();
            (byte[] Buffer, long TimeHns) first;
            while (!TryDequeueFrame(out first) && !_stop && wait.ElapsedMilliseconds < 2000)
                _frameSignal.WaitOne(50);

            byte[] held;
            if (first.Buffer != null)
            {
                held = first.Buffer;
                slotter = FrameSlotter.StartingAt(_fps, first.TimeHns);
            }
            else
            {
                held = new byte[_frameBytes]; // no frame yet: start black
                slotter = new FrameSlotter(_fps, QpcNowHns());
            }

            while (true)
            {
                // Each new frame closes every slot before its capture time; those show the held frame.
                while (TryDequeueFrame(out var f))
                {
                    for (long n = slotter.SlotsBefore(f.TimeHns); n > 0; n--)
                        WriteVideoSlot(held, slotter);
                    ReturnBuffer(held);
                    held = f.Buffer;
                }

                if (_stop)
                {
                    // Fill the timeline up to the moment recording stopped, then finish.
                    for (long n = slotter.SlotsBefore(QpcNowHns()); n > 0; n--)
                        WriteVideoSlot(held, slotter);
                    if (slotter.NextSlot == 0) WriteVideoSlot(held, slotter); // never an empty stream
                    break;
                }

                // Static window (no new frames): repeat the held frame once a slot is safely past.
                long due = slotter.SlotsBefore(QpcNowHns() - CaptureLatencyHns);
                for (long n = due; n > 0; n--)
                    WriteVideoSlot(held, slotter);

                if (due == 0)
                {
                    long deadline = slotter.OriginHns + slotter.SlotTimeHns(slotter.NextSlot) + CaptureLatencyHns;
                    int waitMs = (int)Math.Clamp((deadline - QpcNowHns()) / 10_000 + 1, 1, 100);
                    _frameSignal.WaitOne(waitMs);
                }
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        _recordedSeconds = slotter != null ? slotter.SlotTimeHns(slotter.NextSlot) / 10_000_000.0 : 0;
        FinishInternal(error);
    }

    /// <summary>Encode <paramref name="frame"/> as the next constant-rate output slot.</summary>
    private void WriteVideoSlot(byte[] frame, FrameSlotter slotter)
    {
        long slot = slotter.Take();
        if (ReferenceEquals(frame, _lastWrittenFrame)) RepeatedSlots++;
        _lastWrittenFrame = frame;

        var buffer = MediaFactory.MFCreateMemoryBuffer(frame.Length);
        buffer.Lock(out IntPtr dst, out _, out _);
        Marshal.Copy(frame, 0, dst, frame.Length);
        buffer.Unlock();
        buffer.CurrentLength = frame.Length;

        var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = slotter.SlotTimeHns(slot);
        sample.SampleDuration = slotter.SlotDurationHns(slot);
        lock (_writerLock)
        {
            if (!_finalized) _writer!.WriteSample(_streamIndex, sample);
        }
        sample.Dispose();
        buffer.Dispose();
    }

    /// <summary>Manual stop. Blocks until the file is finalized.</summary>
    public void Stop()
    {
        if (!IsRecording) return;
        _stop = true;
        _frameSignal.Set(); // wake the encoder if it's waiting
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

        _isRecording = false;
        LogCaptureStats(error);
        Stopped?.Invoke(new RecordingResult(error == null, _outputPath, _recordedSeconds, error));
    }

    /// <summary>Append one line of capture-smoothness stats to %APPDATA%\Capper\recordings.log, so a
    /// stuttery clip can be traced to its cause (source not producing frames vs. frames lost).</summary>
    private void LogCaptureStats(string? error)
    {
        try
        {
            double secs = Math.Max(0.001, _recordedSeconds);
            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {Path.GetFileName(_outputPath)}: {_recordedSeconds:0.0}s, " +
                          $"captured {CapturedFrames / secs:0} fps, longest gap {MaxCaptureGapMs:0} ms, " +
                          $"repeated {RepeatedSlots}/{(long)Math.Round(secs * _fps)} output frames, " +
                          $"handler avg {CaptureHandlerMs / Math.Max(1, CapturedFrames):0.0} ms" +
                          (error != null ? $", error: {error}" : "");
            Directory.CreateDirectory(AppConfig.AppDataDir);
            File.AppendAllText(Path.Combine(AppConfig.AppDataDir, "recordings.log"), line + Environment.NewLine);
        }
        catch { /* diagnostics only */ }
    }

    private static ulong Pack(uint high, uint low) => ((ulong)high << 32) | low;

    public void Dispose()
    {
        try { Stop(); } catch { }
        _frameSignal.Dispose();
    }
}
