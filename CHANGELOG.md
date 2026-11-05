# Changelog

All notable changes to Capper are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.0.3] - 2026-11-05

### Fixed
- **Stuttery recordings** of fast-moving content (games, animations), most visible on high-refresh
  displays. Windows' capture API delivered at most ~60 frames/s by default, which a 144 Hz display
  rounds down to 48 Hz; encoding that at 30 fps made motion lurch between half and double speed.
  Capper now captures every vsync (Windows 11 24H2+).
- Frames are now placed on the 30/60 fps timeline by their **capture timestamp** instead of a
  sleep-paced sampling loop, so Windows' coarse ~15.6 ms timer can't make frame spacing uneven.

### Added
- **Save & Copy** button in the Trim dialog: saves the clip and copies it to the clipboard as a file,
  so it can be pasted straight into Discord.
- Each recording appends a line of capture stats (frames received, longest gap, repeated frames) to
  `%APPDATA%\Capper\recordings.log`, to help diagnose any stuttery clip.

### Changed
- Builds against the Windows 10.0.26100 SDK (still runs on Windows 10 1809+).

## [1.0.2] - 2026-06-19

### Changed
- **Check for updates…** is now a manual tray menu item; Capper no longer checks automatically on
  startup.

## [1.0.1] - 2026-06-19

### Added
- **In-app updates** via [Velopack](https://velopack.io): an installer (`Capper-win-Setup.exe`) with
  delta updates, and an "Install update" flow that downloads a newer version and restarts.

### Fixed
- Recording no longer plays back **too fast toward the end**: frames are now timestamped by real
  elapsed time, so playback stays at true speed even when the encoder briefly falls behind.
- **Trim preview playback** is smooth: removed per-frame allocations that caused GC stalls, added
  frame-drop backpressure, and matched the decode resolution to the preview size for a sharper image.

## [1.0.0] - 2026-06-18

### Added
- Tray app that records the **active window** (or full screen) to a share-ready MP4.
- Global hotkey to start/stop; a non-activating **Now Recording** pill that isn't captured in the clip.
- **System-audio** capture (WASAPI loopback) muxed into the clip, with silence padding for A/V sync.
- **Trim and Save Clip** dialog: video + audio preview, scrubbing, in/out handles, and a live
  estimated file size.
- **Quality presets** (resolution / fps / bitrate), GPU downscaling, a quality-tuned H.264 encoder,
  and MP4 **fast-start** so clips stream immediately — all with no external dependencies (no ffmpeg).
- Dark UI theme, app/tray icon, and optional run-at-login.

[Unreleased]: https://github.com/openface/Capper/compare/v1.0.3...HEAD
[1.0.3]: https://github.com/openface/Capper/compare/v1.0.2...v1.0.3
[1.0.2]: https://github.com/openface/Capper/compare/v1.0.1...v1.0.2
[1.0.1]: https://github.com/openface/Capper/compare/v1.0.0...v1.0.1
[1.0.0]: https://github.com/openface/Capper/releases/tag/v1.0.0
