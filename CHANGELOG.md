# Changelog

All notable changes to Capper are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- **In-app updates** via [Velopack](https://velopack.io): an installer (`Capper-win-Setup.exe`) with
  delta updates, plus a **Check for updates…** tray menu item that installs a newer version and
  restarts.

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

[Unreleased]: https://github.com/openface/Capper/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/openface/Capper/releases/tag/v1.0.0
