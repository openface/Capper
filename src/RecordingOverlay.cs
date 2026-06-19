using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Capper;

/// <summary>
/// A small, non-activating "Now Recording" indicator pill shown at the bottom of the screen while
/// capturing. It never takes focus and — because Windows.Graphics.Capture records the target
/// window's own surface — never appears in the recorded clip. Clicking it (or the hotkey) stops.
/// </summary>
internal sealed class RecordingOverlay : Form
{
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TOPMOST = 0x00000008;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    private const int SW_SHOWNOACTIVATE = 4;

    private static readonly Color Bg = Theme.Bg;
    private static readonly Color Fg = Theme.Fg;
    private static readonly Color Muted = Theme.Muted;
    private static readonly Color Accent = Theme.Accent;

    public event Action? StopRequested;

    private readonly Label _title;
    private readonly Label _hint;
    // Slow pulse on the record dot — the universal "recording" cue.
    private readonly System.Windows.Forms.Timer _pulse = new() { Interval = 33 };
    private double _pulsePhase;
    private TimeSpan _elapsed;
    private string _stopHotkey = "";

    public RecordingOverlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;
        BackColor = Bg;
        Size = new Size(228, 56);

        _title = new Label
        {
            ForeColor = Fg,
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI Semibold", 10f),
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Bounds = new Rectangle(36, 8, Width - 44, 22),
            Cursor = Cursors.Hand,
        };
        _hint = new Label
        {
            ForeColor = Muted,
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI", 8f),
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Bounds = new Rectangle(36, 30, Width - 44, 18),
            Cursor = Cursors.Hand,
        };
        _title.Click += (_, _) => StopRequested?.Invoke();
        _hint.Click += (_, _) => StopRequested?.Invoke();
        Click += (_, _) => StopRequested?.Invoke();
        Controls.Add(_title);
        Controls.Add(_hint);

        _pulse.Tick += (_, _) =>
        {
            _pulsePhase += 0.16;
            Invalidate(new Rectangle(8, 15, 24, 24)); // just the dot + glow area
        };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
            return cp;
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Region = new Region(GdiHelpers.RoundedRectangle(new Rectangle(0, 0, Width, Height), 14));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        // t sweeps 0..1..0; the dot brightens and a soft halo breathes outward.
        double t = (Math.Sin(_pulsePhase) + 1) / 2;
        const float cx = 20, cy = 27; // center of the 12px dot at (14,21)

        float halo = 12 + 8 * (float)t;
        using (var glow = new SolidBrush(Color.FromArgb((int)(70 * (1 - t)), Accent)))
            e.Graphics.FillEllipse(glow, cx - halo / 2, cy - halo / 2, halo, halo);

        using var dot = new SolidBrush(Color.FromArgb(150 + (int)(105 * t), Accent));
        e.Graphics.FillEllipse(dot, 14, 21, 12, 12);
    }

    public void ShowRecording(TimeSpan elapsed, string stopHotkey)
    {
        _stopHotkey = stopHotkey;
        UpdateElapsed(elapsed);
        _hint.Text = $"{stopHotkey} to stop";
        if (!Visible)
        {
            ShowWindow(Handle, SW_SHOWNOACTIVATE);
            Visible = true;
        }
        Reposition();
        TopMost = true;
        BringToFront();
        _pulse.Start();
    }

    private void Reposition()
    {
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(wa.X + (wa.Width - Width) / 2, wa.Bottom - Height - 24);
    }

    public void UpdateElapsed(TimeSpan elapsed)
    {
        _elapsed = elapsed;
        _title.Text = $"Now Recording   ·   {(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}";
    }

    public void HideOverlay()
    {
        _pulse.Stop();
        Hide();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _pulse.Dispose();
        base.Dispose(disposing);
    }
}
