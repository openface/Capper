# Changelog

All notable changes to Capper are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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
