# Changelog
All notable changes to Unity Multi Timeline Recorder will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.5.19] - 2026-08-03

### Fixed
- Movie recorder validation now uses encoder-aware resolution limits instead of a
  flat 4096x4096 cap: H.264 (built-in MP4 / NVENC H.264) stays at 4096, NVENC HEVC
  and ProRes (MOV) allow up to 8192, WebM (VP8) up to 16383. The flat cap silently
  dropped valid recordings such as wide LED-preview RenderTextures (e.g. 7488x1344)
  exported as WebM — the recording "completed" but no file was written.
- RenderTexture-source movie items are now validated against the RT's actual size
  (the Recorder always outputs at the RT's own resolution, not the item's
  width/height setting). Applies to both the local recording path and the
  distributed `RecorderSettingsBuilderShared.BuildMovieSettings` path.

### Added
- Pre-recording dialog: starting a recording with an H.264 movie item whose
  effective resolution exceeds 4096px now prompts to switch the output format to
  WebM or ProRes (MOV) — or cancel — instead of silently skipping the item.
- The per-recorder settings UI shows the same encoder-aware resolution error
  inline (effective RT size for RenderTexture sources).

## [1.1.0] - 2026-06-02

### Added (fork: distributed rendering)
- LAN-distributed rendering: dispatch each selected Timeline as a job to Worker
  machines on the local network, run MTR's render pipeline headlessly on each
  Worker (ControlTrack-driven, honoring the MTR recorder settings), and collect
  results to `Recordings/Distributed/<YYYYMMDDHHMMSS>/<TimelineName>/`.
- HMAC-authenticated Master↔Worker HTTP transport, UDP worker discovery, and a
  Setup Wizard (`DistributedRecorder > Setup Wizard`) for password/registry/sync.
- Dispatch queue + retry: a single Worker records more Timelines than available
  workers sequentially — busy jobs are re-queued instead of failed.
- Sample generator: `DistributedRecorder > Create MTR Multi-Timeline Sample`.

## [1.0.0] - 2024-07-13

### Added
- Initial release of Unity Multi Timeline Recorder
- Multi-timeline batch recording functionality
- Support for multiple output formats:
  - Movie (MP4, MOV, WebM)
  - Image Sequences (PNG, JPG, EXR)
  - Animation Clips
  - Alembic
  - FBX
  - AOV (Arbitrary Output Variables)
- Flexible per-timeline recorder configuration
- Advanced path management with wildcard support
- Play Mode recording with real-time progress monitoring
- Comprehensive Editor UI for managing recordings
- Assembly definitions for proper code organization
- Full documentation and examples