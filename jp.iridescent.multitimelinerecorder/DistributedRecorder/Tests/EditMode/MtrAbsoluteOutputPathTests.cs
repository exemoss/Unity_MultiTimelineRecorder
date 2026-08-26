// Tests for absolute (out-of-project) output paths surviving serialization
// (fix/absolute-output-path).
// Unity Recorder の OutputPath.FromPath は Absolute ルートで m_Leaf にしか書かず、
// m_AbsolutePath(null) がシリアライズ往復で空文字になると出力先がファイル名のみに化ける。
// ConfigureOutputPath 後の設定が Instantiate(シリアライズ往復と同じ変換)を経ても
// 絶対パスを保持することを固定する。

using System.IO;
using NUnit.Framework;
using Unity.MultiTimelineRecorder;
using UnityEditor.Recorder;
using UnityEngine;

namespace DistributedRecorder.Tests
{
    [TestFixture]
    public class MtrAbsoluteOutputPathTests
    {
        private string outsideDir;

        [SetUp]
        public void SetUp()
        {
            // プロジェクト外の実在する絶対パス（ドライブ非依存でどの環境でも成立する）
            outsideDir = Path.Combine(Path.GetTempPath(), "MtrAbsPathTest").Replace('\\', '/');
            Directory.CreateDirectory(outsideDir);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(outsideDir))
                Directory.Delete(outsideDir, true);
        }

        [Test]
        public void ConfigureOutputPath_AbsoluteOutsideProject_SurvivesSerializationRoundtrip()
        {
            var settings = ScriptableObject.CreateInstance<MovieRecorderSettings>();
            MovieRecorderSettings clone = null;
            try
            {
                RecorderSettingsHelper.ConfigureOutputPath(
                    settings, outsideDir, "probe_output", RecorderSettingsType.Movie);

                // Instantiate はシリアライズ→デシリアライズの複製で、null 文字列は空文字になる
                //（PlayMode 用一時アセットの保存・読み直しと同じ変換）
                clone = Object.Instantiate(settings);

                var absolute = clone.FileNameGenerator.BuildAbsolutePath(null).Replace('\\', '/');
                StringAssert.StartsWith(outsideDir, absolute,
                    "往復後もプロジェクト外の絶対ディレクトリを指すこと（ファイル名のみに化けない）");
                StringAssert.EndsWith("probe_output.mp4", absolute);
            }
            finally
            {
                if (clone != null) Object.DestroyImmediate(clone);
                Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void ConfigureOutputPath_ProjectRelative_IsUnchanged()
        {
            var settings = ScriptableObject.CreateInstance<MovieRecorderSettings>();
            MovieRecorderSettings clone = null;
            try
            {
                RecorderSettingsHelper.ConfigureOutputPath(
                    settings, "Recordings/RecSetBatch/x", "probe_output", RecorderSettingsType.Movie);
                clone = Object.Instantiate(settings);

                var absolute = clone.FileNameGenerator.BuildAbsolutePath(null).Replace('\\', '/');
                StringAssert.Contains("/Recordings/RecSetBatch/x/", absolute,
                    "プロジェクト相対パス（Project ルート）は従来どおり");
            }
            finally
            {
                if (clone != null) Object.DestroyImmediate(clone);
                Object.DestroyImmediate(settings);
            }
        }
    }
}
