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
