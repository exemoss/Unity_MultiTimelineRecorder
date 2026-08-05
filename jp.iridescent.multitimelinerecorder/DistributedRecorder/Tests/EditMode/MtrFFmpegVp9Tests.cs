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

        // ---- Output scaling (RT ソースの Resolution 尊重) ------------------------

        [Test]
        public void GetOptions_WithScale_InsertsSizeIntoScaleFilter()
        {
            var settings = new MtrFFmpegEncoderSettings
            {
                Format = MtrFFmpegEncoderSettings.OutputFormat.Vp9Webm,
                ScaleWidth = 1920,
                ScaleHeight = 1080,
            };
            StringAssert.Contains("scale=1920:1080:out_color_matrix=bt709", settings.GetOptions(),
                "スケーリング指定時は scale フィルタに幅高さを渡す");
        }

        [Test]
        public void GetOptions_WithoutScale_KeepsSourceSize()
        {
            var settings = new MtrFFmpegEncoderSettings
            {
                Format = MtrFFmpegEncoderSettings.OutputFormat.Vp9Webm,
            };
            StringAssert.Contains("scale=out_color_matrix=bt709", settings.GetOptions(),
                "未指定(0)ならサイズ変更なし");
        }

        [Test]
        public void GetOptions_OddScale_IsRoundedToEven()
        {
            var settings = new MtrFFmpegEncoderSettings
            {
                Format = MtrFFmpegEncoderSettings.OutputFormat.Vp9Webm,
                ScaleWidth = 1919,
                ScaleHeight = 1079,
            };
            StringAssert.Contains("scale=1918:1078:", settings.GetOptions(),
                "yuv420p 系の制約に合わせ偶数へ丸める");
        }

        [Test]
        public void EffectiveResolution_RtWithFfmpegMovie_UsesItemResolution()
        {
            var rt = new RenderTexture(3840, 2160, 0);
            try
            {
                var item = new MultiRecorderConfig.RecorderConfigItem
                {
                    recorderType = RecorderSettingsType.Movie,
                    imageSourceType = ImageRecorderSourceType.RenderTexture,
                    width = 1920,
                    height = 1080,
                    name = "Test Movie Recorder",
                    movieConfig = MakeVp9Config(),
                };
                item.imageRenderTexture = rt;
                item.movieConfig.encoderType = MovieEncoderType.FFmpegVp9;

                item.GetEffectiveOutputResolution(out int w, out int h);
                Assert.AreEqual(1920, w, "FFmpeg エンコーダはスケーリングにより Resolution 指定が出力解像度になる");
                Assert.AreEqual(1080, h);

                // 内蔵 CoreEncoder は従来どおり RT 実寸
                item.movieConfig.encoderType = MovieEncoderType.CoreEncoder;
                item.GetEffectiveOutputResolution(out w, out h);
                Assert.AreEqual(3840, w);
                Assert.AreEqual(2160, h);
            }
            finally { Object.DestroyImmediate(rt); }
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
        public void Validate_Vp9WithAlpha_IsValid()
        {
            // バストアップ素材等の透過 WebM 用途(v1.5.22 で誤って弾いていた回帰の防止)
            var config = MakeVp9Config();
            config.captureAlpha = true;
            bool valid = config.Validate(out string error);
            Assert.IsTrue(valid, $"VP9(WebM) はアルファ対応。Error: {error}");
        }

        [Test]
        public void Validate_NvencWithAlpha_IsRejected()
        {
            var config = new MovieRecorderSettingsConfig
            {
                outputFormat = MovieRecorderSettings.VideoRecorderOutputFormat.WebM,
                encoderType = MovieEncoderType.FFmpegNvencH264,
                ffmpegPath = fakeFfmpegPath,
                width = 1920,
                height = 1080,
                frameRate = 30,
                captureAlpha = true,
            };
            Assert.IsFalse(config.Validate(out _), "NVENC はアルファ非対応のまま");
        }

        [Test]
        public void Vp9Alpha_UsesRgbaInputAndYuva420p()
        {
            var settings = new MtrFFmpegEncoderSettings
            {
                Format = MtrFFmpegEncoderSettings.OutputFormat.Vp9Webm,
            };

            Assert.IsTrue(((IEncoderSettings)settings).CanCaptureAlpha, "VP9 はアルファ対応を宣言する");
            Assert.AreEqual("rgba", settings.GetPixelFormat(true));
            Assert.AreEqual("rgb24", settings.GetPixelFormat(false));
            StringAssert.Contains("format=yuva420p", settings.GetOptions(true),
                "アルファ付きは yuva420p でエンコードする(WebM の alpha_mode=1)");
            StringAssert.Contains("format=yuv420p", settings.GetOptions(false));

            var nvenc = new MtrFFmpegEncoderSettings
            {
                Format = MtrFFmpegEncoderSettings.OutputFormat.H264Nvenc,
            };
            Assert.IsFalse(((IEncoderSettings)nvenc).CanCaptureAlpha, "NVENC はアルファ非対応のまま");
            Assert.AreEqual("rgb24", nvenc.GetPixelFormat(true));
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
