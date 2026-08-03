# NOTICE — FFmpeg NVENC Encoder (derived from Unity Recorder sample)

This directory (`Editor/Encoders/`) contains code derived from Unity
Technologies' official **Unity Recorder** package sample **"Custom Encoder:
FFmpeg"** (`com.unity.recorder`, `Samples~/FFmpegCommandLineEncoder/`),
licensed under the **Unity Companion License**
(https://unity3d.com/legal/licenses/unity_companion_license). See
`Library/PackageCache/com.unity.recorder@*/LICENSE.md` in a project with the
Recorder package installed for the full license text. Unity Recorder package
copyright © Unity Technologies.

Original sample files (Unity Technologies):

- `FFmpegEncoder.cs`
- `FFmpegEncoderSettings.cs`
- `FFmpegEncoderSettingsPropertyDrawer.cs`
- `FFmpegPipe.cs`

Ported into this fork (`jp.iridescent.multitimelinerecorder`) as:

- `MtrFFmpegEncoder.cs`
- `MtrFFmpegEncoderSettings.cs`
- `MtrFFmpegEncoderSettingsPropertyDrawer.cs`
- `MtrFFmpegPipe.cs`

## Modifications made when porting (specs/mtr-nvenc-encoder, 2026-07-17)

- Renamed all types (`FFmpeg*` → `MtrFFmpeg*`) and moved them into namespace
  `Unity.MultiTimelineRecorder.Encoders` so they do not collide with the
  original sample's types if a consuming project also imports the stock
  Recorder sample into its own `Assets/` folder.
- `MtrFFmpegEncoderSettings.FfmpegPath` gained a public setter. The original
  sample's `ffmpegPath` field only had a private `[SerializeField]` with no
  public setter (it could only be edited through its own
  `PropertyDrawer`/Inspector UI). MTR needs to assign this value
  programmatically from `MovieRecorderSettingsConfig.ApplyToSettings()`, since
  MTR builds `MovieRecorderSettings` (and their `EncoderSettings`) at
  render time rather than storing them as persisted Inspector-edited assets.
- `MtrFFmpegEncoderSettings.OutputFormat` was reduced from the sample's full
  codec list (software H.264, ProRes ×6, VP8, VP9, plus NVENC H.264/HEVC) to
  just the two NVENC codecs (`H264Nvenc` / `HevcNvenc`) that this fork's
  initial scope requires (see `specs/mtr-nvenc-encoder/plan.md`, 案1 and the
  "ユーザー決定" section: AV1/software codecs are explicitly out of scope for
  now). `Qp` and `BitrateKbps` were added as separate, UI-adjustable fields
  (the sample hard-codes `-qp 24` in its option string).
- `MtrFFmpegPipe.SyncFrameData()` (renamed from `FFmpegPipe.SyncFrameData()`)
  and `PushFrameData()` now check the `_terminate` flag inside their wait
  loops. **Known hole in the original sample**: if the ffmpeg subprocess dies
  mid-recording, `SyncFrameData()` only checked
  `_cancellationToken.IsCancellationRequested`, which is only set by
  `CloseAndGetOutput()` — meaning a mid-recording ffmpeg crash could make
  `SyncFrameData()` wait forever on a pong signal that will never arrive,
  hanging the Unity Editor's main thread. The MTR port checks `_terminate`
  directly, returns immediately, drops any further pushed frames, and logs one
  `Debug.LogError` (not a silent failure) so the console makes the failure
  visible instead of the Editor appearing to freeze.
- The audio `PushFrameData(NativeArray<float>)` overload no longer uses
  `unsafe` code (`GetUnsafePtr()` + `Buffer.MemoryCopy()`). It uses the safe
  `NativeArray<T>.Reinterpret<U>(int expectedTypeSize)` instance method
  (`data.Reinterpret<byte>(sizeof(float))`) + `NativeArray<T>.CopyFrom()`
  instead. This overload is part of `UnityEngine.CoreModule` (not the
  two-type-argument `Reinterpret<T, U>()` extension method that
  `com.unity.collections` provides), so no extra package dependency is
  required and `Unity.MultiTimelineRecorder.Editor.asmdef` can keep
  `allowUnsafeCode: false` (MTR's existing project-wide convention; the
  sample's own asmdef sets `allowUnsafeCode: true`).
- `MtrFFmpegPipe.SyncFrameData()` now bounds the *total* time it will wait
  per call with a `Stopwatch` (`_syncStallTimeoutMs`, 60 seconds), in
  addition to the existing `_terminate` check. The `_terminate` check (see
  above) only covers a dead ffmpeg subprocess; if ffmpeg is alive but has
  stalled (hung encoder/driver, GPU issue, etc.), the original loop — and
  the MTR port's own `_terminate`-aware loop — would still wait on
  `_copyPong`/`_pipePong` indefinitely. Exceeding the timeout now force-sets
  `_terminate` (via a new `LogSyncStallTimeoutAndTerminate()` helper) so the
  recording session fails safely with a clear `Debug.LogError` instead of
  hanging the Unity main thread (`specs/mtr-nvenc-encoder`, iteration 3 —
  this complements the producer-side stall/timeout added to
  `PlayModeTimelineRenderer` in the same iteration, replacing the
  `EditorApplication.isPaused`-based backpressure that could hang
  indefinitely because it also stopped the frame-consuming side).

Two other functional differences from the original sample:

- `MtrFFmpegEncoder.OpenStream()`'s `catch` clause fixes a sample bug: the
  original sample's catch block calls `_ffmpegVideoPipe.Dispose()` without
  nulling the field afterward, so if `_ffmpegAudioPipe`'s constructor also
  throws, the `finally`-less cleanup path could `Dispose()` the same
  `_ffmpegVideoPipe` instance twice. The MTR port sets each pipe field to
  `null` immediately after disposing it, so a second `Dispose()` call can
  never happen.
- `MtrFFmpegEncoderSettings.GetOptions()` applies the rate-control arguments
  (`-rc constqp -qmin/-qmax -qp`, or `-rc vbr -b:v/-maxrate/-bufsize`) to
  **both** `h264_nvenc` and `hevc_nvenc`. The original sample's `HevcNvidia`
  codec entry only set `-preset p7 -tune hq -rc-lookahead 4` with no rate
  control at all (relying on the codec's internal default), which would have
  made HEVC's QP/bitrate UI fields in this fork no-ops for HEVC. The port
  applies the same rate-control construction to both codecs so the
  UI-adjustable `Qp`/`BitrateKbps` fields behave consistently regardless of
  which NVENC codec is selected.

No other functional changes were made; the rawvideo/AAC pipe protocol, the
audio remux-on-close step, and the ffmpeg command-line construction approach
are otherwise unchanged from the original sample.
