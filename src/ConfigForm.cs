using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace Clipfoo;

/// <summary>Tray settings window: capture mode, quality preset, audio, hotkey, output folder, run-at-login.</summary>
internal sealed class ConfigForm : Form
{
    private static readonly int[] AudioKbpsValues = { 96, 128, 160, 192 };

    private readonly AppConfig _cfg;
    public event Action<AppConfig>? Saved;

    private readonly TextBox _outputBox = new();
    private readonly TextBox _hotkeyBox = new() { ReadOnly = true };
    private readonly ComboBox _captureMode = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _preset = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label _presetHint = new() { ForeColor = Color.Gray, Font = new Font("Segoe UI", 8f), AutoSize = false };
    private readonly CheckBox _captureAudio = new() { Text = "Capture system audio (what you hear)" };
    private readonly ComboBox _audioKbps = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox _runAtStartup = new() { Text = "Run Clipfoo at login (background tray agent)" };

    private uint _pendingMods;
    private uint _pendingVk;

    public ConfigForm(AppConfig cfg)
    {
        _cfg = cfg;
        _pendingMods = cfg.Hotkey.Modifiers;
        _pendingVk = cfg.Hotkey.VirtualKey;

        Text = "Clipfoo Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(440, 400);
        Font = new Font("Segoe UI", 9f);

        BuildLayout();
        LoadFromConfig();
    }

    private void BuildLayout()
    {
        int pad = 16, w = ClientSize.Width - pad * 2, y = pad;

        Section("Record", pad, ref y);
        _captureMode.Items.Add("Active window (the focused window)");
        _captureMode.Items.Add("Full screen (the whole monitor)");
        _captureMode.SetBounds(pad, y, w, 24);
        Controls.Add(_captureMode);
        y += 26;
        Hint("Active window stays glued to the window you start on. Full screen records the monitor it's on.", pad, ref y);
        y += 12;

        Section("Quality preset", pad, ref y);
        foreach (VideoPreset p in Enum.GetValues<VideoPreset>()) _preset.Items.Add(AppConfig.PresetLabel(p));
        _preset.SetBounds(pad, y, w, 24);
        _preset.SelectedIndexChanged += (_, _) => UpdatePresetHint();
        Controls.Add(_preset);
        y += 26;
        _presetHint.SetBounds(pad, y, w, 16);
        Controls.Add(_presetHint);
        y += 24;

        Section("Audio", pad, ref y);
        _captureAudio.SetBounds(pad, y, 260, 24);
        _captureAudio.CheckedChanged += (_, _) => _audioKbps.Enabled = _captureAudio.Checked;
        var akLabel = new Label { Text = "Bitrate", Left = pad + 270, Top = y + 3, AutoSize = true };
        foreach (var a in AudioKbpsValues) _audioKbps.Items.Add($"{a} kbps");
        _audioKbps.SetBounds(pad + 322, y, 82, 24);
        Controls.Add(_captureAudio); Controls.Add(akLabel); Controls.Add(_audioKbps);
        y += 40;

        Section("Start / stop hotkey", pad, ref y);
        _hotkeyBox.SetBounds(pad, y, w - 90, 24);
        _hotkeyBox.KeyDown += HotkeyBox_KeyDown;
        _hotkeyBox.Enter += (_, _) => _hotkeyBox.BackColor = Color.LightYellow;
        _hotkeyBox.Leave += (_, _) => _hotkeyBox.BackColor = SystemColors.Window;
        var setBtn = new Button { Text = "Set", Left = pad + w - 82, Top = y - 1, Width = 82, Height = 26 };
        setBtn.Click += (_, _) => _hotkeyBox.Focus();
        Controls.Add(_hotkeyBox); Controls.Add(setBtn);
        y += 26;
        Hint("Click the box and press a combo (e.g. Ctrl+Alt+R, or an F-key).", pad, ref y);
        y += 8;

        Section("Save clips to", pad, ref y);
        _outputBox.SetBounds(pad, y, w - 90, 24);
        var browse = new Button { Text = "Browse…", Left = pad + w - 82, Top = y - 1, Width = 82, Height = 26 };
        browse.Click += (_, _) => BrowseFolder();
        Controls.Add(_outputBox); Controls.Add(browse);
        y += 38;

        _runAtStartup.SetBounds(pad, y, w, 24);
        Controls.Add(_runAtStartup);
        y += 36;

        var save = new Button { Text = "Save", Width = 90, Height = 30, Left = ClientSize.Width - pad - 190, Top = y };
        save.Click += (_, _) => Save_Click();
        var cancel = new Button { Text = "Cancel", Width = 90, Height = 30, Left = ClientSize.Width - pad - 90, Top = y };
        cancel.Click += (_, _) => Close();
        AcceptButton = save; CancelButton = cancel;
        Controls.Add(save); Controls.Add(cancel);

        ClientSize = new Size(ClientSize.Width, y + 30 + pad); // fit to content
    }

