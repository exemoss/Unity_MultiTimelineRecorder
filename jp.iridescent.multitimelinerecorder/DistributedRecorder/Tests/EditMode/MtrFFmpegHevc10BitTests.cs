// Tests for FFmpeg NVENC HEVC 10bit (Main10) encoder support (feature/hevc-10bit).
// - GetOptions の main10 プロファイル / 10bit ピクセル形式の組み立て
// - コンテナ・アルファ制約と設定バリデーション
// - encoderType → MtrFFmpegEncoderSettings.Format のマッピング

using System.IO;
using NUnit.Framework;
using Unity.MultiTimelineRecorder;
using Unity.MultiTimelineRecorder.Encoders;
using UnityEditor.Recorder;
using UnityEditor.Recorder.Encoder;
using UnityEngine;

namespace DistributedRecorder.Tests
{
    [TestFixture]
    public class MtrFFmpegHevc10BitTests
    {
        private string fakeFfmpegPath;

        [SetUp]
        public void SetUp()
        {
            // Validate は File.Exists を見るだけなので、実体はダミーファイルでよい
            fakeFfmpegPath = Path.Combine("Temp", $"fake_ffmpeg_{System.Guid.NewGuid():N}.exe");
            File.WriteAllText(fakeFfmpegPath, "dummy");
        }

        [TearDown]
        public void TearDown()
        {
            if (File.Exists(fakeFfmpegPath))
                File.Delete(fakeFfmpegPath);
        }

        // ---- GetOptions ---------------------------------------------------------

        [Test]
        public void GetOptions_Hevc10Bit_UsesMain10WithP010()
        {
            var settings = new MtrFFmpegEncoderSettings
            {
                Format = MtrFFmpegEncoderSettings.OutputFormat.HevcNvenc10Bit,
                Qp = 24,
            };

            string options = settings.GetOptions();

            StringAssert.Contains("-c:v hevc_nvenc", options);
            StringAssert.Contains("-profile:v main10", options, "10bit は Main10 プロファイル必須");
            StringAssert.Contains("-pix_fmt p010le", options, "NVENC への 10bit 入力は p010le");
            StringAssert.Contains("format=p010le", options,
                "swscale 側でも 10bit へ変換しないと 8bit のままエンコーダに渡ってしまう");
            StringAssert.Contains("out_color_matrix=bt709", options, "BT.709 変換・タグ付けは 8bit 経路と共通");
            StringAssert.Contains("-rc constqp", options, "レート制御は 8bit HEVC と同一");
        }

        [Test]
        public void GetOptions_Hevc8Bit_IsUnchanged()
        {
            var settings = new MtrFFmpegEncoderSettings
            {
                Format = MtrFFmpegEncoderSettings.OutputFormat.HevcNvenc,
            };

            string options = settings.GetOptions();

            StringAssert.Contains("-c:v hevc_nvenc", options);
            StringAssert.Contains("-pix_fmt yuv420p", options, "既存 8bit HEVC の出力は変えない");
            StringAssert.DoesNotContain("main10", options);
            StringAssert.DoesNotContain("p010le", options);
        }

        [Test]
        public void GetOptions_HevcPaths_ContainHvc1Tag()
        {
            // ffmpeg の mp4 muxer は HEVC を既定で hev1 タグで格納するが、Apple 系
            // (QuickTime / macOS / iOS)は hvc1 しか再生できない(v4.4.3)。
            // ストリームは同一でサンプルエントリの fourcc だけが変わる
            foreach (var format in new[]
                     {
                         MtrFFmpegEncoderSettings.OutputFormat.HevcNvenc,
                         MtrFFmpegEncoderSettings.OutputFormat.HevcNvenc10Bit,
                     })
            {
                var settings = new MtrFFmpegEncoderSettings { Format = format };
                StringAssert.Contains("-tag:v hvc1", settings.GetOptions(),
                    $"{format} は hvc1 タグが無いと Apple 系プレーヤーで再生できない");
            }

            var debandSettings = new MtrFFmpegEncoderSettings
            {
                Format = MtrFFmpegEncoderSettings.OutputFormat.HevcNvenc10Bit,
                Deband = true,
            };
            StringAssert.Contains("-tag:v hvc1", debandSettings.GetOptions(),
                "deband 分岐(HEVC 10bit)にも同様に付与する");

            // H.264 に hvc1 を付けると mp4 muxer がエラーになるため付かないこと
            var h264 = new MtrFFmpegEncoderSettings
            {
                Format = MtrFFmpegEncoderSettings.OutputFormat.H264Nvenc,
            };
            StringAssert.DoesNotContain("-tag:v hvc1", h264.GetOptions(),
                "hvc1 は HEVC 専用タグ");
        }

