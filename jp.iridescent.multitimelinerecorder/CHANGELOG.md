# Changelog
All notable changes to Unity Multi Timeline Recorder will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
Version numbers follow the rules in [VERSIONING.md](../VERSIONING.md): MAJOR when
existing settings produce different output, MINOR for features that leave output
unchanged, PATCH for fixes.

## [4.4.3] - 2026-08-31

### Fixed
- **HEVC mp4 now plays in QuickTime / macOS / iOS.** ffmpeg's mp4 muxer stores
  HEVC with the `hev1` sample-entry tag by default, which Apple's players refuse
  to play; only `hvc1` is supported there. All HEVC paths (8bit, 10bit,
  10bit+deband) now pass `-tag:v hvc1`. The encoded stream is bit-identical —
  only the container fourcc changes (`hev1` → `hvc1`), so the picture, editing
  compatibility, and concatenation with previously exported files are unaffected.
  Verified that the tag also survives the `-c:v copy` audio remux step.
  H.264 / VP9 / ProRes paths are unchanged.

## [4.4.2] - 2026-08-31

### Fixed
- **git-sync branch switch no longer aborts when the Worker's tree is dirty.**
  `GitInfo.TryCheckoutBranch` (git-sync-branch-switch, v4.3.0) ran
  `checkout -B <branch> origin/<branch>` without `-f`. The documented contract —
  and the Master-side UI wording — promise that the Worker's local changes are
  discarded (same destructive class as `reset --hard`), but a non-forced
  checkout ABORTS with "Your local changes ... would be overwritten by
  checkout" whenever local modifications conflict with the target branch.
  A Worker's tree is dirty by design after every dispatch (the settings-snapshot
  SOs are overwritten in place), so switching to a branch that changes those
  files always failed: the Master saw only "sync started" while the Worker
  silently stayed on the old commit. The command is now
  `checkout -f -B <branch> origin/<branch>`, which discards conflicting local
  changes and clobbers conflicting untracked files — the behavior the docs
  always claimed. New real-git integration tests
  (`GitSyncBranchSwitchCheckoutTests`, local throw-away repos, no network)
  cover the dirty-tracked-file conflict, the untracked-file conflict, and the
  clean switch; the two conflict cases fail against the pre-fix code.

## [4.4.1] - 2026-08-27

### Fixed
- **Temp-file cleanup after audio remuxing now retries and warns instead of
  failing silently.** With audio enabled, the Movie pass writes raw video to
  the output path, moves it to `<output>.tmp`, remuxes it with the `<output>.mkv`
  audio into the final mp4, then deletes both temp files. On SMB shares the
  delete can be rejected right after the remux because the server releases the
  ffmpeg file handles late; the old code tried exactly once and logged
  `IOException.Data` — an empty dictionary — so the temp files stayed behind
  with no usable message even though the final mp4 was complete (observed
  2026-08-27 on a distributed render: two of three S04 outputs kept their
  `.tmp`/`.mkv` on the share, the third did not — timing dependent). Deletion
  now retries up to 5 times at 0.5 s intervals, also catching
  `UnauthorizedAccessException` (which Windows can throw for in-use files on
  a share), and if the file still cannot be removed it logs a warning naming
  the leftover path and stating that the final output itself is complete.

## [4.4.0] - 2026-08-27

### Added
- **Silent mode for the ffmpeg installer** (`FfmpegInstaller.InstallAsync(completed,
  confirm, silent)`): with `silent = true` every modal dialog — the confirmation,
  the already-installed notice, the winget-missing guidance, and the
  completion/failure notices — is suppressed and logged to the Console instead.
  Modal dialogs block the editor main thread, which on an unattended distributed
  Worker froze the HTTP listener and the batch until someone clicked OK; silent
  mode lets a consuming project auto-install ffmpeg when a render job arrives on
  a machine that does not have it yet (observed 2026-08-27: a newly added Worker
  failed every Movie pass with "ffmpeg.exe が見つかりません"). The winget process
  itself already ran fully non-interactively; the non-modal progress bar (with
  cancel) is kept. Default behavior (`silent = false`) is unchanged.

## [4.3.2] - 2026-08-27

