using UnityEditor;
using UnityEngine;

namespace Unity.MultiTimelineRecorder.Encoders
{
    /// <summary>
    /// 「このマシンの ffmpeg」設定の共通 GUI ブロック。
    /// ffmpeg.exe の場所はマシン固有のため、共有設定 (SO) には書き込まず
    /// <see cref="FfmpegLocator.UserOverride"/> (EditorPrefs) と自動検出だけで扱う。
    /// MTR ウィンドウのマシン設定セクション・各レコーダの Encoder 設定欄から共用する。
    /// </summary>
    public static class FfmpegLocatorGUI
    {
        /// <summary>
        /// 解決状態の表示と [指定...] [自動検出に戻す] [セットアップ] ボタンを描画する。
        /// <paramref name="configuredSeed"/> は旧データに残っている設定値
        /// (実在すれば解決候補として使われる。新規に書き込まれることはない)。
        /// </summary>
        public static void Draw(string configuredSeed)
        {
            bool resolved = FfmpegLocator.IsResolved(configuredSeed);

            EditorGUILayout.BeginHorizontal();
            var icon = resolved ? "TestPassed" : "console.warnicon.sml";
            GUILayout.Label(EditorGUIUtility.IconContent(icon), GUILayout.Width(18), GUILayout.Height(18));
            EditorGUILayout.LabelField(
                new GUIContent("ffmpeg — " + FfmpegLocator.Describe(configuredSeed),
                    "録画時に使われる ffmpeg.exe。マシンごとに場所が違うため共有設定には保存されず、" +
                    "個人設定 (EditorPrefs) → 自動検出の順にこのマシン内で解決されます"),
                EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(20);
            if (GUILayout.Button(new GUIContent("指定...",
                "このマシン専用の ffmpeg.exe を選ぶ (EditorPrefs に保存。リポジトリには入りません)"),
                GUILayout.Width(64)))
            {
                var current = FfmpegLocator.Resolve(configuredSeed);
                var start = FfmpegLocator.IsResolved(configuredSeed)
                    ? System.IO.Path.GetDirectoryName(current) : "";
                var picked = EditorUtility.OpenFilePanel("ffmpeg.exe を選択", start, "exe");
                if (!string.IsNullOrEmpty(picked))
                {
                    FfmpegLocator.UserOverride = picked;
                }
            }
            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(FfmpegLocator.UserOverride)))
            {
                if (GUILayout.Button(new GUIContent("自動検出に戻す",
                    "個人設定を消して自動検出 (PATH / WinGet / Chocolatey / Scoop / C:\\ffmpeg) に任せる"),
                    GUILayout.Width(104)))
                {
                    FfmpegLocator.UserOverride = "";
                }
            }
            using (new EditorGUI.DisabledScope(FfmpegInstaller.IsRunning))
            {
                if (GUILayout.Button(new GUIContent("セットアップ",
                    "winget (Windows 標準のパッケージマネージャ) で ffmpeg をこの PC に導入します"),
                    GUILayout.Width(88)))
                {
                    // 導入先は自動検出が拾う場所なのでパスの書き込みは不要。
                    // キャッシュだけ捨てて解決し直す
                    FfmpegInstaller.InstallAsync(found =>
                    {
                        FfmpegLocator.ClearCache();
                        RepaintMtrWindows();
                    });
                }
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            if (!resolved)
            {
                EditorGUILayout.HelpBox(
                    "ffmpeg.exe がこのマシンで見つかりません。FFmpeg 系エンコーダの録画は開始できません。" +
                    "「セットアップ」で導入するか、「指定...」で場所を教えてください。",
                    MessageType.Warning);
            }
        }

        private static void RepaintMtrWindows()
        {
            // 非同期コールバックからの更新なので、MTR 系ウィンドウを明示的に再描画する
            foreach (var window in Resources.FindObjectsOfTypeAll<EditorWindow>())
            {
                if (window.GetType().Assembly == typeof(FfmpegLocatorGUI).Assembly)
                    window.Repaint();
            }
        }
    }
}