    private void UpdatePresetHint()
    {
        if (_preset.SelectedIndex >= 0)
            _presetHint.Text = AppConfig.PresetHint((VideoPreset)_preset.SelectedIndex);
    }

    private void Section(string text, int x, ref int y)
    {
        Controls.Add(new Label { Text = text, Left = x, Top = y, AutoSize = true, Font = new Font("Segoe UI", 9f, FontStyle.Bold) });
        y += 22;
    }

    private void Hint(string text, int x, ref int y)
    {
        Controls.Add(new Label { Text = text, Left = x, Top = y, AutoSize = true, ForeColor = Color.Gray, Font = new Font("Segoe UI", 8f) });
        y += 16;
    }

    private void LoadFromConfig()
    {
        _outputBox.Text = _cfg.OutputDirectory;
        _hotkeyBox.Text = _cfg.Hotkey.Display;
        _captureMode.SelectedIndex = _cfg.CaptureMode == CaptureMode.FullScreen ? 1 : 0;
        _preset.SelectedIndex = (int)_cfg.Preset;
        UpdatePresetHint();
        _captureAudio.Checked = _cfg.AudioSource == AudioSource.System;
        _audioKbps.SelectedIndex = Math.Max(0, Array.IndexOf(AudioKbpsValues, _cfg.AudioBitrateKbps));
        _audioKbps.Enabled = _captureAudio.Checked;
        _runAtStartup.Checked = _cfg.RunAtStartup || StartupManager.IsEnabled();
    }

    private void HotkeyBox_KeyDown(object? sender, KeyEventArgs e)
    {
        e.SuppressKeyPress = true;
        e.Handled = true;
        var key = e.KeyCode;
        if (key is Keys.ControlKey or Keys.Menu or Keys.ShiftKey or Keys.LWin or Keys.RWin) return;

        uint mods = 0;
        if (e.Control) mods |= Hotkey.MOD_CONTROL;
        if (e.Alt) mods |= Hotkey.MOD_ALT;
        if (e.Shift) mods |= Hotkey.MOD_SHIFT;
        bool isFunctionKey = key is >= Keys.F1 and <= Keys.F12;
        if (mods == 0 && !isFunctionKey) { System.Media.SystemSounds.Beep.Play(); return; }

        _pendingMods = mods;
        _pendingVk = (uint)key;
        _hotkeyBox.Text = Hotkey.Describe(mods, (uint)key);
    }

    private void BrowseFolder()
    {
        using var dlg = new FolderBrowserDialog { Description = "Where should clips be saved?" };
        if (Directory.Exists(_outputBox.Text)) dlg.SelectedPath = _outputBox.Text;
        if (dlg.ShowDialog(this) == DialogResult.OK) _outputBox.Text = dlg.SelectedPath;
    }

    private void Save_Click()
    {
        if (string.IsNullOrWhiteSpace(_outputBox.Text))
        {
            MessageBox.Show(this, "Please choose a folder to save clips to.", "Clipfoo",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        try { Directory.CreateDirectory(_outputBox.Text); }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"That folder can't be used:\n{ex.Message}", "Clipfoo",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _cfg.OutputDirectory = _outputBox.Text.Trim();
        _cfg.CaptureMode = _captureMode.SelectedIndex == 1 ? CaptureMode.FullScreen : CaptureMode.ActiveWindow;
        _cfg.Hotkey.Modifiers = _pendingMods;
        _cfg.Hotkey.VirtualKey = _pendingVk;
        _cfg.ApplyPreset((VideoPreset)_preset.SelectedIndex);
        _cfg.AudioSource = _captureAudio.Checked ? AudioSource.System : AudioSource.None;
        _cfg.AudioBitrateKbps = AudioKbpsValues[_audioKbps.SelectedIndex];
        _cfg.RunAtStartup = _runAtStartup.Checked;

        Saved?.Invoke(_cfg);
        Close();
    }
}
