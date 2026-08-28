using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Unity.MultiTimelineRecorder
{
    /// <summary>
    /// MTR ウィンドウの「個人の作業状態」ストア (EditorPrefs、プロジェクトスコープ)。
    ///
    /// テイク番号・タイムライン選択・列幅・デバッグ表示は「いま誰がどのマシンで何を録るか」
    /// であってチーム共有の設定ではない。従来これらは共有 SO
    /// (MultiTimelineRecorderSettings.asset) に書かれていたため、録画のたびにアセットが
    /// 変更され、コミットで他人の選択・テイク番号を上書きし合っていた。
    /// 本クラス導入後、これらは EditorPrefs にのみ保存される (SO 側の同名フィールドは
    /// 既存データからの一度きりの移行にだけ使い、以後書き込まない)。
    ///
    /// キーはプロジェクトパスのハッシュでスコープする (同一マシンの複数プロジェクトで
    /// 状態が混ざらないように)。シーン別状態はさらにシーンパスのハッシュで分ける。
    /// </summary>
    internal static class MtrUserState
    {
        [Serializable]
        internal class GlobalState
        {
            public int takeNumber = 1;
            public int selectedDirectorIndex = 0;
            public List<int> selectedDirectorIndices = new List<int>();
            public float leftColumnWidth = 250f;
            public float centerColumnWidth = 250f;
            public bool debugMode = false;
            public bool showStatusSection = true;
            public bool showDebugSettings = false;
            public bool showTimingInFrames = false;
        }

        [Serializable]
        internal class TimelineTake
        {
            public int timelineIndex;
            public int takeNumber;
        }

        [Serializable]
        internal class SceneState
        {
            public List<int> selectedDirectorIndices = new List<int>();
            public int selectedDirectorIndex = 0;
            public int currentTimelineIndexForRecorder = 0;
            public List<TimelineTake> timelineTakeNumbers = new List<TimelineTake>();
        }

        private static string ProjectHash =>
            projectHash ?? (projectHash = StableHash(Application.dataPath));
        private static string projectHash;

        // GUI から毎フレーム参照される (テイク番号表示等) ため、EditorPrefs の JSON パースを
        // 都度行わないようメモ化する。書き込みは本クラス経由のみなのでキャッシュは常に一致する
        // (ドメインリロードで自然にクリアされる)
        private static GlobalState globalCache;
        private static readonly Dictionary<string, SceneState> sceneCache =
            new Dictionary<string, SceneState>();

        private static string GlobalKey => "MTR.User." + ProjectHash + ".Global";

        private static string SceneKey(string scenePath)
        {
            var id = string.IsNullOrEmpty(scenePath) ? "noscene" : StableHash(scenePath);
            return "MTR.User." + ProjectHash + ".Scene." + id;
        }

        // ── グローバル状態 ──

        /// <summary>
        /// グローバルな個人状態を読む。EditorPrefs 未保存 (導入前) なら
        /// <paramref name="migrateFrom"/> (共有 SO に残っている旧値) から初期化する。
        /// </summary>
        public static GlobalState LoadGlobal(MultiTimelineRecorderSettings migrateFrom)
        {
            if (globalCache != null) return globalCache;
            globalCache = LoadGlobalUncached(migrateFrom);
            return globalCache;
        }

        private static GlobalState LoadGlobalUncached(MultiTimelineRecorderSettings migrateFrom)
        {
            var json = EditorPrefs.GetString(GlobalKey, "");
            if (!string.IsNullOrEmpty(json))
            {
                try { return JsonUtility.FromJson<GlobalState>(json) ?? new GlobalState(); }
                catch { /* 壊れた保存値は既定へフォールバック */ }
            }

            var state = new GlobalState();
            if (migrateFrom != null)
            {
                state.takeNumber = migrateFrom.takeNumber;
                state.selectedDirectorIndex = migrateFrom.selectedDirectorIndex;
                state.selectedDirectorIndices = new List<int>(migrateFrom.selectedDirectorIndices);
                state.leftColumnWidth = migrateFrom.leftColumnWidth;
                state.centerColumnWidth = migrateFrom.centerColumnWidth;
                state.debugMode = migrateFrom.debugMode;
                state.showStatusSection = migrateFrom.showStatusSection;
                state.showDebugSettings = migrateFrom.showDebugSettings;
                state.showTimingInFrames = migrateFrom.showTimingInFrames;
            }
            return state;
        }

        public static void SaveGlobal(GlobalState state)
        {
            if (state == null) return;
            globalCache = state;
            EditorPrefs.SetString(GlobalKey, JsonUtility.ToJson(state));
        }

        // ── シーン別状態 (選択・テイク番号) ──

        /// <summary>
        /// シーン別の個人状態を読む。EditorPrefs 未保存 (導入前) なら共有 SO の
        /// シーン別ブロックに残っている旧値から初期化する。
        /// </summary>
        public static SceneState LoadScene(string scenePath, MultiTimelineRecorderSettings migrateFrom)
        {
            var cacheKey = scenePath ?? "";
            if (sceneCache.TryGetValue(cacheKey, out var cached)) return cached;
            var loaded = LoadSceneUncached(scenePath, migrateFrom);
            sceneCache[cacheKey] = loaded;
            return loaded;
        }

        private static SceneState LoadSceneUncached(string scenePath, MultiTimelineRecorderSettings migrateFrom)
        {
            var json = EditorPrefs.GetString(SceneKey(scenePath), "");
            if (!string.IsNullOrEmpty(json))
            {
                try { return JsonUtility.FromJson<SceneState>(json) ?? new SceneState(); }
                catch { /* 壊れた保存値は既定へフォールバック */ }
            }

            var state = new SceneState();
            var old = migrateFrom != null && !string.IsNullOrEmpty(scenePath)
                ? migrateFrom.GetSceneSettings(scenePath)
                : null;
            if (old != null)
            {
                state.selectedDirectorIndices = new List<int>(old.selectedDirectorIndices);
                state.selectedDirectorIndex = old.selectedDirectorIndex;
                state.currentTimelineIndexForRecorder = old.currentTimelineIndexForRecorder;
                foreach (var entry in old.timelineTakeNumbers)
                {
                    state.timelineTakeNumbers.Add(new TimelineTake
                    {
                        timelineIndex = entry.timelineIndex,
                        takeNumber = entry.takeNumber,
                    });
                }
            }
            else if (migrateFrom != null)
            {
                // シーン別ブロックが無い旧データはグローバルのテイク一覧が実質の状態だった
                foreach (var entry in migrateFrom.timelineTakeNumbers)
                {
                    state.timelineTakeNumbers.Add(new TimelineTake
                    {
                        timelineIndex = entry.timelineIndex,
                        takeNumber = entry.takeNumber,
                    });
                }
            }
            return state;
        }

        public static void SaveScene(string scenePath, SceneState state)
        {
            if (state == null) return;
            sceneCache[scenePath ?? ""] = state;
            EditorPrefs.SetString(SceneKey(scenePath), JsonUtility.ToJson(state));
        }

        // ── テイク番号 (アクティブシーン単位のヘルパ) ──

        public static int GetTimelineTake(string scenePath, int timelineIndex,
            MultiTimelineRecorderSettings migrateFrom)
        {
            var scene = LoadScene(scenePath, migrateFrom);
            var entry = scene.timelineTakeNumbers.Find(e => e.timelineIndex == timelineIndex);
            if (entry != null) return entry.takeNumber;
            return LoadGlobal(migrateFrom).takeNumber;
        }

        public static void SetTimelineTake(string scenePath, int timelineIndex, int take,
            MultiTimelineRecorderSettings migrateFrom)
        {
            var scene = LoadScene(scenePath, migrateFrom);
            var entry = scene.timelineTakeNumbers.Find(e => e.timelineIndex == timelineIndex);
            if (entry != null)
            {
                entry.takeNumber = take;
            }
            else
            {
                scene.timelineTakeNumbers.Add(new TimelineTake
                {
                    timelineIndex = timelineIndex,
                    takeNumber = take,
                });
            }
            SaveScene(scenePath, scene);
        }

        /// <summary>プロジェクトパス・シーンパスを EditorPrefs キーに使える短い安定ハッシュへ。</summary>
        private static string StableHash(string text)
        {
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                var bytes = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(text ?? ""));
                var sb = new System.Text.StringBuilder(16);
                for (int i = 0; i < 8; i++) sb.Append(bytes[i].ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
