# Changelog
All notable changes to Unity Multi Timeline Recorder will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.5.23] - 2026-08-05

### Fixed
- VP9/WebM: the output file no longer stays at 0 bytes for the whole recording.
  ffmpeg's webm muxer buffered everything until a clean close, so the file
  looked "not saved" while recording, an abnormal end (Play Mode killed,
  editor crash, stall-guard abort) lost the entire recording, and the Encoder
  Output Stall Guard could falsely abort the run because the file never grew.
  `-flush_packets 1 -cluster_time_limit 2000` forces continuous writes — the
  file now grows from ~2 s after recording starts (measured).

## [1.5.22] - 2026-08-04

### Added
- VP9 / WebM output with proper BT.709 color: new encoder option
  "FFmpeg VP9 (WebM, BT.709)" (`MovieEncoderType.FFmpegVp9`). Encodes with
  libvpx-vp9 (software — NVENC cannot encode VP9), converts RGB→YUV with the
  BT.709 matrix (limited range) and writes full color metadata
  (color_space / primaries / trc = bt709, range = tv) into the WebM stream.
  Audio is encoded as Opus (WebM does not allow AAC). Requires ffmpeg.exe,
  same as the NVENC options; the QP slider acts as CRF for VP9.

### Changed
- The FFmpeg NVENC (H.264 / HEVC) paths now share the same BT.709
  conversion/tagging. Previously the RGB→YUV conversion used swscale's
  default BT.601 matrix with no color tags, so players assuming BT.709 for
  HD content showed slightly shifted colors. New NVENC output is
  colorimetrically correct and consistent with the VP9 path (note: it will
  differ very slightly from pre-1.5.22 renders).

## [1.5.21] - 2026-08-04

### Fixed
- Render History: runs that ended abnormally no longer keep their timer running.
  A low-frequency watchdog in the editor update loop finalizes leftover Running
  entries (instantly when the window state is Error / Complete / Idle / stale
  Recording, and after a 300 s grace when stuck in a preparation state), and a
  catch-all on Play Mode exit finalizes the entry no matter which state branch
  was taken (Error when the renderer set the failure flag, Completed when the
  completion flag is set, otherwise Interrupted).
- `<RecorderName>` wildcard now resolves to the recorder item's display name
  (e.g. `M1_V_LED_L`) in the local recording path. It previously fell back to
  the recorder type name (`Movie`). `<Recorder>` keeps resolving to the type
  name for backwards compatibility, and the output-path preview in the recorder
  settings UI now matches the actual output.

## [1.5.20] - 2026-08-03

### Added
- Render History: the MTR window now records every local recording run (start
  time, duration, recorded timelines) and its outcome — Completed, Interrupted
  (Play Mode stopped), Cancelled (Stop button), or Error (with the error note
  and the progress reached). Shown newest-first in a collapsible "Render
  History" section with a Clear button. History is stored per-machine in
  `UserSettings/MultiTimelineRecorderRenderHistory.json` (not committed to the
  repository). Runs that ended without being detected (editor crash / window
  closed) are marked Interrupted on the next run.

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