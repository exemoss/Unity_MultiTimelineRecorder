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
  `NativeArray<T>.Reinterpret<byte>()` + `NativeArray<T>.CopyFrom()` APIs from
  `com.unity.collections` instead, so `Unity.MultiTimelineRecorder.Editor.asmdef`
  can keep `allowUnsafeCode: false` (MTR's existing project-wide convention;
  the sample's own asmdef sets `allowUnsafeCode: true`).

No other functional changes were made; the rawvideo/AAC pipe protocol, the
audio remux-on-close step, and the ffmpeg command-line construction approach
are otherwise unchanged from the original sample.
