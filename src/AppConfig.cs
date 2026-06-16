using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Clipfoo;

/// <summary>
/// A global hotkey expressed as Win32 RegisterHotKey modifiers + virtual-key code.
/// </summary>
public sealed class Hotkey
{
    // MOD_* flags from RegisterHotKey.
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    public uint Modifiers { get; set; } = MOD_CONTROL | MOD_ALT;
    public uint VirtualKey { get; set; } = 0x52; // 'R'

    [JsonIgnore]
    public string Display => Describe(Modifiers, VirtualKey);

    public static string Describe(uint modifiers, uint vk)
    {
        var parts = new List<string>();
        if ((modifiers & MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((modifiers & MOD_ALT) != 0) parts.Add("Alt");
        if ((modifiers & MOD_SHIFT) != 0) parts.Add("Shift");
        if ((modifiers & MOD_WIN) != 0) parts.Add("Win");
        parts.Add(KeyName(vk));
        return string.Join("+", parts);
    }

    private static string KeyName(uint vk)
    {
        // Letters and digits map directly to ASCII.
        if (vk >= 0x30 && vk <= 0x5A) return ((char)vk).ToString();
        return vk switch
        {
            0x70 => "F1", 0x71 => "F2", 0x72 => "F3", 0x73 => "F4",
            0x74 => "F5", 0x75 => "F6", 0x76 => "F7", 0x77 => "F8",
            0x78 => "F9", 0x79 => "F10", 0x7A => "F11", 0x7B => "F12",
            0x20 => "Space", 0x2D => "Insert", 0x2E => "Delete",
            0x24 => "Home", 0x23 => "End", 0x21 => "PageUp", 0x22 => "PageDown",
            _ => $"0x{vk:X2}",
        };
    }
}

/// <summary>Where recorded audio comes from.</summary>
public enum AudioSource
{
    None,
    System,  // WASAPI loopback — what you hear (app/game/video sound)
}

/// <summary>Use-case capture presets that bundle output resolution, frame rate, and bitrate.</summary>
public enum VideoPreset
{
    Discord,          // 720p / 30 fps — safe all-round for Discord-style sharing
    SmallFile,        // 480p / 30 fps — longer clips, smaller files
    Smooth,           // 720p / 60 fps — gameplay / fast motion
    Sharp,            // 1080p / 30 fps — text, UI, tutorials
    OriginalQuality,  // source resolution / 60 fps — save locally / upload elsewhere
}

/// <summary>What the recorder captures.</summary>
public enum CaptureMode
{
    /// <summary>The window focused when recording starts; stays glued to it (even through a
    /// fullscreen toggle) regardless of what you click next. Other windows on top aren't captured.</summary>
    ActiveWindow,

    /// <summary>The whole monitor the focused window is on — best for exclusive-fullscreen games
    /// or grabbing everything on a screen.</summary>
    FullScreen,
}

/// <summary>
/// Persisted application settings plus the file-size / clip-length estimation math.
/// </summary>
public sealed class AppConfig
{
    // --- Persisted settings ---
    public string OutputDirectory { get; set; } = DefaultOutputDirectory();
    public Hotkey Hotkey { get; set; } = new();

    /// <summary>The selected capture preset (drives resolution, frame rate, and bitrate).</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public VideoPreset Preset { get; set; } = VideoPreset.Discord;

    /// <summary>Target output height in pixels; the source aspect ratio is preserved and the image
    /// is never upscaled past the source. 0 = keep the source resolution.</summary>
    public int TargetHeight { get; set; } = 720;

    /// <summary>Target average video bitrate. This is the quality knob and the size driver.</summary>
    public int VideoBitrateKbps { get; set; } = 2500;

    /// <summary>Capture/encode frame rate.</summary>
    public int Fps { get; set; } = 30;

    /// <summary>Whether the hotkey records the focused window or the whole screen.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public CaptureMode CaptureMode { get; set; } = CaptureMode.ActiveWindow;

    /// <summary>Audio to capture alongside the video.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AudioSource AudioSource { get; set; } = AudioSource.System;

    /// <summary>Target AAC audio bitrate when audio is enabled.</summary>
    public int AudioBitrateKbps { get; set; } = 128;

    /// <summary>Launch Clipfoo automatically when you log in (runs as a tray agent).</summary>
    public bool RunAtStartup { get; set; } = false;

    // --- Presets ---

    /// <summary>The (output height, fps, bitrate-kbps) a preset resolves to. Height 0 = source.</summary>
    public static (int Height, int Fps, int Kbps) PresetValues(VideoPreset p) => p switch
    {
        VideoPreset.Discord => (720, 30, 2500),
        VideoPreset.SmallFile => (480, 30, 1200),
        VideoPreset.Smooth => (720, 60, 4500),
        VideoPreset.Sharp => (1080, 30, 5000),
        VideoPreset.OriginalQuality => (0, 60, 8000),
        _ => (720, 30, 2500),
    };

    public static string PresetLabel(VideoPreset p) => p switch
    {
        VideoPreset.Discord => "Discord — 720p, 30 fps",
        VideoPreset.SmallFile => "Small file — 480p, 30 fps",
        VideoPreset.Smooth => "Smooth — 720p, 60 fps",
        VideoPreset.Sharp => "Sharp — 1080p, 30 fps",
        VideoPreset.OriginalQuality => "Original quality — source, 60 fps",
        _ => p.ToString(),
    };

    public static string PresetHint(VideoPreset p) => p switch
    {
        VideoPreset.Discord => "Best all-round for sharing to Discord and similar chats.",
        VideoPreset.SmallFile => "Lower resolution for longer clips and smaller uploads.",
        VideoPreset.Smooth => "Higher frame rate for gameplay and fast motion.",
        VideoPreset.Sharp => "Full 1080p for crisp text, menus and tutorials.",
        VideoPreset.OriginalQuality => "Records at the source resolution — largest files.",
        _ => "",
    };

    /// <summary>Set the preset and apply its resolution/fps/bitrate to this config.</summary>
    public void ApplyPreset(VideoPreset p)
    {
        var (h, fps, kbps) = PresetValues(p);
        Preset = p;
        TargetHeight = h;
        Fps = fps;
        VideoBitrateKbps = kbps;
    }

    public static string FormatDuration(double seconds)
    {
        if (double.IsInfinity(seconds) || seconds <= 0) return "0:00";
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}"
            : $"{ts.Minutes}:{ts.Seconds:00}";
    }

    // --- Persistence ---

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string AppDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Clipfoo");

    public static string ConfigPath => Path.Combine(AppDataDir, "config.json");

    public static string DefaultOutputDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Clipfoo");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOpts);
                if (cfg != null)
                {
                    cfg.Hotkey ??= new Hotkey();
                    if (string.IsNullOrWhiteSpace(cfg.OutputDirectory))
                        cfg.OutputDirectory = DefaultOutputDirectory();
                    return cfg;
                }
            }
        }
        catch
        {
            // Corrupt config -> fall back to defaults rather than failing to launch.
        }
        return new AppConfig();
    }

    public void Save()
    {
        Directory.CreateDirectory(AppDataDir);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOpts));
    }

    public AppConfig Clone()
    {
        return new AppConfig
        {
            OutputDirectory = OutputDirectory,
            Hotkey = new Hotkey { Modifiers = Hotkey.Modifiers, VirtualKey = Hotkey.VirtualKey },
            Preset = Preset,
            TargetHeight = TargetHeight,
            VideoBitrateKbps = VideoBitrateKbps,
            Fps = Fps,
            CaptureMode = CaptureMode,
            AudioSource = AudioSource,
            AudioBitrateKbps = AudioBitrateKbps,
            RunAtStartup = RunAtStartup,
        };
    }
}