### Fixed
- **POST /git-sync now re-resolves UPM packages when the sync changed
  `Packages/manifest.json` or `Packages/packages-lock.json`.** The handler
  already ran `AssetDatabase.Refresh()` after `git reset --hard` (and after
  the v4.3.0 `checkout -B` branch-switch path), but Refresh does not trigger
  a Package Manager resolve — so a sync that moved a git-URL package pin
  (e.g. this package's own `#vX.Y.Z` reference) left the Worker running the
  old package version until its Editor window was manually focused (observed
  2026-08-27: `mtrVersion` stayed 4.2.0 after a sync that pinned 4.2.1,
  blocking the project-job capability gate). Both sync paths now snapshot
  the two files before the git operation and, when either changed, call
  `UnityEditor.PackageManager.Client.Resolve()` on the main thread after the
  Refresh and invalidate the `VersionChecker` caches (which now also cover
  `mtrVersion`). Syncs that do not touch the package manifests behave
  exactly as before; recording output is unchanged (hence PATCH).
- **Transient PackageManager failures no longer surface as a bogus version
  mismatch.** When the local `com.unity.recorder` resolution (offline
  `Client.List`) came back empty at dispatch time — observed right after
  Editor startup on the Master — `VersionChecker.MatchesLocal` compared the
  empty string as-is and dispatch failed with the misleading
  "Version mismatch detected: Recorder: local=, remote=5.1.6" (the F9 fix
  already kept the empty result out of the cache, but the in-flight
  comparison still used it). `MatchesLocal` now retries once
  (`InvalidateCache` + re-resolve) when the local value is empty and the
  remote reports a real version; if the retry still comes back empty it
  fails with a dedicated "Version check failed: could not resolve the local
  com.unity.recorder version …" reason instead of a mismatch.
  `JobDispatcher.ClassifyRejection` maps that reason (arriving as a Worker
  409) to `DispatchFailReason.VersionMismatch` so the UI keeps offering the
  re-dispatch path — a re-send re-runs the Worker-side resolution, which
  typically succeeds once PackageManager has recovered. The empty-vs-empty
  comparison (Recorder not installed on either side) is unchanged.
  (Authored as 4.2.2 on `feature/project-job-hook`; ships here as part of
  4.3.2 — no v4.2.2 tag exists.)

## [4.3.1] - 2026-08-27

### Fixed
- **Low-bitrate recordings no longer falsely abort with "Encoder output
  stalled".** ffmpeg holds output in its internal buffer until it fills, so a
  recording whose encoded bitrate is tiny (e.g. a mostly-black view — a
  cast-focus pass of a song where the subject only appears near the end;
  measured ~4 KB/s) left the .mp4 at its 44-byte header until process close.
  The Encoder Output Stall Guard, which watches output file size, then killed
  the healthy recording exactly at its timeout (122 s) every time — while all
  actually-delivered frames only surfaced in the file via the shutdown flush.
  All MtrFFmpeg codec paths (NVENC H.264/HEVC/HEVC 10-bit incl. deband,
  ProRes) now pass `-flush_packets 1`, forcing a flush per packet so the file
  grows continuously from the start of the recording; the VP9/WebM path
  already did this for the same reason. Verified that the emitted bytes are
  identical with and without the flag (only flush granularity changes), so
  existing settings produce the same output (PATCH).

## [4.3.0] - 2026-08-27

### Added
- **Remote branch switching via /git-sync** (git-sync-branch-switch): the
  Master can now include a `targetBranch` in `GitSyncRequest`
  (`JobDispatcher.SendGitSyncAsync(worker, targetBranch)`). The Worker
  validates the name (`GitInfo.IsValidRefName`, rejected with 400 otherwise),
  fetches it from origin and runs
  `git checkout -B <branch> origin/<branch>` — positioning the working tree
  exactly at the remote ref, so an unattended Worker can follow the Master
  across a branch change (e.g. feature branch → main after a merge) without
  anyone touching the Worker PC. Also recovers a detached-HEAD Worker onto a
  real branch. This is the one deliberate, documented exception to the
  "branch never from the request" rule: the endpoint is HMAC-authenticated,
  the value is strictly validated, and the only reachable operation is no
  more destructive than the existing fetch + reset --hard. Scene
  close/reopen modal avoidance matches the legacy sync path.
- `GitSyncBranchSupport` capability gate (minimum 4.3.0): Workers in
  [1.4.11, 4.3.0) silently ignore the unknown `targetBranch` field
  (JsonUtility) and would sync their current branch while acking success —
  callers must check the Worker's /health `mtrVersion` before sending a
  non-empty `targetBranch`. Same pattern as `ProjectJobSupport`.
- Wire compatibility: the new field is additive with an empty default —
  requests without `targetBranch` are byte-identical in behavior (hence
  MINOR; output is unchanged).

## [4.2.1] - 2026-08-27

### Fixed
- **Project jobs no longer suppress the Play Mode domain reload.** 4.2.0 kept
  `PlayModeReloadGuard` (DisableDomainReload) enabled for the whole project
  job so the Worker infrastructure survived Play Mode; but suppressing the
  reload changed what the handler recorded — editor state that the reload
  normally rebuilds leaked into Play Mode (real case: LTCGI character
  lighting went dark in the consuming project's distributed renders while
  local runs of the identical batch were correct; A/B-verified on one
  machine with the guard as the only variable). Project jobs now run with
  the reload enabled — the exact environment of a local run — and instead
  survive it: the active job (id / kind / full request) is persisted to
  `SessionState`, and `JobRunner.TryResumeProjectJob` (called from
  Bootstrap on every WorkerAutoRecovery restart) restores the store entry
  and resumes polling the re-registered handler. Handlers must keep their
  own state in reload-surviving storage and re-register via
  `[InitializeOnLoadMethod]` (the contract docs now say so). Master-side
  progress polling simply sees the Worker offline during each pass and
  recovers between passes. MTR (non-project) jobs keep using the guard,
  unchanged.

## [4.2.0] - 2026-08-27

### Added
- **Project-job hook** for the distributed recorder: a `JobRequest` whose new
  `projectJobKind` field is non-empty delegates the ENTIRE job execution —
  scene preparation, any number of Play Mode recording passes, cleanup — to a
  handler the Unity *project* registers via the new
  `DistributedRecorder.Worker.ProjectJobHandlerRegistry`
  (Start / Poll / Cancel delegates, registered from an
  `[InitializeOnLoadMethod]`). The package contributes only what it already
  owns — transport, HMAC auth, queueing, progress forwarding (`Poll` unit
  counts are surfaced as currentFrame/totalFrames), result bookkeeping, the
  N-job restart cycle and disk-quota sweep. During a project job the Worker
  keeps `PlayModeReloadGuard` enabled for the whole job so the listener, the
  runner and the handler registration survive every Play Mode entry. The
  opaque `projectJobPayloadJson` (≤ 1 MB) travels verbatim to the handler.
  Designed for project-side batch systems (e.g. a song-bundle render batch
  that must rebuild scene content on the Worker before recording).
- `WorkerHealth.mtrVersion`: Workers now report this package's own version in
  GET /health. `JobDispatcher.DispatchAsync` uses it as a hard capability gate
  for project jobs (`ProjectJobSupport.IsSupported`, minimum 4.2.0, NOT
  skippable via `skipVersionCheck`): a pre-4.2.0 Worker would silently drop
  the unknown fields (JsonUtility) and mis-run the job as a legacy MTR job.
- Wire compatibility: all new fields are additive with empty defaults —
  existing MTR / legacy jobs, old Masters and old Workers are byte-identical
  in behavior (hence MINOR).

## [4.1.1] - 2026-08-26

### Fixed
- Recording to an **absolute path outside the project** (e.g. `D:\RecSetBatch`)
  silently wrote the file into the project root instead. Unity Recorder's
  `OutputPath.FromPath` stores an absolute directory only in `m_Leaf`; the
  `m_AbsolutePath` backing field stays `null` and `GetFullPath` falls back to
  the leaf — but a `null` string becomes an **empty** string after a
  serialization roundtrip (the temp-asset reload used for Play Mode
  recording), so the fallback check (`!= null`) passed and the directory
  collapsed to the bare file name, which ffmpeg then resolved against its
  working directory (the project root). `ConfigureOutputPath` now also
  writes the directory into `m_AbsolutePath` (via reflection; the Recorder
  package itself stays unmodified) so both fields agree after the roundtrip.
  Project-relative outputs (`Recordings/...`) were never affected.

## [4.1.0] - 2026-08-26

### Added
- Opt-in **Deband** toggle for HEVC NVENC 10bit
  (`MovieRecorderSettingsConfig.ffmpegDeband` /
  `MtrFFmpegEncoderSettings.Deband`, default **off** — existing settings
  produce byte-identical output, hence MINOR): inserts ffmpeg's `deband`
  filter right after the 10-bit quantization
  (`format=yuv420p10le,deband=1thr=0.01:2thr=0.01:3thr=0.01:r=24:b=1`) to
  smooth contour-like banding in gentle dark gradients (volumetric light
  cones etc.). Sub-code dithering alone measurably does **not** survive
  NVENC's flat-block quantization (killed even at QP 0), whereas deband's
  spatial interpolation produces genuinely distinct codes and survives
  encoding. Edges and detail are preserved (thresholds act only on flats
  within ~1% luminance). File size grows roughly 10-25%. The fixed preset
  was tuned on real 7488x1344 LED content. Ignored for all other encoder
  formats. Also updates the 10bit help text that still claimed 8-bit
  input (stale since 4.0.0).

## [4.0.0] - 2026-08-26

### Changed
- **HEVC NVENC 10bit now reads frames from Unity at 16 bits per channel**
  (`GetTextureFormat` returns `RGBA64`, so the Recorder core issues a 16-bit
  `AsyncGPUReadback`, piped to ffmpeg as `rgba64le`) instead of 8-bit
  `rgb24`. With a high-precision source (e.g. a 16-bit RenderTexture),
  smooth dark gradients such as volumetric light cones keep real 10-bit
  gradation: banding visibly decreases compared to 3.x, which quantized
  every frame to 8 bits before encoding. Same settings now produce
  different (better) pixels, hence MAJOR. 8-bit sources (e.g. Game View
  capture) still yield effectively 8-bit gradation, and every other
  encoder format is byte-identical to 3.1.1. Recording speed and file
  size are essentially unchanged (measured on a 7488x1344 RT source).

## [3.1.1] - 2026-08-25

### Fixed
- The ffmpeg setup flow now gives explicit dialog feedback: a confirmation
  dialog before starting (naming the pinned version and warning about the
  download; skipped when the caller already confirmed, e.g. the auto-detect
  failure dialog), a completion dialog with the detected path, and an
  "already installed" dialog instead of silently doing nothing.
  `FfmpegInstaller.InstallAsync` gains an optional `confirm` parameter
  (default true).

## [3.1.0] - 2026-08-25

### Added
- New Movie encoder option "FFmpeg NVENC HEVC 10bit"
  (`MovieEncoderType.FFmpegNvencHevc10Bit` /
  `MtrFFmpegEncoderSettings.OutputFormat.HevcNvenc10Bit`): encodes HEVC with
  the Main10 profile (`-profile:v main10 -pix_fmt p010le`, swscale converts
  the 8-bit RGB frames to 10-bit `p010le`). Even from an 8-bit source,
  10-bit quantization visibly reduces gradient banding at similar file size
  and speed. Requires an NVENC GPU with 10-bit HEVC support (Pascal /
  GTX 10-series or later). Container, rate control (QP / target bitrate),
  BT.709 tagging, scaling and audio behavior are identical to the existing
  8-bit HEVC NVENC path, whose output is unchanged (hence MINOR).
- One-click ffmpeg setup (`FfmpegInstaller`): a "セットアップ" button next to
  the existing auto-detect button (and an offer in the auto-detect failure
  dialog) installs ffmpeg via winget (`winget install Gyan.FFmpeg`,
  cancelable progress bar, async — the editor is not blocked). Downloading
  and verification are delegated to winget; this package still contains no
  code that talks to external hosts directly. The install lands where
  `FfmpegLocator` already searches, so auto-detection picks it up
  immediately and the path is filled in on completion. The version is
  pinned to 8.0.1: ffmpeg 8.1+/9.0 builds require NVENC API 13.1 (NVIDIA
  driver 610+) and fail on all NVENC encoding with older drivers, while
  8.0.1 works from API 13.0 (driver 570 range; verified on RTX 4070 Ti /
  driver 591.86 including the new 10-bit HEVC path).

## [3.0.0] - 2026-08-19

### Changed
- **Ranged recordings with audio are no longer out of sync — and their output
  changes, hence the major bump.** Unity Recorder pulls audio a few frames
  ahead whenever an audio clip is already active at (or before) the moment
  recording starts. Full-length recordings avoid this by authoring audio
  clips slightly after the timeline head, but a Recording Range /
  SignalEmitter range starting past 0 structurally begins mid-song, so every
  such take with audio drifted. Sections containing an audio-capturing Movie
  recorder with a range starting past 0 now reserve an audio safe gap
  (default 3 frames) in front of nested playback and pull that recorder's
  clip forward to the section head, so recording starts before any nested
  audio clip activates. Compared to 2.x, the same settings now produce:
  - FFmpeg encoders (NVENC / VP9 / ProRes): the pulled-forward head
    (gap + pre-roll + lead-in / skipped lead) is trimmed frame- and
    sample-accurately before encoding, so the file is exactly the requested
    range with audio in sync (previously the audio ran ahead).
  - Built-in CoreEncoder (no trim hook): the file keeps the pulled-forward
    head as extra leading frames showing the pre-playback picture (audio in
    sync); a preflight dialog warns with the exact frame count before
    recording starts.
  - Full-length recordings and ranges starting at frame 0 are laid out
    identically to 2.1.2 — their output is unchanged.

### Added
- "Audio Sync Gap" in Global Settings (default 3 frames, 0 disables)
  controls how far the recorder start is pulled ahead of nested playback.
- Recording Range support in the headless pass:
  `DistributedWorkerBridge.StartHeadlessRender` gains an overload taking a
  `RecorderConfigItem` as the range source plus the audio safe gap, giving
  EditorWindow-free callers (e.g. the project-side RecSet batch) the same
  range semantics as the local MTR path, including the audio fix. The
  existing 5-arg overload keeps full-length behavior, so the distributed
  Worker path is unchanged (ranged distributed jobs remain unsupported).
- `MtrFFmpegEncoderSettings.HeadTrimFrames`: drops the first N video frames
  and the matching span of audio samples before they reach the ffmpeg pipes.

## [2.1.2] - 2026-08-18

### Fixed
- Audio is no longer dropped from large recordings. The FFmpeg encoder writes
  video and audio to separate files and muxes them at the end, but
  `MtrFFmpegPipe.CloseAndGetOutput` waited only 0.5 s for ffmpeg to exit
  (`_timeoutValue`, the frame-pipe ping/pong timeout) and ignored the return
  value. `Process.Close()`/`Dispose()` only release the .NET handle, so ffmpeg
  stayed alive holding the output file while it finalized the container
  (Matroska cue writing, which scales with output size). The following
  `PostProcessAudioRemuxing` checked `IsFileLocked` exactly once, saw the lock
  and returned, so the mux was skipped entirely: the delivered file contained
  video only and the audio was left behind as a stray `.mkv`. Reproduced with a
  3.85 GB VP9 7488x1344 webm; a 392 MB NVENC MP4 from the same session muxed
  fine, so the failure only shows up on long or high-resolution takes. Exit is
  now awaited with a dedicated `_exitTimeoutValue` (10 min, warning on
  overrun), and the lock check retries for up to 30 s at 250 ms intervals. When
  it still fails the error names the audio file and prints the `-c copy`
  command that recovers the take, since the audio itself is never lost.

## [2.1.1] - 2026-08-14

### Fixed
- "Capture UI" now actually captures dynamically generated overlay UI. The
  canvas scan used `FindObjectsByType`, which silently skips objects marked
  `HideFlags.DontSave` — the standard flag for runtime-generated overlay UI —
  so exactly the canvases the option was built for were never switched to
  camera space and never appeared in the output. The scan now uses
  `Resources.FindObjectsOfTypeAll` filtered to loaded-scene objects.
- "Capture UI" text no longer degenerates into solid rectangles on telephoto
  cameras. Hanging a Screen Space - Camera canvas off a camera with a
  few-degree FOV shrinks the canvas to a ~1e-5 world scale, which breaks
  TextMeshPro SDF rendering (images survive, text becomes filled boxes;
  reproduced at 2.2° FOV, fine at 60°). Captured canvases are now rendered by
  a dedicated normal-FOV camera into a transparent RT on a free unnamed layer
  (excluded from the target camera and restored afterwards), then
  alpha-composited onto the recording RT after frame rendering — the same
  end-of-frame timing ScaledRenderTextureBlitter uses. URP's camera stack was
  tried first but overlay cameras do not composite into a base camera's
  RenderTexture output (verified empirically). With no free layer the old
  direct binding is kept as a fallback.
- `CameraTargetTextureBinder` no longer binds in Edit Mode. Binding before
  entering Play Mode baked the redirected `targetTexture` into the scene's
  play-mode snapshot; after the domain reload the binder re-captured that RT
  as the "original" value, so cleanup restored the camera to a RenderTexture
  that was then deleted — leaving the camera pointing at a dead RT and its
  display (e.g. a Display 2 program monitor) black until the scene was
  rebuilt. Binders created in Edit Mode now stay inert and only the play-mode
  copy binds, so the camera's true original target is restored.

## [2.1.0] - 2026-08-06

### Added
- "Capture UI" option for Target Camera recordings. Screen Space - Overlay
  canvases draw straight to the display and never pass through a camera, so
  UI that is visible on screen was missing from the recorded file. With the
  option on, canvases on the same display as the target camera are switched
  to Screen Space - Camera for the duration of the recording (and restored
  afterwards), so they end up in the output. Off by default.

## [2.0.0] - 2026-08-06

### Fixed (breaking: output changes for existing Target Camera setups)
- "Target Camera" now actually records the camera you selected. It never did:
  `CameraInputSettings` has no way to point at a specific camera (only
  ActiveCamera / MainCamera / TaggedCamera), and the old code tried to assign a
  non-existent `Camera` property by reflection, silently failed, and recorded
  Unity Recorder's default camera instead. Any existing recorder using Target
  Camera was producing footage from a different camera than the one shown in the
  UI — hence the MAJOR bump, even though this is a fix.
- The camera is now recorded by temporarily redirecting its output into a
  managed RenderTexture and recording that. This works for cameras rendering to
  any display, so a switcher Program camera on Display 2 can finally be
  recorded. The camera's original `targetTexture` is restored when recording
  ends, and the temporary RenderTexture asset is deleted.
- Output resolution follows the recorder's Resolution setting (the RT is created
  at that size), and the existing scaling/alpha/BT.709 paths apply as usual.

