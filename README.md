# Clipfoo

A tiny Windows tray utility that records the **active window** to a short, share-ready MP4
clip — built for dropping clips into Discord and similar 10 MB-limited chats.

- Lives in the system tray; a global hotkey **starts and stops** recording.
- Records either the **active window** (stays glued to it, even through a fullscreen toggle) or the
  **whole screen** — your choice in settings.
- While recording, only a tiny **"REC" indicator** is shown — nothing else to fuss with.
- Records **system audio** (what you hear) alongside the video, muxed into one MP4.
- After you stop, a **trim dialog** plays the clip back with **video + audio** and lets you cut it
  to the length you want (and shows the resulting size) — then Save, or Discard the take.
- Runs as a **background desktop agent** — optionally launches at login.
- **Zero external dependencies** — capture is native Windows.Graphics.Capture + Media Foundation
  (+ WASAPI loopback for audio), and trimming is native Windows.Media.Editing. No ffmpeg.

## Using it

1. Run `Clipfoo.exe`. A circular icon appears in the system tray (near the clock).
2. Focus the window you want to record and press the hotkey (default **Ctrl+Alt+R**). A small
   **Now Recording** pill appears bottom-center (showing the hotkey to stop) and the tray icon
   turns red.
3. Press the hotkey again (or click the pill) to finish.
4. The dark **Trim & Save** dialog opens with a **video preview**:
   - Press **play** for real **video + audio** playback (bounded to your selected range); scrub the
     timeline or use the **frame-step** buttons to land on exact in/out points.
   - Drag the **green (start)** and **red (end)** handles to keep just the part you want — the
     preview follows the handle you drag, and the selection length and **estimated file size** are
     shown.
   - **Save** re-encodes the selected range into your output folder (with a **Cancel** button while
     it works); **Discard** throws the recording away; the **✕** keeps the full, untrimmed clip.

Files in your output folder name their state, so an in-progress recording is never mistaken for
a finished clip:

| Filename | State |
|----------|-------|
| `ClipFoo-<date>.pending.mp4` | Currently recording, or waiting for you to trim/keep it |
| `ClipFoo-<date>.trimming.mp4` | Being re-encoded while you Save |
| `ClipFoo-<date>.mp4` | Finished clip — ready to share |

**Save** and **✕ (keep)** rename the file to the finished `ClipFoo-<date>.mp4`; **Discard**
deletes it. Stray `.pending`/`.trimming` files from an interrupted session are cleaned up on the
next launch.

> The REC pill never steals focus and — because Windows.Graphics.Capture records the target
> window's own surface — does **not** appear in your clip.

Right-click the tray icon for the menu:

| Item | What it does |
|------|--------------|
| Configure… | Opens the settings window |
| Open Output Folder | Opens where clips are saved |
| Quit | Exits |

## Settings (tray → Configure…)

Recording is **preset** — you just hotkey. Settings are saved in the tray's Configure window:

- **Record** — **Active window** (the focused window; stays glued to it through focus changes and
  fullscreen toggles) or **Full screen** (the whole monitor the focused window is on — best for
  exclusive-fullscreen games).
- **Quality preset** — one pick sets output resolution, frame rate, and bitrate:

  | Preset | Resolution | FPS | Bitrate | Use case |
  |--------|-----------:|----:|--------:|----------|
  | **Quick share** | 720p | 30 | ~2.5 Mbps | Default; drop straight into Discord-style chats |
  | **Long clip** | 480p | 30 | ~1.2 Mbps | Keep long recordings under the size limit |
  | **Gameplay** | 720p | 60 | ~6 Mbps | Smooth, fast motion |
  | **Tutorial** | 1080p | 30 | ~5 Mbps | Crisp text, menus, UI |
  | **Original** | source | 60 | ~16 Mbps | Max quality to keep locally or edit |

  Resolution is the target *height*; the source aspect ratio is preserved and never upscaled.
- **Audio** — capture the system mix (WASAPI loopback) on/off, plus AAC bitrate (96–192 kbps).
- **Hotkey** — click the box and press a combo (e.g. `Ctrl+Alt+R`, or an F-key).
- **Save clips to** — output folder (default `Videos\Clipfoo`).
- **Run at login** — per-user startup entry so Clipfoo is always in the tray.

