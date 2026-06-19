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