        [Test]
        public void GetOptions_AllCodecPaths_ContainFlushPackets()
        {
            // 低ビットレート出力(ほぼ黒い映像等)で mp4/mov がクローズまで 44 バイトの
            // ままになり、Encoder Output Stall Guard が「ファイルが成長しない」と誤検知して
            // 録画を中断する(v4.3.1 実事例)。-flush_packets 1 で毎パケットのフラッシュを
            // 強制する(出力バイト列は不変。VP9/WebM は既存対策で付与済み)
            foreach (var format in new[]
                     {
                         MtrFFmpegEncoderSettings.OutputFormat.H264Nvenc,
                         MtrFFmpegEncoderSettings.OutputFormat.HevcNvenc,
                         MtrFFmpegEncoderSettings.OutputFormat.HevcNvenc10Bit,
                         MtrFFmpegEncoderSettings.OutputFormat.ProRes4444Mov,
                         MtrFFmpegEncoderSettings.OutputFormat.ProRes422HqMov,
                         MtrFFmpegEncoderSettings.OutputFormat.Vp9Webm,
                     })
            {
                var settings = new MtrFFmpegEncoderSettings { Format = format };
                StringAssert.Contains("-flush_packets 1", settings.GetOptions(),
                    $"{format} の出力はフラッシュ強制が無いと停滞ガードに誤検知される");
            }

            var debandSettings = new MtrFFmpegEncoderSettings
            {
                Format = MtrFFmpegEncoderSettings.OutputFormat.HevcNvenc10Bit,
                Deband = true,
            };
            StringAssert.Contains("-flush_packets 1", debandSettings.GetOptions(),
                "deband 分岐(HEVC 10bit)にも同様に付与する");
        }

        [Test]
        public void Hevc10Bit_ExtensionAndAlpha()
        {
            IEncoderSettings settings = new MtrFFmpegEncoderSettings
            {
                Format = MtrFFmpegEncoderSettings.OutputFormat.HevcNvenc10Bit,
            };

            Assert.AreEqual("mp4", settings.Extension, "NVENC 系は MP4 コンテナ");
            Assert.IsFalse(settings.CanCaptureAlpha, "NVENC 系はアルファ非対応");
        }

        // ---- Validate / ApplyToSettings ----------------------------------------

        [Test]
        public void Validate_Hevc10BitWithMp4_IsValid()
        {
            var config = new MovieRecorderSettingsConfig
            {
                outputFormat = MovieRecorderSettings.VideoRecorderOutputFormat.MP4,
                encoderType = MovieEncoderType.FFmpegNvencHevc10Bit,
                ffmpegPath = fakeFfmpegPath,
                width = 1920,
                height = 1080,
                frameRate = 30,
            };
            bool valid = config.Validate(out string error);
            Assert.IsTrue(valid, $"HEVC 10bit + MP4 は有効。Error: {error}");
        }

        [Test]
        public void Validate_Hevc10BitWithWebm_IsRejected()
        {
            var config = new MovieRecorderSettingsConfig
            {
                outputFormat = MovieRecorderSettings.VideoRecorderOutputFormat.WebM,
                encoderType = MovieEncoderType.FFmpegNvencHevc10Bit,
                ffmpegPath = fakeFfmpegPath,
                width = 1920,
                height = 1080,
                frameRate = 30,
            };
            Assert.IsFalse(config.Validate(out _), "NVENC 系は MP4 コンテナ専用");
        }

        [Test]
        public void Validate_Hevc10BitWithAlpha_IsRejected()
        {
            var config = new MovieRecorderSettingsConfig
            {
                outputFormat = MovieRecorderSettings.VideoRecorderOutputFormat.MOV,
                encoderType = MovieEncoderType.FFmpegNvencHevc10Bit,
                ffmpegPath = fakeFfmpegPath,
                width = 1920,
                height = 1080,
                frameRate = 30,
                captureAlpha = true,
            };
            Assert.IsFalse(config.Validate(out _), "NVENC 系はアルファ非対応");
        }

        [Test]
        public void Validate_Hevc10Bit_Allows8KLikeHevc()
        {
            var config = new MovieRecorderSettingsConfig
            {
                outputFormat = MovieRecorderSettings.VideoRecorderOutputFormat.MP4,
                encoderType = MovieEncoderType.FFmpegNvencHevc10Bit,
                ffmpegPath = fakeFfmpegPath,
                width = 7680,
                height = 4320,
                frameRate = 30,
            };
            bool valid = config.Validate(out string error);
            Assert.IsTrue(valid, $"解像度上限は 8bit HEVC と同じ 8192px。Error: {error}");
        }

        [Test]
        public void ApplyToSettings_Hevc10Bit_MapsToHevcNvenc10Bit()
        {
            var config = new MovieRecorderSettingsConfig
            {
                outputFormat = MovieRecorderSettings.VideoRecorderOutputFormat.MP4,
                encoderType = MovieEncoderType.FFmpegNvencHevc10Bit,
                ffmpegPath = fakeFfmpegPath,
                width = 1920,
                height = 1080,
                frameRate = 30,
            };
            var settings = ScriptableObject.CreateInstance<MovieRecorderSettings>();
            try
            {
                config.ApplyToSettings(settings);
                var encoder = settings.EncoderSettings as MtrFFmpegEncoderSettings;
                Assert.IsNotNull(encoder);
                Assert.AreEqual(MtrFFmpegEncoderSettings.OutputFormat.HevcNvenc10Bit, encoder.Format);
            }
            finally { Object.DestroyImmediate(settings); }
        }
    }
}