There is no file-size setting — the **trim dialog** handles hitting a size after recording.
Settings persist to `%APPDATA%\Clipfoo\config.json`.

## Building from source

Requirements: **.NET 9 SDK** (Windows). The project targets `net9.0-windows10.0.19041.0`.

```sh
# Debug build / run
dotnet run

# Self-contained single-file release (one Clipfoo.exe, ~57 MB, no runtime needed)
dotnet publish -c Release
# -> bin/Release/net9.0-windows10.0.19041.0/win-x64/publish/Clipfoo.exe
```

If NuGet has no source configured on a fresh machine:

```sh
dotnet nuget add source https://api.nuget.org/v3/index.json -n nuget.org
```

## How it works

| Concern | Implementation |
|---------|----------------|
| Capture target | Active window → `CreateForWindow` (at hotkey time); Full screen → `CreateForMonitor` on the focused window's monitor |
| Capture | `Windows.Graphics.Capture` free-threaded frame pool (Direct3D 11, BGRA). Follows the target's size changes (`ContentSize` → `Recreate`) and auto-stops when a captured window closes |
| Audio | NAudio `WasapiLoopbackCapture` (system mix) → 16-bit PCM → AAC stream in the same MP4. Silence gaps (loopback delivers no packets while nothing plays) are padded to keep A/V in sync |
| Encode | Media Foundation `IMFSinkWriter` → H.264 + AAC / MP4. The encoder is tuned for quality-per-byte: **High profile** (CABAC + 8×8 transform), **B-frames**, **peak-constrained VBR** (mean = preset bitrate, peak up to 2× so hard frames — motion, foliage — can borrow bits without macroblocking), and a quality-leaning speed setting, all configured via the encoder's `ICodecAPI` (`H264EncoderConfig`) instead of the Baseline/CBR default. Each property is `IsSupported`-guarded, so an unsupported one is skipped, never fatal |
| Resolution scaling | A Direct3D 11 video processor (`FrameScaler`) GPU-downscales each frame to the preset resolution (aspect-preserved, letterboxed), so the per-frame CPU readback is the small output size. If it can't be created, Media Foundation scales during encode instead |
| Preview playback | `Windows.Media.Playback.MediaPlayer` in **frame-server** mode (`VideoPreviewPlayer`) — audio plays through the default device; each decoded frame is copied via `CopyFrameToVideoSurface` into a D3D11 texture and shown. Paused scrubbing uses `MediaComposition.GetThumbnailAsync` |
| Trim | `Windows.Media.Editing.MediaComposition` re-encodes the selected range (cancelable) |
| Fast-start | Finished clips are rewritten with the `moov` atom in front (`FastStart`) so they stream/scrub immediately — no ffmpeg |
| Run at login | per-user `HKCU\…\Run` registry entry (`StartupManager`) |
| Constant frame rate | A dedicated encode thread writes the latest captured frame at the target fps, so clips stay valid even when the window is static |
| Tray / hotkey | WinForms `NotifyIcon` + Win32 `RegisterHotKey` on a message-only window |
| REC indicator | Borderless, topmost, **non-activating** (`WS_EX_NOACTIVATE`) pill; not captured by WGC, so it can float over the recorded window |
| Interop | [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows) (D3D11/DXGI/Media Foundation) — compiled in, not shipped as a separate binary |

## Notes & limitations

- Records at the **preset's** resolution (the source is downscaled, never upscaled, aspect
  preserved); trimming preserves that resolution and only changes duration.
- **Active window** capture is focus- and occlusion-proof: it keeps recording the window you started
  on no matter what you click, and windows stacked on top of it don't appear. It follows the window
  through resizes/fullscreen toggles — with any downscaling preset the GPU scaler rescales the new
  size to fit (letterboxed); only **Original quality** (no scaling) crops/letterboxes a mid-recording
  resize. For exclusive-fullscreen games, use **Full screen** mode (whole-monitor capture).
- Audio is the **system mix** (loopback), not per-window — it captures everything you hear.
  Microphone capture is intentionally not included. Requires a 44.1/48 kHz device (the norm);
  other rates fall back to video-only. Silence gaps are padded so A/V stays in sync even on
  very quiet clips.
- Trimming snaps precisely (re-encode), so very long recordings take a few seconds to export;
  the Save dialog shows progress and can be cancelled.
- Some windows that block capture (certain DRM/secure surfaces) can't be recorded.
