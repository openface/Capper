using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Capper;

internal static class ShotProgram
{
    private const uint PW_RENDERFULLCONTENT = 0x00000002;

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush(); // blocks until the next desktop composition (one per vsync)

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [STAThread]
    private static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        string outDir = args.Length > 0 ? args[0] : ".";
        string mode = args.Length > 1 ? args[1] : "ui";
        Directory.CreateDirectory(outDir);

        if (mode == "trim") { CaptureTrim(outDir, args.Length > 2 ? args[2] : null); return; }
        if (mode == "probe") { Probe(args.Length > 2 ? args[2] : null); return; }
        if (mode == "record") { RecordSample(outDir, args.Length > 2 ? int.Parse(args[2]) : 5); return; }
        if (mode == "window") { RecordWindow(outDir, args.Length > 2 ? args[2] : "", args.Length > 3 ? int.Parse(args[3]) : 5); return; }
        if (mode == "dups") { DuplicateReport(args.Length > 2 ? args[2] : null, outDir, args.Skip(3).Select(int.Parse).ToArray()); return; }
        if (mode == "smooth") { Environment.ExitCode = SmoothnessTest(outDir, args.Length > 2 ? int.Parse(args[2]) : 5); return; }

        // Settings window — default config (system audio on, so the checkbox shows checked).
        using (var f = new ConfigForm(new AppConfig()))
        {
            f.StartPosition = FormStartPosition.CenterScreen;
            f.Show();
            Pump(600);
            Save(f.Handle, Path.Combine(outDir, "settings.png"));
            f.Close();
        }

