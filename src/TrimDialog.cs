using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using Windows.Media.Editing;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Capper;

/// <summary>
/// Sleek dark "Trim &amp; Save" dialog with a scrubbable video preview and green/red trim handles.
/// The user trims to the length they want; the selected range is re-encoded (VBR, preserving the
/// recorded quality) via <see cref="TrimEngine"/>. Frame preview uses MediaComposition thumbnails.
/// </summary>
internal sealed class TrimDialog : Form
{
    private static readonly Color Bg = Color.FromArgb(28, 29, 33);
    private static readonly Color HeaderColor = Color.FromArgb(36, 38, 44);
    private static readonly Color Fg = Color.FromArgb(238, 239, 242);
    private static readonly Color Muted = Color.FromArgb(150, 153, 160);
    private static readonly Color Accent = Color.FromArgb(232, 72, 72);
    private static readonly Color AccentHover = Color.FromArgb(244, 96, 96);
    private static readonly Color Chip = Color.FromArgb(52, 54, 61);

    private readonly string _path;       // staging file the recorder wrote to
    private readonly string _finalPath;  // where the kept clip lands in the output folder
    private readonly AppConfig _cfg;

    private enum Outcome { KeepFull, Saved, Discarded }
    private Outcome _outcome = Outcome.KeepFull;

    private readonly Label _title = new();
    private readonly PictureBox _preview = new();
    private readonly TrimTimeline _timeline = new();
    private readonly Button _prev = new();
    private readonly Button _play = new();
    private readonly Button _next = new();
    private readonly Label _info = new();
    private readonly ProgressBar _progress = new();
    private readonly Button _save = new();
    private readonly Button _discard = new();
    private readonly Button _cancel = new();
    private System.Threading.CancellationTokenSource? _trimCts;

    private MediaComposition? _comp;
    private VideoPreviewPlayer? _video; // real A/V playback; thumbnails are used for paused scrubbing
    private double _duration;
    private long _sourceVideoBps;
    private int _thumbW = 480, _thumbH = 270;

    private TimeSpan? _pendingPreview;
    private bool _previewBusy;

    private readonly System.Windows.Forms.Timer _playTimer = new() { Interval = 66 };
    private readonly System.Diagnostics.Stopwatch _playClock = new();
    private double _playFrom;
    private bool _playing;
    private bool _busy;

    private Point _dragOrigin;
    private bool _dragging;

