// Derived from Unity Technologies' Unity Recorder package sample
// "Custom Encoder: FFmpeg" (com.unity.recorder, Samples~/FFmpegCommandLineEncoder/
// FFmpegEncoderSettingsPropertyDrawer.cs), licensed under the Unity Companion License.
// See NOTICE.md in this folder for the full attribution and the list of
// modifications made when porting this into MTR (mtr-nvenc-encoder).
//
// NOTE: MTR does not currently expose MovieRecorderSettings directly in an
// Inspector (it builds settings transiently at record time and draws its own
// UI in MovieRecorderEditor.cs / RecorderConfigEditorWindow.cs instead), so
// this drawer is not exercised by MTR's own recording path. It is kept for
// parity with the sample so that MtrFFmpegEncoderSettings also works correctly
// if a project assigns it to a MovieRecorderSettings asset directly via the
// stock Recorder window UI (see plan.md 案1 メリット: 「MTR を使う他プロジェクトでも
// そのまま再利用できる」).
using UnityEditor;
using UnityEngine;
using static Unity.MultiTimelineRecorder.Encoders.MtrFFmpegEncoderSettings;

namespace Unity.MultiTimelineRecorder.Encoders
{
    [CustomPropertyDrawer(typeof(MtrFFmpegEncoderSettings))]
    class MtrFFmpegEncoderSettingsPropertyDrawer : PropertyDrawer
    {
        static class Styles
        {
            internal static readonly GUIContent FormatLabel = new("Codec format", "NVENC のコーデック種別。");
            internal static readonly GUIContent FfmpegPathLabel = new("ffmpeg.exe Path", "ffmpeg.exe への絶対パス。リポジトリには同梱されないため各マシンで導入したパスを指定する。");
            internal static readonly GUIContent BrowseLabel = new("...", "ffmpeg.exe を参照して選択する。");
            internal static readonly GUIContent QpLabel = new("QP", "固定量子化パラメータ。値が小さいほど高画質・大容量(目安 0-51)。目標ビットレートが0より大きい場合は無視される。");
            internal static readonly GUIContent BitrateLabel = new("Target Bitrate (kbps)", "目標ビットレート(kbps)。0の場合はQP固定モードを使用する。");
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            return 0;
        }

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            // Using BeginProperty / EndProperty on the parent property means that
            // prefab override logic works on the entire property.
            EditorGUI.BeginProperty(position, label, property);

            // ffmpeg.exe の場所はマシン固有のため、シリアライズ値へは書き込まず
            // 解決状態の表示とこのマシン専用の指定 (EditorPrefs) のみ描画する
            var ffmpegPathProp = property.FindPropertyRelative("ffmpegPath");
            FfmpegLocatorGUI.Draw(ffmpegPathProp.stringValue);

            var format = property.FindPropertyRelative("outputFormat");
            format.intValue = (int)(OutputFormat)EditorGUILayout.EnumPopup(Styles.FormatLabel, (OutputFormat)format.intValue);

            var bitrateProp = property.FindPropertyRelative("bitrateKbps");
            bitrateProp.intValue = EditorGUILayout.IntField(Styles.BitrateLabel, bitrateProp.intValue);

            using (new EditorGUI.DisabledScope(bitrateProp.intValue > 0))
            {
                var qpProp = property.FindPropertyRelative("qp");
                qpProp.intValue = EditorGUILayout.IntSlider(Styles.QpLabel, qpProp.intValue, 0, 51);
            }

            EditorGUI.EndProperty();
        }
    }
}
