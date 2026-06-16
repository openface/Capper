using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Clipfoo;

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

    private static readonly Color Bg = Color.FromArgb(24, 25, 28);
    private static readonly Color Fg = Color.FromArgb(238, 239, 242);
    private static readonly Color Muted = Color.FromArgb(150, 153, 160);
    private static readonly Color Accent = Color.FromArgb(232, 72, 72);

    public event Action? StopRequested;

    private readonly Label _title;
    private readonly Label _hint;
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
        var path = new GraphicsPath();
        int d = 14 * 2;
        path.AddArc(0, 0, d, d, 180, 90);
        path.AddArc(Width - d, 0, d, d, 270, 90);
        path.AddArc(Width - d, Height - d, d, d, 0, 90);
        path.AddArc(0, Height - d, d, d, 90, 90);
        path.CloseFigure();
        Region = new Region(path);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var dot = new SolidBrush(Accent);
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

    public void HideOverlay() => Hide();
}