    public TrimDialog(string stagingPath, string finalPath, AppConfig cfg)
    {
        _path = stagingPath;
        _finalPath = finalPath;
        _cfg = cfg;

        Text = "Trim & Save — Capper";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Bg;
        Font = new Font("Segoe UI", 9f);
        ClientSize = new Size(620, 600);

        BuildLayout();
        _playTimer.Tick += (_, _) => PlayTick();
        Load += async (_, _) => await ProbeAsync();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Region = new Region(Rounded(new Rectangle(0, 0, Width, Height), 14));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var hb = new SolidBrush(HeaderColor);
        e.Graphics.FillRectangle(hb, 0, 0, Width, 48);
        using var border = new Pen(Color.FromArgb(60, 62, 70));
        e.Graphics.DrawPath(border, Rounded(new Rectangle(0, 0, Width - 1, Height - 1), 14));
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
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

    private void BuildLayout()
    {
        int pad = 18, w = ClientSize.Width - pad * 2;

        // Header: title on the left, close on the right. The title doubles as the drag area.
        _title.SetBounds(pad, 14, w - 40, 22);
        _title.ForeColor = Fg; _title.BackColor = Color.Transparent;
        _title.Font = new Font("Segoe UI Semibold", 10f);
        _title.Text = "Trim & Save";
        _title.MouseDown += Header_MouseDown; _title.MouseMove += Header_MouseMove; _title.MouseUp += Header_MouseUp;
        var close = new LinkLabel { Text = "✕", AutoSize = true, LinkColor = Muted, ActiveLinkColor = Fg, LinkBehavior = LinkBehavior.NeverUnderline, BackColor = Color.Transparent, Cursor = Cursors.Hand, Font = new Font("Segoe UI", 11f) };
        close.Location = new Point(ClientSize.Width - close.PreferredWidth - 14, 12);
        close.Click += (_, _) => Close(); // keeps the full recording (promoted to the output folder)
        Controls.Add(_title); Controls.Add(close);

        int previewH = (int)(w * 9.0 / 16.0);
        _preview.SetBounds(pad, 58, w, previewH);
        _preview.BackColor = Color.Black;
        _preview.SizeMode = PictureBoxSizeMode.Zoom;
        Controls.Add(_preview);

        int y = 58 + previewH + 14;

        _timeline.SetBounds(pad, y, w, 46);
        _timeline.Enabled = false;
        _timeline.StartChanged += (_, _) => { Seek(_timeline.Start); UpdateReadouts(); };
        _timeline.EndChanged += (_, _) => { Seek(_timeline.End); UpdateReadouts(); };
        _timeline.PlayheadChanged += (_, _) => { StopPlay(); RequestFrame(Sec(_timeline.Playhead)); UpdateReadouts(); };
        Controls.Add(_timeline);
        y += 54;

        // Transport directly under the timeline (the handles show the in/out points visually,
        // and the playhead clock below shows their exact time as you drag).
        StyleTransport(_prev, "◀ǀ"); _prev.Click += (_, _) => StepFrame(-1);
        StyleTransport(_play, "▶"); _play.Click += (_, _) => TogglePlay();
        StyleTransport(_next, "ǀ▶"); _next.Click += (_, _) => StepFrame(+1);
        int tx = (ClientSize.Width - (40 * 3 + 16)) / 2;
        _prev.Location = new Point(tx, y);
        _play.Location = new Point(tx + 48, y);
        _next.Location = new Point(tx + 96, y);
        Controls.Add(_prev); Controls.Add(_play); Controls.Add(_next);
        y += 42;

        // One tidy line: playback clock · selection length · estimated size.
        _info.SetBounds(pad, y, w, 20);
        _info.ForeColor = Fg; _info.BackColor = Color.Transparent;
        _info.TextAlign = ContentAlignment.MiddleCenter;
        Controls.Add(_info);
        y += 28;

        _progress.SetBounds(pad, y, w, 6);
        _progress.Style = ProgressBarStyle.Continuous; _progress.Visible = false;
        Controls.Add(_progress);
        y += 16;

        StyleButton(_save, "Save", Accent, AccentHover, Color.White);
        _save.SetBounds(ClientSize.Width - pad - 110, y, 110, 34);
        _save.Click += async (_, _) => await SaveAsync();
        StyleButton(_discard, "Discard", Chip, Color.FromArgb(74, 50, 52), Color.FromArgb(230, 130, 130));
        _discard.SetBounds(ClientSize.Width - pad - 110 - 104, y, 96, 34);
        _discard.Click += (_, _) => DiscardAndClose();
        Controls.Add(_save); Controls.Add(_discard);

        // Shown in place of Save/Discard while an export is running.
        StyleButton(_cancel, "Cancel", Chip, Color.FromArgb(62, 64, 72), Fg);
        _cancel.SetBounds(ClientSize.Width - pad - 110, y, 110, 34);
        _cancel.Visible = false;
        _cancel.Click += (_, _) => { _cancel.Enabled = false; _trimCts?.Cancel(); };
        Controls.Add(_cancel);
        _cancel.BringToFront();

        ClientSize = new Size(ClientSize.Width, y + 34 + pad);
    }

    private async Task ProbeAsync()
    {
        try
        {
            var inFile = await StorageFile.GetFileFromPathAsync(_path);
            var clip = await MediaClip.CreateFromFileAsync(inFile);
            var vp = clip.GetVideoEncodingProperties();
            _duration = clip.OriginalDuration.TotalSeconds;
            _sourceVideoBps = (long)vp.Bitrate;
            if (vp.Width > 0 && vp.Height > 0)
            {
                _thumbW = 480;
                _thumbH = Math.Max(2, (int)Math.Round(480.0 * vp.Height / vp.Width));
            }
            _comp = new MediaComposition();
            _comp.Clips.Add(clip);
            CreateVideoPlayer();

            _title.Text = "Trim & Save";

            _timeline.Duration = _duration;
            _timeline.Start = 0;
            _timeline.End = _duration;
            _timeline.Playhead = 0;
            _timeline.Enabled = true;

            UpdateReadouts();
            RequestFrame(TimeSpan.Zero);
        }
        catch (Exception ex)
        {
            _title.Text = "Couldn't open clip";
            _info.Text = ex.Message;
            _save.Enabled = false;
        }
    }

    private async void RequestFrame(TimeSpan t)
    {
        if (_comp == null) return;
        _pendingPreview = t;
        if (_previewBusy) return;
        _previewBusy = true;
        try
        {
            while (_pendingPreview is TimeSpan want)
            {
                _pendingPreview = null;
                try
                {
                    IRandomAccessStream ras = await _comp.GetThumbnailAsync(want, _thumbW, _thumbH, VideoFramePrecision.NearestFrame);
                    var bmp = await ToBitmap(ras);
                    if (!IsDisposed) { var old = _preview.Image; _preview.Image = bmp; old?.Dispose(); }
                    else bmp.Dispose();
                }
                catch { /* a frame fetch can fail near the very end; ignore */ }
            }
        }
        finally { _previewBusy = false; }
    }

    private static async Task<Bitmap> ToBitmap(IRandomAccessStream ras)
    {
        using var net = ras.AsStreamForRead();
        using var ms = new MemoryStream();
        await net.CopyToAsync(ms);
        ms.Position = 0;
        using var img = Image.FromStream(ms);
        return new Bitmap(img);
    }

    // --- Real A/V playback (MediaPlayer frame-server) ---

    private void CreateVideoPlayer()
    {
        try
        {
            _video = new VideoPreviewPlayer(_path, _thumbW, _thumbH);
            _video.FrameReady += OnVideoFrame;
            _video.Ended += () => { try { BeginInvoke(StopPlay); } catch { } };
        }
        catch
        {
            _video = null; // no audio/video player; the thumbnail scrubber still works
        }
    }

    /// <summary>Background-thread callback with a BGRA frame: turn it into a bitmap and show it.</summary>
    private void OnVideoFrame(byte[] bgra, int w, int h)
    {
        if (!_playing) return;
        Bitmap bmp;
        try
        {
            bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var bd = bmp.LockBits(new Rectangle(0, 0, w, h),
                System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                int stride = w * 4;
                for (int row = 0; row < h; row++)
                    System.Runtime.InteropServices.Marshal.Copy(bgra, row * stride, IntPtr.Add(bd.Scan0, row * bd.Stride), stride);
            }
            finally { bmp.UnlockBits(bd); }
        }
        catch { return; }

        try
        {
            BeginInvoke(() =>
            {
                if (IsDisposed || !_playing) { bmp.Dispose(); return; }
                var old = _preview.Image; _preview.Image = bmp; old?.Dispose();
            });
        }
        catch { bmp.Dispose(); } // handle not created / form closing
    }

    // --- Playback ---

    private void TogglePlay() { if (_playing) StopPlay(); else StartPlay(); }

    private void StartPlay()
    {
        if (_comp == null) return;
        double from = _timeline.Playhead;
        if (from >= _timeline.End - 0.05) from = _timeline.Start;
        _playFrom = from;
        _timeline.Playhead = from;
        _playing = true;
        _play.Text = "❙❙";

        if (_video != null)
        {
            _video.Position = Sec(from); // real audio + video from here
            _video.Play();
        }
        else
        {
            _playClock.Restart(); // fallback: thumbnail-driven, silent
        }
        _playTimer.Start();
    }

    private void StopPlay()
    {
        if (!_playing) return;
        _playTimer.Stop();
        _playClock.Stop();
        _video?.Pause();
        _playing = false;
        _play.Text = "▶";
    }

    private void PlayTick()
    {
        double t = _video != null ? _video.Position.TotalSeconds : _playFrom + _playClock.Elapsed.TotalSeconds;
        if (t >= _timeline.End)
        {
            _timeline.Playhead = _timeline.End;
            StopPlay();
            RequestFrame(Sec(_timeline.End)); // settle on a crisp frame at the out point
            UpdateReadouts();
            return;
        }
        _timeline.Playhead = t;
        // The frame-server paints the picture itself; only fetch thumbnails when there's no player
        // (or the GPU frame copy isn't working), so playback still shows motion.
        if (_video == null || !_video.FrameServerOk) RequestFrame(Sec(t));
        UpdateReadouts();
    }

    private void StepFrame(int dir)
    {
        StopPlay();
        double step = 1.0 / Math.Max(1, _cfg.Fps);
        double t = Math.Clamp(_timeline.Playhead + dir * step, 0, _duration);
        _timeline.Playhead = t;
        RequestFrame(Sec(t));
        UpdateReadouts();
    }

    private void Seek(double t)
    {
        StopPlay();
        _timeline.Playhead = t;
        RequestFrame(Sec(t));
    }

    private static TimeSpan Sec(double s) => TimeSpan.FromSeconds(Math.Max(0, s));

    // --- Readouts ---

    private int AudioBps => _cfg.AudioSource == AudioSource.System ? _cfg.AudioBitrateKbps * 1000 : 0;
    private double SelectedDuration => Math.Max(0.1, _timeline.End - _timeline.Start);
    private int ExportVideoBps => (int)(_sourceVideoBps > 0 ? _sourceVideoBps : _cfg.VideoBitrateKbps * 1000);

    private void UpdateReadouts()
    {
        double approxMb = (ExportVideoBps + AudioBps) * SelectedDuration / 8_000_000.0;
        _info.Text = $"{FormatTime(_timeline.Playhead)} / {FormatTime(_duration)}     ·     "
                   + $"{SelectedDuration:0.0}s     ·     ~{approxMb:0.0} MB";
    }

    // --- Save / Discard ---

    private async Task SaveAsync()
    {
        if (_busy || _comp == null) return;
        StopPlay();
        _busy = true;
        SetEnabled(false);
        _trimCts = new System.Threading.CancellationTokenSource();
        _save.Visible = false; _discard.Visible = false;
        _cancel.Visible = true; _cancel.Enabled = true; _cancel.BringToFront();
        _progress.Visible = true; _progress.Value = 0;

        Directory.CreateDirectory(Path.GetDirectoryName(_finalPath)!);
        // Trim into a ".trimming.mp4" temp in the output folder, then promote to the final name.
        string trimTemp = ClipFiles.TrimmingPath(_finalPath);
        var start = Sec(_timeline.Start);
        var end = Sec(_timeline.End);
        var progress = new Progress<double>(p => { if (!IsDisposed) _progress.Value = (int)Math.Clamp(p, 0, 100); });

        try
        {
            ReleaseSource(); // drop our handle on the staging file before re-encoding it
            await TrimEngine.TrimAsync(_path, trimTemp, start, end, ExportVideoBps, AudioBps, progress, _trimCts.Token);
            FastStart.Process(trimTemp); // moov-first so the clip streams progressively
            ClipFiles.Promote(trimTemp, _finalPath);
            ClipFiles.Discard(_path); // remove the untrimmed staging recording
            _outcome = Outcome.Saved;
            Close();
        }
        catch (OperationCanceledException)
        {
            // The ".pending" staging clip is untouched, so the user can re-trim or keep it.
            ClipFiles.Discard(trimTemp);
            RestoreAfterTrim();
        }
        catch (Exception ex)
        {
            ClipFiles.Discard(trimTemp);
            MessageBox.Show(this, "Trim failed:\n" + ex.Message, "Capper", MessageBoxButtons.OK, MessageBoxIcon.Error);
            RestoreAfterTrim();
        }
        finally
        {
            _trimCts?.Dispose();
            _trimCts = null;
        }
    }

    /// <summary>Return the dialog to its editable state after a cancelled/failed export and
    /// reopen the preview composition (released before the trim).</summary>
    private void RestoreAfterTrim()
    {
        if (IsDisposed) return;
        _busy = false;
        _cancel.Visible = false;
        _save.Visible = true; _discard.Visible = true;
        _progress.Visible = false;
        SetEnabled(true);
        _ = ReopenAsync();
    }

    private async Task ReopenAsync()
    {
        try
        {
            var inFile = await StorageFile.GetFileFromPathAsync(_path);
            var clip = await MediaClip.CreateFromFileAsync(inFile);
            _comp = new MediaComposition();
            _comp.Clips.Add(clip);
            CreateVideoPlayer();
            RequestFrame(Sec(_timeline.Playhead));
        }
        catch { /* preview won't refresh, but Save/Discard still work */ }
    }

    private void DiscardAndClose()
    {
        StopPlay();
        ReleaseSource();
        ClipFiles.Discard(_path); // never reaches the output folder
        _outcome = Outcome.Discarded;
        Close();
    }

    /// <summary>Release the preview composition's handle on the staging file so it can be moved/deleted.</summary>
    private void ReleaseSource()
    {
        StopPlay();
        _video?.Dispose();
        _video = null;
        _comp = null;
        _preview.Image?.Dispose();
        _preview.Image = null;
        GC.Collect(); GC.WaitForPendingFinalizers();
    }

    private void SetEnabled(bool on)
    {
        _timeline.Enabled = on; _save.Enabled = on; _discard.Enabled = on;
        _prev.Enabled = on; _play.Enabled = on; _next.Enabled = on;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _playTimer.Stop();
        // ✕ / Alt-F4 keeps the full, untrimmed clip — promote it from staging to the output folder.
        if (_outcome == Outcome.KeepFull)
        {
            ReleaseSource();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_finalPath)!);
                if (File.Exists(_path)) { ClipFiles.Promote(_path, _finalPath); FastStart.Process(_finalPath); }
            }
            catch { /* leave the staging file in place if promotion fails */ }
        }
        _video?.Dispose(); // safety net for the Saved/Discarded paths
        _video = null;
        _preview.Image?.Dispose();
        base.OnFormClosed(e);
    }

    // --- Header drag ---
    private void Header_MouseDown(object? s, MouseEventArgs e) { _dragging = true; _dragOrigin = e.Location; }
    private void Header_MouseUp(object? s, MouseEventArgs e) => _dragging = false;
    private void Header_MouseMove(object? s, MouseEventArgs e)
    {
        if (!_dragging) return;
        Location = new Point(Location.X + e.X - _dragOrigin.X, Location.Y + e.Y - _dragOrigin.Y);
    }

    // --- formatting + factories ---

    private static string FormatTime(double seconds)
    {
        if (seconds < 0) seconds = 0;
        var ts = TimeSpan.FromSeconds(seconds);
        return $"{(int)ts.TotalMinutes}:{ts.Seconds:00}.{ts.Milliseconds:000}";
    }

    private void StyleButton(Button b, string text, Color back, Color hover, Color fore)
    {
        b.Text = text; b.FlatStyle = FlatStyle.Flat; b.BackColor = back; b.ForeColor = fore;
        b.Cursor = Cursors.Hand; b.TabStop = false; b.Font = new Font("Segoe UI Semibold", 9.5f);
        b.FlatAppearance.BorderSize = 0; b.FlatAppearance.MouseOverBackColor = hover; b.FlatAppearance.MouseDownBackColor = hover;
    }

    private void StyleTransport(Button b, string glyph)
    {
        b.Text = glyph; b.FlatStyle = FlatStyle.Flat; b.BackColor = Chip; b.ForeColor = Fg;
        b.Size = new Size(40, 30); b.Cursor = Cursors.Hand; b.TabStop = false; b.Font = new Font("Segoe UI", 9f);
        b.FlatAppearance.BorderSize = 0; b.FlatAppearance.MouseOverBackColor = Color.FromArgb(62, 64, 72);
    }

    // --- Timeline control: track, selected region, green start + red end handles, playhead ---
    private sealed class TrimTimeline : Control
    {
        private double _duration = 1, _start, _end = 1, _playhead;
        private int _drag; // 0 none, 1 start, 2 end, 3 playhead

        public event EventHandler? StartChanged;
        public event EventHandler? EndChanged;
        public event EventHandler? PlayheadChanged;

        public TrimTimeline() { DoubleBuffered = true; }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public double Duration { get => _duration; set { _duration = Math.Max(0.1, value); Invalidate(); } }
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public double Start { get => _start; set { _start = Clamp(value); Invalidate(); } }
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public double End { get => _end; set { _end = Clamp(value); Invalidate(); } }
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public double Playhead { get => _playhead; set { _playhead = Clamp(value); Invalidate(); } }

        private double Clamp(double v) => Math.Clamp(v, 0, _duration);
        private int Track => Width - 24;
        private int XOf(double t) => 12 + (int)(t / _duration * Track);
        private double TOf(int x) => Clamp((x - 12) / (double)Track * _duration);

        protected override void OnMouseDown(MouseEventArgs e)
        {
            int xs = XOf(_start), xe = XOf(_end);
            if (Math.Abs(e.X - xs) <= 9) _drag = 1;
            else if (Math.Abs(e.X - xe) <= 9) _drag = 2;
            else _drag = 3;
            MoveTo(e.X);
        }

        protected override void OnMouseMove(MouseEventArgs e) { if (_drag != 0) MoveTo(e.X); }
        protected override void OnMouseUp(MouseEventArgs e) => _drag = 0;

        private void MoveTo(int x)
        {
            double t = TOf(x);
            const double gap = 0.1;
            switch (_drag)
            {
                case 1: _start = Math.Clamp(t, 0, _end - gap); _playhead = _start; StartChanged?.Invoke(this, EventArgs.Empty); break;
                case 2: _end = Math.Clamp(t, _start + gap, _duration); _playhead = _end; EndChanged?.Invoke(this, EventArgs.Empty); break;
                default: _playhead = Math.Clamp(t, _start, _end); PlayheadChanged?.Invoke(this, EventArgs.Empty); break;
            }
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int midY = Height / 2;

            using (var track = new SolidBrush(Color.FromArgb(60, 62, 70)))
                g.FillRectangle(track, 12, midY - 7, Track, 14);

            int xs = XOf(_start), xe = XOf(_end);
            using (var sel = new SolidBrush(Color.FromArgb(70, 232, 72, 72)))
                g.FillRectangle(sel, xs, midY - 7, Math.Max(1, xe - xs), 14);

            int xp = XOf(_playhead);
            using (var ph = new Pen(Color.FromArgb(235, 235, 240), 2))
                g.DrawLine(ph, xp, midY - 14, xp, midY + 14);

            DrawHandle(g, xs, midY, Color.FromArgb(96, 204, 124));
            DrawHandle(g, xe, midY, Color.FromArgb(232, 72, 72));
        }

        private static void DrawHandle(Graphics g, int x, int midY, Color color)
        {
            using var b = new SolidBrush(color);
            g.FillRectangle(b, x - 3, midY - 16, 6, 32);
            g.FillPolygon(b, new[] { new Point(x - 3, midY - 16), new Point(x + 7, midY - 16), new Point(x - 3, midY - 6) });
        }
    }
}
