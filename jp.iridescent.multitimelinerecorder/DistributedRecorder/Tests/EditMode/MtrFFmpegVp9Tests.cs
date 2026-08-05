// Tests for FFmpeg VP9 (WebM, BT.709) encoder support (feature/ffmpeg-vp9-webm).
// - GetOptions のコーデック/色引数の組み立て
// - コンテナ制約と設定バリデーション
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
    public class MtrFFmpegVp9Tests
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
        public void GetOptions_Vp9_UsesLibvpxWithBt709AndCrf()
        {
            var settings = new MtrFFmpegEncoderSettings
            {
                Format = MtrFFmpegEncoderSettings.OutputFormat.Vp9Webm,
                Qp = 30,
            };

            string options = settings.GetOptions();

            StringAssert.Contains("-c:v libvpx-vp9", options);
            StringAssert.Contains("-crf 30", options);
            StringAssert.Contains("-row-mt 1", options, "row-mt 無しでは 7K 幅で実用速度が出ない");
            StringAssert.Contains("out_color_matrix=bt709", options, "RGB→YUV 変換行列の BT.709 固定");
            StringAssert.Contains("color_trc=bt709", options, "BT.709 メタデータの焼き込み");
            StringAssert.Contains("-flush_packets 1", options,
                "フラッシュ強制無しでは webm がクローズまで 0 バイトのままになり、" +
                "異常終了でデータ喪失 + ストールガード誤検知を招く");
        }

        [Test]
        public void GetOptions_Vp9_BitrateModeOverridesCrf()
        {
            var settings = new MtrFFmpegEncoderSettings
            {
                Format = MtrFFmpegEncoderSettings.OutputFormat.Vp9Webm,
                Qp = 30,
                BitrateKbps = 8000,
            };

            string options = settings.GetOptions();

            StringAssert.Contains("-b:v 8000k", options);
            StringAssert.DoesNotContain("-crf", options);
        }

        [Test]
        public void GetOptions_Nvenc_IncludesBt709ColorArgs()
        {
            var settings = new MtrFFmpegEncoderSettings
            {
                Format = MtrFFmpegEncoderSettings.OutputFormat.H264Nvenc,
            };

            string options = settings.GetOptions();

            StringAssert.Contains("-c:v h264_nvenc", options);
            StringAssert.Contains("out_color_matrix=bt709", options,
                "NVENC 経路も BT.709 変換・タグ付けを共有する(従来は BT.601 行列変換・タグ無しだった)");
        }

        [Test]
        public void Extension_MatchesContainer()
        {
            IEncoderSettings vp9 = new MtrFFmpegEncoderSettings
            {
                Format = MtrFFmpegEncoderSettings.OutputFormat.Vp9Webm,
            };
            IEncoderSettings nvenc = new MtrFFmpegEncoderSettings
            {
                Format = MtrFFmpegEncoderSettings.OutputFormat.H264Nvenc,
            };

            Assert.AreEqual("webm", vp9.Extension);
            Assert.AreEqual("mp4", nvenc.Extension);
        }

        // ---- MovieRecorderSettingsConfig.Validate -------------------------------

        private MovieRecorderSettingsConfig MakeVp9Config(
            MovieRecorderSettings.VideoRecorderOutputFormat format = MovieRecorderSettings.VideoRecorderOutputFormat.WebM)
        {
            return new MovieRecorderSettingsConfig
            {
                outputFormat = format,
                encoderType = MovieEncoderType.FFmpegVp9,
                ffmpegPath = fakeFfmpegPath,
                width = 7488,
                height = 1344,
                frameRate = 30,
            };
        }

        [Test]
        public void Validate_Vp9WithWebm_IsValid()
        {
            bool valid = MakeVp9Config().Validate(out string error);
            Assert.IsTrue(valid, $"VP9 + WebM (7488x1344) は有効な組み合わせ。Error: {error}");
        }

        [Test]
        public void Validate_Vp9WithMp4_IsRejected()
        {
            bool valid = MakeVp9Config(MovieRecorderSettings.VideoRecorderOutputFormat.MP4).Validate(out string error);
            Assert.IsFalse(valid, "VP9 は WebM コンテナ専用");
        }

        [Test]
        public void Validate_NvencWithWebm_IsRejected()
        {
            var config = new MovieRecorderSettingsConfig
            {
                outputFormat = MovieRecorderSettings.VideoRecorderOutputFormat.WebM,
                encoderType = MovieEncoderType.FFmpegNvencH264,
                ffmpegPath = fakeFfmpegPath,
                width = 1920,
                height = 1080,
                frameRate = 30,
            };
            Assert.IsFalse(config.Validate(out _), "NVENC は MP4 コンテナ専用");
        }

        [Test]
        public void Validate_FfmpegWithAlpha_IsRejected()
        {
            var config = MakeVp9Config();
            config.captureAlpha = true;
            Assert.IsFalse(config.Validate(out string error), "FFmpeg 系エンコーダはアルファ非対応");
        }

        [Test]
        public void GetMaxDimension_Vp9Webm_AllowsWideLedResolution()
        {
            Assert.AreEqual(MovieRecorderSettingsConfig.MaxDimensionWebM,
                MovieRecorderSettingsConfig.GetMaxDimension(
                    MovieRecorderSettings.VideoRecorderOutputFormat.WebM, MovieEncoderType.FFmpegVp9));
        }

        // ---- ApplyToSettings mapping --------------------------------------------

        [Test]
        public void ApplyToSettings_Vp9_MapsToVp9WebmEncoderSettings()
        {
            var config = MakeVp9Config();
            var settings = ScriptableObject.CreateInstance<MovieRecorderSettings>();
            try
            {
                config.ApplyToSettings(settings);

                var encoder = settings.EncoderSettings as MtrFFmpegEncoderSettings;
                Assert.IsNotNull(encoder, "FFmpegVp9 では MtrFFmpegEncoderSettings が適用される");
                Assert.AreEqual(MtrFFmpegEncoderSettings.OutputFormat.Vp9Webm, encoder.Format);
                Assert.AreEqual("webm", ((IEncoderSettings)encoder).Extension);
            }
            finally
            {
                Object.DestroyImmediate(settings);
            }
        }
    }
}
