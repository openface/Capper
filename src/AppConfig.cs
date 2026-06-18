using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Capper;

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

/// <summary>Use-case capture presets that bundle output resolution, frame rate, and bitrate.
/// Each name says what the clip is *for*. The order is load-bearing: <see cref="ConfigForm"/> maps
/// the combo-box index to <c>(int)</c> the enum value, so new presets must be appended, not inserted.
/// Legacy names from before the rename are migrated on load by <see cref="VideoPresetJsonConverter"/>.</summary>
public enum VideoPreset
{
    QuickShare,  // 720p / 30 fps — default; drop straight into chat, plays anywhere (was "Discord")
    LongClip,    // 480p / 30 fps — keep a long recording under the size limit (was "SmallFile")
    Gameplay,    // 720p / 60 fps — fast motion without judder (was "Smooth")
    Tutorial,    // 1080p / 30 fps — crisp text, menus, UI (was "Sharp")
    Original,    // source resolution / 60 fps — max quality to keep or edit (was "OriginalQuality")
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
    [JsonConverter(typeof(VideoPresetJsonConverter))]
    public VideoPreset Preset { get; set; } = VideoPreset.QuickShare;

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

    /// <summary>Launch Capper automatically when you log in (runs as a tray agent).</summary>
    public bool RunAtStartup { get; set; } = false;

    // --- Presets ---

    /// <summary>The (output height, fps, bitrate-kbps) a preset resolves to. Height 0 = source.</summary>
    public static (int Height, int Fps, int Kbps) PresetValues(VideoPreset p) => p switch
    {
        VideoPreset.QuickShare => (720, 30, 2500),
        VideoPreset.LongClip => (480, 30, 1200),
        VideoPreset.Gameplay => (720, 60, 6000),
        VideoPreset.Tutorial => (1080, 30, 5000),
        VideoPreset.Original => (0, 60, 16000),
        _ => (720, 30, 2500),
    };

    public static string PresetLabel(VideoPreset p) => p switch
    {
        VideoPreset.QuickShare => "Quick share — 720p, 30 fps",
        VideoPreset.LongClip => "Long clip — 480p, 30 fps",
        VideoPreset.Gameplay => "Gameplay — 720p, 60 fps",
        VideoPreset.Tutorial => "Tutorial — 1080p, 30 fps",
        VideoPreset.Original => "Original — source, 60 fps",
        _ => p.ToString(),
    };

    public static string PresetHint(VideoPreset p) => p switch
    {
        VideoPreset.QuickShare => "Best all-round for sharing to Discord and similar chats.",
        VideoPreset.LongClip => "Lower resolution for longer recordings and smaller uploads.",
        VideoPreset.Gameplay => "Higher frame rate to keep fast motion smooth.",
        VideoPreset.Tutorial => "Full 1080p for crisp text, menus and tutorials.",
        VideoPreset.Original => "Records at the source resolution — largest files.",
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
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Capper");

    public static string ConfigPath => Path.Combine(AppDataDir, "config.json");

    public static string DefaultOutputDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Capper");

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

/// <summary>
/// Serializes <see cref="VideoPreset"/> as its name, and on read accepts both the current names and
/// the legacy names from before the rename, so an existing <c>config.json</c> keeps its chosen preset
/// instead of silently resetting. Unknown values fall back to the default rather than throwing.
/// </summary>
public sealed class VideoPresetJsonConverter : JsonConverter<VideoPreset>
{
    public override VideoPreset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int n)
            && Enum.IsDefined(typeof(VideoPreset), n))
            return (VideoPreset)n;

        return reader.GetString() switch
        {
            // Current names.
            "QuickShare" => VideoPreset.QuickShare,
            "LongClip" => VideoPreset.LongClip,
            "Gameplay" => VideoPreset.Gameplay,
            "Tutorial" => VideoPreset.Tutorial,
            "Original" => VideoPreset.Original,
            // Legacy names (pre-rename) — migrated so old configs don't reset.
            "Discord" => VideoPreset.QuickShare,
            "SmallFile" => VideoPreset.LongClip,
            "Smooth" => VideoPreset.Gameplay,
            "Sharp" => VideoPreset.Tutorial,
            "OriginalQuality" => VideoPreset.Original,
            _ => VideoPreset.QuickShare,
        };
    }

    public override void Write(Utf8JsonWriter writer, VideoPreset value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());
}
