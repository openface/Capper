using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Clipfoo;

/// <summary>
/// Tray-resident app. The hotkey starts/stops recording the focused window; while recording a
/// minimal indicator is shown. On stop, the trim dialog opens so the clip can be cut to size.
/// Settings live in the tray's Configure window.
/// </summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly Icon _idleIcon;
    private readonly Icon _recordingIcon;
    private readonly HotKeyWindow _hotkey;
    private readonly System.Windows.Forms.Timer _recTimer;
    private readonly SynchronizationContext _ui;
    private readonly RecordingOverlay _overlay;

    private AppConfig _config;
    private WindowCaptureRecorder? _recorder;
    private ConfigForm? _configForm;
    private DateTime _recStart;
    private string? _pendingFinalPath;
    private bool _hotkeyActive;
    private bool _balloonOpensConfig;

    public TrayApplicationContext()
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _config = AppConfig.Load();
        StartupManager.Apply(_config.RunAtStartup);
        CleanupOrphanedStaging();

        _idleIcon = TrayIcons.Create(false);
        _recordingIcon = TrayIcons.Create(true);

        _overlay = new RecordingOverlay();
        _overlay.StopRequested += StopRecording;

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Configure…", null, (_, _) => OpenConfig()));
        menu.Items.Add(new ToolStripMenuItem("Open Output Folder", null, (_, _) => OpenOutputFolder()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Quit", null, (_, _) => Quit()));

        _tray = new NotifyIcon
        {
            Icon = _idleIcon,
            Text = "Clipfoo — idle",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => OpenConfig();
        _tray.BalloonTipClicked += (_, _) =>
        {
            if (_balloonOpensConfig) { _balloonOpensConfig = false; OpenConfig(); }
        };

        _hotkey = new HotKeyWindow();
        _hotkey.HotKeyPressed += OnHotkey;
        RegisterHotkey();

        _recTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _recTimer.Tick += (_, _) =>
        {
            if (_recorder is { IsRecording: true }) _overlay.UpdateElapsed(DateTime.Now - _recStart);
        };

        Notify("Clipfoo is running",
            $"Press {_config.Hotkey.Display} while a window is focused to start recording it; " +
            "press again to stop and trim.", ToolTipIcon.Info);
    }

    private void OnHotkey()
    {
        if (_recorder is { IsRecording: true }) StopRecording();
        else StartRecording();
    }

    private void StartRecording()
    {
        IntPtr fg = Native.GetActiveWindow();

        // Choose what to capture based on the configured mode.
        Windows.Graphics.Capture.GraphicsCaptureItem? item;
        if (_config.CaptureMode == CaptureMode.FullScreen)
        {
            item = Native.CreateItemForMonitor(Native.GetMonitorForWindow(fg));
        }
        else
        {
            if (fg == IntPtr.Zero
                || Native.GetWindowProcessId(fg) == Environment.ProcessId
                || !Native.IsCapturableWindow(fg))
            {
                Notify("Clipfoo",
                    "Focus the window you want to record, then press the hotkey.", ToolTipIcon.Info);
                return;
            }
            item = Native.CreateItemForWindow(fg);
        }

        if (item == null)
        {
            Notify("Clipfoo", "Couldn't start capture for the selected target.", ToolTipIcon.Warning);
            return;
        }

        // Record into a ".pending.mp4" staging file in the output folder; it's renamed to the
        // final "ClipFoo-<date>.mp4" only once the user keeps/saves it from the trim dialog.
        string finalPath = BuildOutputPath();
        string stagingPath = ClipFiles.PendingPath(finalPath);
        _pendingFinalPath = finalPath;
        try
        {
            var recorder = new WindowCaptureRecorder();
            recorder.Stopped += OnRecordingStopped;
            recorder.Start(item, _config, stagingPath);
            _recorder = recorder;
            _recStart = DateTime.Now;
            UpdateRecordingUi(true);
            _overlay.ShowRecording(TimeSpan.Zero, _config.Hotkey.Display);
            _recTimer.Start();
        }
        catch (Exception ex)
        {
            _recorder = null;
            UpdateRecordingUi(false);
            Notify("Clipfoo — couldn't start recording", ex.Message, ToolTipIcon.Error, 6000);
        }
    }

    private void StopRecording()
    {
        try { _recorder?.Stop(); }
        catch { /* OnRecordingStopped reports failures */ }
    }

    private void OnRecordingStopped(RecordingResult result)
    {
        _ui.Post(_ =>
        {
            _recorder = null;
            _recTimer.Stop();
            UpdateRecordingUi(false);
            _overlay.HideOverlay();

            if (result.Success && File.Exists(result.OutputPath))
            {
                OpenTrimDialog(result.OutputPath, _pendingFinalPath ?? BuildOutputPath());
            }
            else if (!result.Success)
            {
                Notify("Clipfoo — recording failed",
                    result.Error ?? "Unknown error", ToolTipIcon.Error, 6000);
            }
        }, null);
    }

    private void OpenTrimDialog(string stagingPath, string finalPath)
    {
        var dlg = new TrimDialog(stagingPath, finalPath, _config);
        dlg.Show();
        dlg.Activate();
    }

    private void UpdateRecordingUi(bool recording)
    {
        _tray.Icon = recording ? _recordingIcon : _idleIcon;
        _tray.Text = recording ? "Clipfoo — recording…" : "Clipfoo — idle";
    }

    private void RegisterHotkey()
    {
        _hotkeyActive = _hotkey.Register(_config.Hotkey.Modifiers, _config.Hotkey.VirtualKey);
        if (_hotkeyActive)
        {
            if (_recorder is not { IsRecording: true }) _tray.Text = "Clipfoo — idle";
        }
        else
        {
            // Make the failure actionable: the tooltip shows it's off, and clicking the
            // balloon jumps straight to Configure to pick a free combo.
            _tray.Text = $"Clipfoo — hotkey {_config.Hotkey.Display} unavailable";
            Notify("Clipfoo — hotkey unavailable",
                $"{_config.Hotkey.Display} is in use by another app. Click here to choose a different hotkey.",
                ToolTipIcon.Warning, 6000, opensConfig: true);
        }
    }

    /// <summary>Show a tray balloon. If <paramref name="opensConfig"/> is set, clicking it opens Configure.</summary>
    private void Notify(string title, string message, ToolTipIcon icon, int ms = 4000, bool opensConfig = false)
    {
        _balloonOpensConfig = opensConfig;
        _tray.ShowBalloonTip(ms, title, message, icon);
    }

    private void OpenConfig()
    {
        if (_configForm is { IsDisposed: false })
        {
            _configForm.Activate();
            return;
        }
        _configForm = new ConfigForm(_config.Clone());
        _configForm.Saved += updated =>
        {
            _config = updated;
            _config.Save();
            RegisterHotkey();
            StartupManager.Apply(_config.RunAtStartup);
        };
        _configForm.FormClosed += (_, _) => _configForm = null;
        _configForm.Show();
        _configForm.Activate();
    }

    private void OpenOutputFolder()
    {
        try
        {
            string dir = string.IsNullOrWhiteSpace(_config.OutputDirectory)
                ? AppConfig.DefaultOutputDirectory() : _config.OutputDirectory;
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Notify("Clipfoo", ex.Message, ToolTipIcon.Error);
        }
    }

    private string BuildOutputPath()
    {
        string dir = string.IsNullOrWhiteSpace(_config.OutputDirectory)
            ? AppConfig.DefaultOutputDirectory() : _config.OutputDirectory;
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"ClipFoo-{DateTime.Now:yyyyMMdd-HHmmss}.mp4");
    }

    /// <summary>At launch no trim dialog is open, so any ".pending"/".trimming" file in the
    /// output folder is a leftover from a previous session (e.g. a crash mid-recording).
    /// Best-effort delete so only finished "ClipFoo-&lt;date&gt;.mp4" clips remain.</summary>
    private void CleanupOrphanedStaging()
    {
        try
        {
            string dir = string.IsNullOrWhiteSpace(_config.OutputDirectory)
                ? AppConfig.DefaultOutputDirectory() : _config.OutputDirectory;
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.EnumerateFiles(dir, "ClipFoo-*.pending.mp4")
                         .Concat(Directory.EnumerateFiles(dir, "ClipFoo-*.trimming.mp4")))
            {
                try { File.Delete(f); } catch { }
            }
        }
        catch { }
    }

    private void Quit()
    {
        try { _recorder?.Stop(); } catch { }
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _recTimer.Dispose();
            _overlay.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            _hotkey.Dispose();
            _recorder?.Dispose();
            _idleIcon.Dispose();
            _recordingIcon.Dispose();
        }
        base.Dispose(disposing);
    }
}