> Distributed rendering (Worker) still cannot target a specific camera —
> Unity Recorder offers no API for it there. The shared builder now warns
> explicitly instead of pretending the camera was applied.

## [1.8.0] - 2026-08-06

### Added
- "Skip Before Range" for recorders with a custom range: playback now starts a
  configurable lead-in before the recorded range instead of playing the whole
  timeline up to it. The lead-in (frames or seconds, following the same unit
  toggle) is played but not recorded, so cloth/particles/VFX can settle before
  the first recorded frame — and the long unused head of the timeline is no
  longer played back at all, which is the point: waiting through it is dead
  wall-clock time.
  - The playback window is the union of what every enabled recorder on that
    timeline needs. If even one of them records the full timeline (or has no
    range), playback still starts at the head, because those frames are needed.
  - Pre-roll now anchors to the start of the resolved playback window rather
    than always to frame 0 / the SignalEmitter start.
  - Off by default, so existing configurations play and record exactly as before.

## [1.7.0] - 2026-08-06

### Added
- Per-recorder recording range: each recorder item can now record only a slice
  of its timeline instead of the whole thing ("Recording Range" section, shown
  for every recorder type). Enter the range in frames or seconds via a unit
  toggle — it is always stored as frames, so switching units never loses the
  value. Both ends are inclusive, positions are relative to the start of the
  section timeline, and a range past the end of the timeline is clamped into it.
  Off by default, so existing configurations record the full timeline as before.
  - Takes precedence over SignalEmitter timing when both are set (the
    per-recorder range is the more specific instruction).
  - Invalid ranges (end before start) are reported by the pre-recording
    validation dialog, for every recorder type — not just Movie.
  - Render History records the range (e.g. `f120-300`) alongside the other
    recorder details.

> Includes the distributed-rendering job manifest previously tagged
> `v1.6.0-rc.1`, which is still pending Master/Worker verification. It only
> affects the distributed path; local recording is unchanged.

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