        // Recording pill.
        using (var o = new RecordingOverlay())
        {
            o.ShowRecording(TimeSpan.FromSeconds(7), "Ctrl+Alt+R");
            Pump(600);
            Save(o.Handle, Path.Combine(outDir, "recording-pill.png"));
        }
    }

    // Opens the Trim dialog on a clip and captures it. With a source clip, that clip is copied to a
    // temp staging file (the dialog moves/deletes its working file, so the original is never touched);
    // otherwise a short branded sample window is recorded so the screenshot has no private desktop.
    private static void CaptureTrim(string outDir, string? srcClip)
    {
        var cfg = new AppConfig();
        cfg.ApplyPreset(VideoPreset.QuickShare);

        string finalPath = Path.Combine(Path.GetTempPath(), "Capper-sample.mp4");
        string staging = ClipFiles.PendingPath(finalPath);
        ClipFiles.Discard(staging);
        ClipFiles.Discard(finalPath);

        if (srcClip != null)
        {
            if (!File.Exists(srcClip)) { Console.WriteLine("ERROR: clip not found: " + srcClip); return; }
            File.Copy(srcClip, staging, overwrite: true); // work on a copy; never the user's original
            Console.WriteLine($"using clip: {srcClip} ({new FileInfo(staging).Length} bytes)");
        }
        else
        {
            cfg.AudioSource = AudioSource.None;
            using var sample = new SampleForm(Path.Combine(outDir, "logo.png"));
            sample.Show();
            Pump(500);
            var item = Native.CreateItemForWindow(sample.Handle);
            if (item == null) { Console.WriteLine("ERROR: could not create capture item"); return; }

            var rec = new WindowCaptureRecorder();
            rec.Start(item, cfg, staging);
            Pump(3000);                 // ~3 seconds of clip
            try { rec.Stop(); } catch (Exception ex) { Console.WriteLine("stop: " + ex.Message); }
            Pump(300);
            rec.Dispose();
            sample.Close();
        }

        if (!File.Exists(staging)) { Console.WriteLine("ERROR: no clip produced at " + staging); return; }

        var dlg = new TrimDialog(staging, finalPath, cfg);
        dlg.Show();
        Pump(3500);                     // let ProbeAsync load the clip and render the first preview frame
        Save(dlg.Handle, Path.Combine(outDir, "trim-dialog.png"));
        dlg.Close();
        Pump(300);

        ClipFiles.Discard(staging);
        ClipFiles.Discard(finalPath);
    }

    // Record the animated sample window for a fixed number of seconds, then report the video stream's
    // duration. With correct timestamps, video end should be ~= the wall-clock record time.
    private static void RecordSample(string outDir, int seconds)
    {
        var cfg = new AppConfig();
        cfg.ApplyPreset(VideoPreset.QuickShare);
        cfg.AudioSource = AudioSource.None;
        string outPath = Path.Combine(outDir, $"record-test-{seconds}s.mp4");
        ClipFiles.Discard(outPath);

        using var sample = new SampleForm(Path.Combine(outDir, "logo.png"));
        sample.Show();
        Pump(500);
        var item = Native.CreateItemForWindow(sample.Handle);
        if (item == null) { Console.WriteLine("ERROR: could not create capture item"); return; }

        var rec = new WindowCaptureRecorder();
        var wall = System.Diagnostics.Stopwatch.StartNew();
        rec.Start(item, cfg, outPath);
        Pump(seconds * 1000);
        rec.Stop();
        wall.Stop();
        Pump(300);
        rec.Dispose();
        sample.Close();

        Console.WriteLine($"wall-clock record time: {wall.Elapsed.TotalSeconds:0.000}s");
        Probe(outPath);
    }

    // Stutter regression check. Records a window whose content moves at constant speed and repaints on
    // every vsync, decodes the clip back to RGB, and checks the motion is even: no exact repeats, and the
    // bar advances the same distance every frame. (Uneven frame sampling makes motion speed up and slow
    // down even without exact repeats, so both are checked.)
    // Exit code 0 = pass. Usage: ShotHarness <outDir> smooth [seconds]
    private static int SmoothnessTest(string outDir, int seconds)
    {
        var cfg = new AppConfig();
        cfg.ApplyPreset(VideoPreset.QuickShare); // 720p30 → 1280x800 window exercises the GPU scaler path
        cfg.AudioSource = AudioSource.None;
        string outPath = Path.Combine(outDir, $"smooth-test-{seconds}s.mp4");
        ClipFiles.Discard(outPath);

        using var form = new MotionForm();
        form.Show();
        SpinPump(500, form);
        var item = Native.CreateItemForWindow(form.Handle);
        if (item == null) { Console.WriteLine("ERROR: could not create capture item"); return 2; }

        // Measure this process's Sleep granularity while recording (it depends on the timer resolution
        // Windows grants the process, which differs between this visible harness and the tray app).
        var sleeps = new List<double>();
        var probe = new Thread(() =>
        {
            for (int i = 0; i < 30; i++)
            {
                var s = System.Diagnostics.Stopwatch.StartNew();
                Thread.Sleep(20);
                sleeps.Add(s.Elapsed.TotalMilliseconds);
            }
        }) { IsBackground = true };

        var rec = new WindowCaptureRecorder();
        rec.Start(item, cfg, outPath);
        probe.Start();
        SpinPump(seconds * 1000, form); // no Sleep: keep the form repainting every vsync
        probe.Join();
        Console.WriteLine($"Sleep(20) during recording: avg {sleeps.Average():0.0} ms, max {sleeps.Max():0.0} ms");
        rec.Stop();
        Console.WriteLine($"captured {rec.CapturedFrames / (double)seconds:0} frames/s, capture handler avg {rec.CaptureHandlerMs / Math.Max(1, rec.CapturedFrames):0.00} ms");
        SpinPump(300, form);
        rec.Dispose();
        Console.WriteLine($"source repaints: {form.Paints / (seconds + 0.8):0} per second");
        form.Close();

        // Track the sweeping bar's x position in every decoded frame.
        var barX = new List<int>();
        var (frames, dups, longestRun) = CountDuplicateFrames(outPath, null, (_, bgra, w, h) =>
            barX.Add(FindBarX(bgra, w, h)));
        double pct = frames > 0 ? 100.0 * dups / frames : 100;
        Console.WriteLine($"{outPath}: {frames} frames, {dups} exact repeats ({pct:0.0}%), longest repeat run {longestRun}");

        // The bar moves at constant speed, so in a smooth clip it advances the same distance every frame.
        // Uneven sampling shows up as uneven steps even when no frame is an exact repeat. (Skip steps
        // where the bar wrapped around or wasn't found.)
        var steps = barX.Zip(barX.Skip(1), (a, b) => (a, b))
            .Where(p => p.a >= 0 && p.b >= 0 && p.b >= p.a).Select(p => p.b - p.a).ToList();
        if (steps.Count < 10) { Console.WriteLine("SMOOTHNESS FAIL (bar not tracked)"); return 1; }
        double median = steps.OrderBy(s => s).ElementAt(steps.Count / 2);
        int uneven = steps.Count(s => Math.Abs(s - median) > median * 0.25);
        double unevenPct = 100.0 * uneven / steps.Count;
        var hist = steps.GroupBy(s => s).OrderBy(g => g.Key).Select(g => $"{g.Key}px x{g.Count()}");
        Console.WriteLine($"bar step per frame: median {median}px, {uneven}/{steps.Count} steps off by >25% ({unevenPct:0.0}%)");
        Console.WriteLine("  step histogram: " + string.Join(", ", hist));

        // A smooth capture has essentially no repeats and even steps. Some unevenness is unavoidable:
        // 30 fps on a 144 Hz screen alternates 4- and 5-vsync steps, and the source window itself
        // occasionally misses a vsync. Capped at 48 Hz capture (the old default) ~40% of steps are off.
        bool pass = frames >= seconds * cfg.Fps * 9 / 10 && pct <= 2.0 && unevenPct <= 15.0;
        Console.WriteLine(pass ? "SMOOTHNESS PASS" : "SMOOTHNESS FAIL");
        return pass ? 0 : 1;
    }

    /// <summary>Left edge of MotionForm's red bar along a row near the top, or -1 if not found.</summary>
    private static int FindBarX(byte[] bgra, int w, int h)
    {
        int stride = bgra.Length / h;
        int row = h / 14;
        for (int x = 0; x < w; x++)
        {
            int i = row * stride + x * 4;
            if (bgra[i + 2] > 150 && bgra[i + 1] < 130 && bgra[i] < 130) return x; // B,G,R order
        }
        return -1;
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    private const uint SWP_NOMOVE = 0x2, SWP_NOSIZE = 0x1, SWP_NOACTIVATE = 0x10;
    private static readonly IntPtr HWND_TOPMOST = new(-1), HWND_NOTOPMOST = new(-2);

    // Record an existing top-level window (matched by title substring) with no input sent to it, then
    // report capture rate and exact-repeat frames. Usage: ShotHarness <outDir> window "<title>" [seconds]
    private static void RecordWindow(string outDir, string title, int seconds)
    {
        var proc = System.Diagnostics.Process.GetProcesses()
            .FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero &&
                                 p.MainWindowTitle.Contains(title, StringComparison.OrdinalIgnoreCase));
        if (proc == null) { Console.WriteLine($"ERROR: no window titled *{title}*"); return; }
        IntPtr hwnd = proc.MainWindowHandle;
        // A fully covered Chrome window stops rendering, so float it on top for the recording (topmost
        // works without focus, which Windows won't hand to a background process).
        SetForegroundWindow(hwnd);
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        Thread.Sleep(800);

        var cfg = new AppConfig();
        cfg.ApplyPreset(VideoPreset.QuickShare);
        cfg.AudioSource = AudioSource.None;
        string outPath = Path.Combine(outDir, $"window-test-{seconds}s.mp4");
        ClipFiles.Discard(outPath);

        var item = Native.CreateItemForWindow(hwnd);
        if (item == null) { Console.WriteLine("ERROR: could not create capture item"); return; }
        var rec = new WindowCaptureRecorder();
        rec.Start(item, cfg, outPath);
        Pump(seconds * 1000);
        rec.Stop();
        SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        Console.WriteLine($"recorded \"{proc.MainWindowTitle}\": captured {rec.CapturedFrames / (double)seconds:0} frames/s, " +
                          $"handler avg {rec.CaptureHandlerMs / Math.Max(1, rec.CapturedFrames):0.00} ms, " +
                          $"longest capture gap {rec.MaxCaptureGapMs:0} ms, repeated output frames {rec.RepeatedSlots}");
        rec.Dispose();

        var map = new System.Text.StringBuilder();
        var (frames, dups, longest) = CountDuplicateFrames(outPath, map);
        for (int i = 0; i < map.Length; i += 90) Console.WriteLine(map.ToString(i, Math.Min(90, map.Length - i)));
        Console.WriteLine($"{frames} frames, {dups} exact repeats, longest run {longest}");
    }

    /// <summary>Print a per-frame map of a clip: '#' = new picture, '.' = exact repeat of the previous.
    /// Isolated repeats inside motion ("##.##.#") are pacing judder; long runs are static content.</summary>
    /// Optional frame indices are also saved as PNGs to <paramref name="outDir"/> for inspection.
    private static void DuplicateReport(string? clip, string outDir, int[] dumpFrames)
    {
        if (clip == null || !File.Exists(clip)) { Console.WriteLine("usage: dups <clip.mp4> [frameIndex...]"); return; }
        var map = new System.Text.StringBuilder();
        var motion = new System.Text.StringBuilder();
        byte[]? prevFrame = null;
        var strip = new List<(int Idx, byte[] Px)>();
        (int W, int H) stripSize = (0, 0);
        var (frames, dups, longest) = CountDuplicateFrames(clip, map, (idx, bgra, w, h) =>
        {
            // How much changed vs the previous frame: pixel count and bounding box (noise-thresholded).
            if (prevFrame != null)
            {
                // Mean absolute difference per pixel (whole frame), excluding the top 80 rows (browser UI).
                int stride = bgra.Length / h; long sad = 0, n = 0;
                for (int y = Math.Min(80, h / 8); y < h; y += 2)
                    for (int x = 0; x < w; x += 2)
                    {
                        int i = y * stride + x * 4;
                        sad += Math.Abs(bgra[i] - prevFrame[i]) + Math.Abs(bgra[i + 1] - prevFrame[i + 1]) + Math.Abs(bgra[i + 2] - prevFrame[i + 2]);
                        n++;
                    }
                motion.AppendLine($"{idx} {sad / (double)n:0.000}");
            }
            prevFrame = (byte[])bgra.Clone();

            if (!dumpFrames.Contains(idx)) return;
            // CROP=x,y,w,h env var: collect that region of each dumped frame into one side-by-side strip.
            var crop = Environment.GetEnvironmentVariable("CROP")?.Split(',').Select(int.Parse).ToArray();
            if (crop is { Length: 4 })
            {
                int cs = bgra.Length / h;
                var tile = new byte[crop[2] * crop[3] * 4];
                for (int y = 0; y < crop[3]; y++)
                    Buffer.BlockCopy(bgra, (crop[1] + y) * cs + crop[0] * 4, tile, y * crop[2] * 4, crop[2] * 4);
                strip.Add((idx, tile));
                stripSize = (crop[2], crop[3]);
                return;
            }
            using var bmp = new Bitmap(w, h, PixelFormat.Format32bppRgb);
            var bits = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            int srcStride = bgra.Length / h; // decoder rows may be padded beyond w * 4
            for (int y = 0; y < h; y++) Marshal.Copy(bgra, y * srcStride, bits.Scan0 + y * bits.Stride, Math.Min(w * 4, srcStride));
            bmp.UnlockBits(bits);
            string png = Path.Combine(outDir, $"frame-{idx:000}.png");
            bmp.Save(png, ImageFormat.Png);
            Console.WriteLine("wrote " + png);
        });
        if (strip.Count > 0)
        {
            var (tw, th) = stripSize;
            using var sheet = new Bitmap(tw * strip.Count, th + 18, PixelFormat.Format32bppRgb);
            using (var g = Graphics.FromImage(sheet)) g.Clear(Color.Black);
            for (int k = 0; k < strip.Count; k++)
            {
                var bits = sheet.LockBits(new Rectangle(k * tw, 18, tw, th), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                for (int y = 0; y < th; y++) Marshal.Copy(strip[k].Px, y * tw * 4, bits.Scan0 + y * bits.Stride, tw * 4);
                sheet.UnlockBits(bits);
                using var g = Graphics.FromImage(sheet);
                g.DrawString(strip[k].Idx.ToString(), SystemFonts.DefaultFont, Brushes.White, k * tw + 2, 2);
            }
            string sheetPath = Path.Combine(outDir, "strip.png");
            sheet.Save(sheetPath, ImageFormat.Png);
            Console.WriteLine("wrote " + sheetPath);
        }
        Console.Write(motion);
        for (int i = 0; i < map.Length; i += 90) Console.WriteLine(map.ToString(i, Math.Min(90, map.Length - i)));
        Console.WriteLine($"{frames} frames, {dups} exact repeats, longest run {longest}");
    }

    /// <summary>Decode every video frame to RGB32 and count frames identical to their predecessor.</summary>
    private static (int Frames, int Duplicates, int LongestRun) CountDuplicateFrames(string clip,
        System.Text.StringBuilder? map = null, Action<int, byte[], int, int>? onFrame = null)
    {
        Vortice.MediaFoundation.MediaFactory.MFStartup(false).CheckError();
        try
        {
            using var attrs = Vortice.MediaFoundation.MediaFactory.MFCreateAttributes(1);
            attrs.Set(Vortice.MediaFoundation.SourceReaderAttributeKeys.EnableVideoProcessing, 1u);
            using var reader = Vortice.MediaFoundation.MediaFactory.MFCreateSourceReaderFromURL(clip, attrs);
            using (var rgb = Vortice.MediaFoundation.MediaFactory.MFCreateMediaType())
            {
                rgb.Set(Vortice.MediaFoundation.MediaTypeAttributeKeys.MajorType, Vortice.MediaFoundation.MediaTypeGuids.Video);
                rgb.Set(Vortice.MediaFoundation.MediaTypeAttributeKeys.Subtype, Vortice.MediaFoundation.VideoFormatGuids.Rgb32);
                reader.SetCurrentMediaType(Vortice.MediaFoundation.SourceReaderIndex.FirstVideoStream, rgb);
            }
            int fw, fh;
            using (var cur = reader.GetCurrentMediaType(Vortice.MediaFoundation.SourceReaderIndex.FirstVideoStream))
            {
                ulong packed = cur.GetUInt64(Vortice.MediaFoundation.MediaTypeAttributeKeys.FrameSize);
                fw = (int)(packed >> 32); fh = (int)(packed & 0xFFFFFFFF);
            }

            byte[]? prev = null;
            int frames = 0, dups = 0, run = 0, longest = 0;
            while (true)
            {
                var sample = reader.ReadSample(Vortice.MediaFoundation.SourceReaderIndex.FirstVideoStream,
                    Vortice.MediaFoundation.SourceReaderControlFlag.None, out _, out var flags, out _);
                if ((flags & Vortice.MediaFoundation.SourceReaderFlag.EndOfStream) != 0) { sample?.Dispose(); break; }
                if (sample == null) continue;
                using (sample)
                using (var buf = sample.ConvertToContiguousBuffer())
                {
                    buf.Lock(out IntPtr p, out _, out int len);
                    var cur = new byte[len];
                    Marshal.Copy(p, cur, 0, len);
                    buf.Unlock();
                    if (prev != null && cur.AsSpan().SequenceEqual(prev))
                    {
                        dups++; run++; longest = Math.Max(longest, run);
                        map?.Append('.');
                    }
                    else { run = 0; map?.Append('#'); }
                    onFrame?.Invoke(frames, cur, fw, fh);
                    prev = cur;
                    frames++;
                }
            }
            return (frames, dups, longest);
        }
        finally { Vortice.MediaFoundation.MediaFactory.MFShutdown(); }
    }

    /// <summary>Pump messages without sleeping, repainting <paramref name="form"/> once per desktop
    /// composition (DwmFlush) so its content changes on every vsync. (Invalidating from inside OnPaint would keep a WM_PAINT always
    /// queued, and DoEvents would never return.)</summary>
    private static void SpinPump(int ms, Form? form = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (form != null && !form.IsDisposed) { form.Invalidate(); form.Update(); DwmFlush(); }
            Application.DoEvents();
        }
    }

    /// <summary>Redraws continuously; content is a function of real time, so every composed frame differs.</summary>
    private sealed class MotionForm : Form
    {
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        public int Paints;

        public MotionForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1280, 800);
            DoubleBuffered = true;
            BackColor = Color.Black;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Paints++;
            double t = _clock.Elapsed.TotalSeconds;
            var g = e.Graphics;
            g.Clear(Color.FromArgb(20, 22, 28));
            // A bar sweeping across at constant speed (kept cheap to draw so the window never misses a
            // vsync, which would show up as uneven motion that isn't the recorder's fault).
            int x = (int)(t * 600 % (ClientSize.Width - 100));
            using (var bar = new SolidBrush(Color.FromArgb(232, 72, 72)))
                g.FillRectangle(bar, x, 0, 100, ClientSize.Height);
        }
    }

    // Read each stream to its end via Media Foundation and report the last presentation time, to check
    // whether the audio track ends meaningfully before/after the video (which makes the frame-server
    // preview lose its clock and "rush" the tail of the clip).
    private static void Probe(string? clip)
    {
        if (clip == null || !File.Exists(clip)) { Console.WriteLine("usage: probe <clip.mp4>"); return; }
        Vortice.MediaFoundation.MediaFactory.MFStartup(false).CheckError();
        try
        {
            var reader = Vortice.MediaFoundation.MediaFactory.MFCreateSourceReaderFromURL(clip, null);
            ProbeStream(reader, Vortice.MediaFoundation.SourceReaderIndex.FirstVideoStream, "video");
            ProbeStream(reader, Vortice.MediaFoundation.SourceReaderIndex.FirstAudioStream, "audio");
            reader.Dispose();
        }
        finally { Vortice.MediaFoundation.MediaFactory.MFShutdown(); }
    }

    private static void ProbeStream(Vortice.MediaFoundation.IMFSourceReader reader,
        Vortice.MediaFoundation.SourceReaderIndex stream, string label)
    {
        long lastEndHns = -1, firstHns = -1, count = 0;
        while (true)
        {
            Vortice.MediaFoundation.IMFSample? sample;
            Vortice.MediaFoundation.SourceReaderFlag flags;
            long ts;
            try
            {
                sample = reader.ReadSample(stream, Vortice.MediaFoundation.SourceReaderControlFlag.None,
                    out _, out flags, out ts);
            }
            catch (SharpGen.Runtime.SharpGenException) { Console.WriteLine($"{label}: (no such stream)"); return; }
            if ((flags & Vortice.MediaFoundation.SourceReaderFlag.EndOfStream) != 0) { sample?.Dispose(); break; }
            if (sample == null) continue;
            long dur = 0; try { dur = sample.SampleDuration; } catch { }
            if (firstHns < 0) firstHns = ts;
            lastEndHns = ts + dur;
            count++;
            sample.Dispose();
        }
        if (count == 0) { Console.WriteLine($"{label}: (no samples)"); return; }
        Console.WriteLine($"{label}: {count} samples, first={firstHns / 10_000_000.0:0.000}s, end={lastEndHns / 10_000_000.0:0.000}s");
    }

    private static void Save(IntPtr hwnd, string path)
    {
        GetWindowRect(hwnd, out var r);
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        using var bmp = new Bitmap(w, h);
        using (var g = Graphics.FromImage(bmp))
        {
            IntPtr hdc = g.GetHdc();
            PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT);
            g.ReleaseHdc(hdc);
        }
        bmp.Save(path, ImageFormat.Png);
        Console.WriteLine("wrote " + path + $" ({w}x{h})");
    }

    private static void Pump(int ms)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) { Application.DoEvents(); Thread.Sleep(20); }
    }

    /// <summary>A small branded 16:9 window with gentle motion, recorded as the sample clip so the
    /// Trim dialog has a real, non-private video to preview.</summary>
    private sealed class SampleForm : Form
    {
        private readonly System.Windows.Forms.Timer _t = new() { Interval = 33 };
        private readonly Image? _logo;
        private int _frame;

        public SampleForm(string? logoPath)
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(960, 540);
            DoubleBuffered = true;
            BackColor = Color.FromArgb(18, 19, 22);
            if (logoPath != null && File.Exists(logoPath))
            {
                try { _logo = Image.FromFile(logoPath); } catch { }
            }
            _t.Tick += (_, _) => { _frame++; Invalidate(); };
            _t.Start();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var bg = new LinearGradientBrush(ClientRectangle,
                Color.FromArgb(34, 37, 52), Color.FromArgb(16, 17, 20), 55f))
                g.FillRectangle(bg, ClientRectangle);

            // Gentle left-right accent bar so frames differ (nice when scrubbing the preview).
            int x = (int)((Math.Sin(_frame * 0.05) * 0.5 + 0.5) * (Width - 240)) + 20;
            using (var bar = new SolidBrush(Color.FromArgb(232, 72, 72)))
                g.FillRectangle(bar, x, Height / 2 + 140, 200, 10);

            if (_logo != null)
                g.DrawImage(_logo, Width / 2 - 64, Height / 2 - 150, 128, 128);

            using var title = new Font("Segoe UI Semibold", 30f);
            using var sub = new Font("Segoe UI", 14f);
            TextRenderer.DrawText(g, "Capper", title, new Rectangle(0, Height / 2 + 4, Width, 50),
                Color.FromArgb(238, 239, 242), TextFormatFlags.HorizontalCenter);
            TextRenderer.DrawText(g, "sample clip", sub, new Rectangle(0, Height / 2 + 60, Width, 26),
                Color.FromArgb(150, 153, 160), TextFormatFlags.HorizontalCenter);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _t.Dispose(); _logo?.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
