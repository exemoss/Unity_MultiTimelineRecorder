# Changelog
All notable changes to Unity Multi Timeline Recorder will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
Version numbers follow the rules in [VERSIONING.md](../VERSIONING.md): MAJOR when
existing settings produce different output, MINOR for features that leave output
unchanged, PATCH for fixes.

## [1.6.0-rc.1] - 2026-08-06

Release candidate: the distributed-rendering job manifest needs verification
across Master and Worker machines before 1.6.0 is finalized.

### Added
- Job manifest for distributed rendering: export the dispatch plan to a JSON
  manifest and import it back (`JobManifest` DTO + `JobManifestIO` with
  read/write/validation), wired into the Distributed section of the MTR window.
- `JobDispatcher` accepts an optional commit override, so a run can be pinned to
  a specific project commit instead of the Worker's current checkout.

### Fixed
- Dispatcher now distinguishes "no commit override" from an explicit empty
  override (the latter previously leaked the Master's HEAD to Workers).

## [1.5.28] - 2026-08-05

### Added
- Render History now records the recorders used for each run, not just the
  timelines: name, recorder type, format, encoder, effective resolution,
  source, and whether alpha was captured. Each history row shows a summary
  line (e.g. "↳ M2_KAF_Bustup: MOV/FFmpegProRes4444 1920x1080 +A") with the
  full per-recorder details in its tooltip. Entries recorded before 1.5.28
  keep working and simply show no recorder line.

## [1.5.27] - 2026-08-05

### Added
- "自動検出" button next to FFmpeg Path: finds ffmpeg.exe from PATH, WinGet
  (Links and package dirs), Chocolatey, Scoop, or C:\ffmpeg\bin (plus
  Homebrew/system paths on macOS/Linux) and fills the field.
- FFmpeg ProRes 422 HQ (MOV) encoder (`MovieEncoderType.FFmpegProRes422Hq`,
  prores_ks profile hq, 10-bit 4:2:2, no alpha — validation points to
  ProRes 4444 when Capture Alpha is on). Same BT.709 tagging and Resolution
  scaling as the other FFmpeg encoders.
- RenderTexture-source Resolution scaling now works for ALL recording paths:
  image sequences (PNG/JPEG/EXR) and built-in Core Encoder movies record a
  scaled proxy RT (created as a temp asset, blitted from the source RT every
  frame at end of URP frame rendering, cleaned up after recording). FFmpeg
  paths keep using ffmpeg's scale filter. Effective-resolution validation
  now always honors the item Resolution for RT sources.

## [1.5.26] - 2026-08-05

### Added
- FFmpeg ProRes 4444 (MOV) encoder (`MovieEncoderType.FFmpegProRes4444`,
  prores_ks): natively readable by Premiere / AE, supports Capture Alpha
  (rgba → yuva444p10le), BT.709 conversion/tagging, and — unlike the built-in
  Core Encoder MOV path — output scaling to the item Resolution for
  RenderTexture sources. Software encoding but much faster than VP9; quality
  uses the profile default (QP / bitrate are not used). Audio is AAC-in-MOV.

## [1.5.25] - 2026-08-05

### Fixed
- RenderTexture-source movie items recorded with an FFmpeg encoder (VP9 /
  NVENC) now honor the item's Resolution setting: the output is scaled to it
  by ffmpeg (lanczos, rounded to even; no scaling when it matches the RT).
  Previously the output was always the RT's native size because Unity
  Recorder's RenderTexture input provides frames at RT resolution — that is
  still the case for the built-in Core Encoder, which has no scaling stage.
  Validation/preflight use the scaled size accordingly.

## [1.5.24] - 2026-08-05

### Fixed
- Recorder items whose movie configuration fails validation no longer get
  silently dropped at recording start (the run "completed" with no output and
  only a Console error). The pre-recording check now runs the full validation
  for every enabled Movie item and shows a blocking dialog listing each item
  and its error. This is what made a VP9 + Capture Alpha item record nothing
  in 1.5.22/1.5.23.

### Added
- VP9/WebM alpha support: Capture Alpha now works with the FFmpeg VP9 encoder
  (RGBA input → yuva420p, stored as WebM alpha_mode=1; verified to decode back
  to RGBA). Requires an alpha-capable source (RenderTexture / Target Camera —
  Game View has no alpha and auto-disables it, matching the shared builder).
  NVENC (H.264/HEVC) remains alpha-unsupported and is now reported as a
  pre-recording error instead of a silent skip.

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