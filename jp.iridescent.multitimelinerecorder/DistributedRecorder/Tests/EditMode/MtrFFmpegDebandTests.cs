// Tests for the HEVC 10bit deband option (feature/deband).
// - GetOptions の deband フィルタ挿入と量子化フォーマットの切り替え
// - 既定 OFF / 他エンコーダでは出力が変わらないこと(4.1.0 が MINOR である根拠)
// - MovieRecorderSettingsConfig の受け渡し(ApplyToSettings / Clone)

using NUnit.Framework;
using Unity.MultiTimelineRecorder;
using Unity.MultiTimelineRecorder.Encoders;
using UnityEditor.Recorder;
using UnityEngine;

namespace DistributedRecorder.Tests
{
    [TestFixture]
    public class MtrFFmpegDebandTests
    {
        [Test]
        public void GetOptions_Hevc10BitDeband_InsertsDebandAfterQuantize()
        {
            var settings = new MtrFFmpegEncoderSettings
            {
                Format = MtrFFmpegEncoderSettings.OutputFormat.HevcNvenc10Bit,
                Deband = true,
            };

            string options = settings.GetOptions();

            StringAssert.Contains(MtrFFmpegEncoderSettings.DebandFilter, options);
            StringAssert.Contains("format=yuv420p10le," + MtrFFmpegEncoderSettings.DebandFilter, options,
                "deband はセミプレーナの p010le を受けないため、量子化を planar yuv420p10le で行い直後に挿す");
            StringAssert.Contains("-pix_fmt p010le", options, "エンコーダへは従来どおり p010le で渡す(無損失変換)");
            StringAssert.Contains("-profile:v main10", options);
        }

        [Test]
        public void GetOptions_Hevc10BitDefault_IsUnchanged()
        {
            var settings = new MtrFFmpegEncoderSettings
            {
                Format = MtrFFmpegEncoderSettings.OutputFormat.HevcNvenc10Bit,
            };

            string options = settings.GetOptions();

            StringAssert.DoesNotContain("deband", options, "既定 OFF では 4.0.0 と同一出力");
            StringAssert.Contains("format=p010le", options);
        }

        [Test]
        public void GetOptions_DebandOnOtherFormats_IsIgnored()
        {
            foreach (var format in new[]
                     {
                         MtrFFmpegEncoderSettings.OutputFormat.H264Nvenc,
                         MtrFFmpegEncoderSettings.OutputFormat.HevcNvenc,
                         MtrFFmpegEncoderSettings.OutputFormat.Vp9Webm,
                         MtrFFmpegEncoderSettings.OutputFormat.ProRes4444Mov,
                         MtrFFmpegEncoderSettings.OutputFormat.ProRes422HqMov,
                     })
            {
                var settings = new MtrFFmpegEncoderSettings { Format = format, Deband = true };
                StringAssert.DoesNotContain("deband", settings.GetOptions(),
                    $"deband は HEVC 10bit 専用({format} では無視)");
            }
        }

        [Test]
        public void ApplyToSettings_MapsDebandFlag()
        {
            var config = new MovieRecorderSettingsConfig
            {
                outputFormat = MovieRecorderSettings.VideoRecorderOutputFormat.MP4,
                encoderType = MovieEncoderType.FFmpegNvencHevc10Bit,
                ffmpegDeband = true,
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
                Assert.IsTrue(encoder.Deband);
            }
            finally { Object.DestroyImmediate(settings); }
        }

        [Test]
        public void Clone_CopiesDebandFlag()
        {
            var config = new MovieRecorderSettingsConfig { ffmpegDeband = true };
            Assert.IsTrue(config.Clone().ffmpegDeband);
        }
    }
}
