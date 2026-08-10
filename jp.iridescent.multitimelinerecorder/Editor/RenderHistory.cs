using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEditor;

namespace Unity.MultiTimelineRecorder
{
    /// <summary>
    /// レンダリング実行 1 回分の終了状態。
    /// </summary>
    public enum RenderHistoryStatus
    {
        /// <summary>記録中（未終了）</summary>
        Running = 0,
        /// <summary>正常完了</summary>
        Completed = 1,
        /// <summary>PlayMode 停止などによる中断</summary>
        Interrupted = 2,
        /// <summary>Stop Recording ボタンによる停止</summary>
        Cancelled = 3,
        /// <summary>エラーによる中断</summary>
        Error = 4,
    }

    /// <summary>
    /// 履歴エントリに記録する、録画に使われたレコーダー 1 件分の要約。
    /// 「あの書き出しはどの設定だったか」を後から追えるようにするためのもので、
    /// 設定の復元用ではない（表示専用のスナップショット）。
    /// </summary>
    [Serializable]
    public class RenderHistoryRecorderInfo
    {
        /// <summary>レコーダーアイテムの表示名</summary>
        public string name;
        /// <summary>Movie / Image / AOV 等（RecorderSettingsType の名前）</summary>
        public string recorderType;
        /// <summary>Movie なら MP4/WebM/MOV、Image なら PNG/JPEG/EXR</summary>
        public string format;
        /// <summary>Movie のエンコーダ種別（Movie 以外は空）</summary>
        public string encoder;
        /// <summary>実効出力解像度（"1920x1080"）</summary>
        public string resolution;
        /// <summary>GameView / TargetCamera / RenderTexture</summary>
        public string source;
        /// <summary>アルファ付きで録画したか</summary>
        public bool captureAlpha;

        /// <summary>尺範囲の指定（未指定なら空 = Timeline 全体）。例: "f120-300"</summary>
        public string range;

        /// <summary>1 行表示用の要約（"M2_KAF_Bustup: MOV/ProRes4444 1920x1080 +A f120-300"）。</summary>
        public string ToShortString()
        {
            string codec = string.IsNullOrEmpty(encoder) || encoder == "CoreEncoder"
                ? format
                : $"{format}/{encoder}";
            string alpha = captureAlpha ? " +A" : string.Empty;
            string rangeText = string.IsNullOrEmpty(range) ? string.Empty : $" {range}";
            return $"{name}: {codec} {resolution}{alpha}{rangeText}";
        }

        /// <summary>ツールチップ用の詳細（複数行）。</summary>
        public string ToDetailString()
        {
            return $"{name}\n  種別: {recorderType}\n  形式: {format}" +
                   (string.IsNullOrEmpty(encoder) ? string.Empty : $"\n  エンコーダ: {encoder}") +
                   $"\n  解像度: {resolution}\n  ソース: {source}" +
                   (captureAlpha ? "\n  アルファ: あり" : string.Empty) +
                   (string.IsNullOrEmpty(range) ? "\n  尺範囲: Timeline 全体" : $"\n  尺範囲: {range}");
        }
    }

    /// <summary>
    /// レンダリング実行 1 回分の履歴エントリ。
    /// </summary>
    [Serializable]
    public class RenderHistoryEntry
    {
        public string id;
        /// <summary>録画対象 Timeline 名のカンマ区切り（表示用）</summary>
        public string timelines;
        /// <summary>
        /// この実行で使われたレコーダーの要約一覧（v1.5.28 で追加）。
        /// これより前に記録されたエントリでは空になる。
        /// </summary>
        public List<RenderHistoryRecorderInfo> recorders = new List<RenderHistoryRecorderInfo>();
        public long startedUnixMs;
        /// <summary>0 = 未終了（Running）</summary>
        public long endedUnixMs;
        /// <summary>RenderHistoryStatus を int で保持（JsonUtility の安定性のため）</summary>
        public int status;
        /// <summary>終了時点の進捗 0..1（Completed は 1）</summary>
        public float progress;
        /// <summary>中断理由・エラーメッセージ等の補足</summary>
        public string note;

        public RenderHistoryStatus Status => (RenderHistoryStatus)status;
        public DateTime StartedLocal => DateTimeOffset.FromUnixTimeMilliseconds(startedUnixMs).LocalDateTime;

        /// <summary>レコーダー一覧の 1 行表示（記録が無い旧エントリは空文字列）。</summary>
        public string RecordersSummary
        {
            get
            {
                if (recorders == null || recorders.Count == 0)
                    return string.Empty;
                return string.Join(", ", recorders.ConvertAll(r => r.ToShortString()));
            }
        }

        /// <summary>所要時間。未終了エントリは現在時刻までの経過時間を返す。</summary>
        public TimeSpan Duration
        {
            get
            {
                long end = endedUnixMs > 0 ? endedUnixMs : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                return TimeSpan.FromMilliseconds(Math.Max(0, end - startedUnixMs));
            }
        }
    }

    /// <summary>
    /// レンダリング履歴の永続ストア。
    ///
    /// 保存先は UserSettings/（プロジェクト直下・gitignore 対象）。履歴は成果物ではなく
    /// マシンローカルの作業ログなのでリポジトリに乗せない。Library/ と違い再インポートで
    /// 消えない場所を選んでいる。
    ///
    /// 録画は Edit Mode → Play Mode → Edit Mode とドメインリロードをまたぐため、
    /// 「現在の実行エントリ」の id は EditorPrefs に保持し、終了検知はウィンドウ側の
    /// 状態遷移フックから <see cref="FinalizeCurrent"/> を呼ぶ。終了検知を取り逃した
    /// エントリ（Editor クラッシュ等）は次回 <see cref="BeginRun"/> 時に Interrupted へ
    /// 掃き出す。
    /// </summary>
    public static class RenderHistory
    {
        private const string FileRelPath = "UserSettings/MultiTimelineRecorderRenderHistory.json";
        private const string CurrentIdPrefKey = "MTR_RenderHistoryCurrentId";
        private const int MaxEntries = 100;

        /// <summary>テスト専用: 保存先ファイルの差し替え（null で既定に戻す）</summary>
        internal static string fileOverrideForTests = null;

        [Serializable]
        private class RenderHistoryData
        {
            public List<RenderHistoryEntry> entries = new List<RenderHistoryEntry>();
        }

        private static RenderHistoryData cache;

        private static string FilePath => fileOverrideForTests ?? FileRelPath;

        /// <summary>全エントリ（古い順）。表示側で逆順に読むこと。</summary>
        public static IReadOnlyList<RenderHistoryEntry> Entries => Load().entries;

        /// <summary>
        /// 現在の実行に対応する未終了（Running）エントリ。無ければ null。
        /// ウォッチドッグ（取りこぼし監視）の判定に使う。
        /// </summary>
        public static RenderHistoryEntry CurrentRunningEntry
        {
            get
            {
                string currentId = EditorPrefs.GetString(CurrentIdPrefKey, string.Empty);
                if (string.IsNullOrEmpty(currentId))
                    return null;
                var entry = Load().entries.Find(e => e.id == currentId);
                return entry != null && entry.Status == RenderHistoryStatus.Running ? entry : null;
            }
        }

        /// <summary>未終了の実行エントリが残っているか。</summary>
        public static bool HasUnfinishedCurrentRun => CurrentRunningEntry != null;

        /// <summary>
        /// 録画実行の開始を記録する。前回の Running エントリが残っていれば
        /// （終了検知の取り逃し = Editor クラッシュ / ウィンドウ閉鎖等）Interrupted に確定させる。
        /// </summary>
        public static RenderHistoryEntry BeginRun(IEnumerable<string> timelineNames)
        {
            return BeginRun(timelineNames, null);
        }

        /// <summary>
        /// レコーダーの要約付きで録画実行の開始を記録する。
        /// </summary>
        public static RenderHistoryEntry BeginRun(
            IEnumerable<string> timelineNames,
            IEnumerable<RenderHistoryRecorderInfo> recorderInfos)
        {
            var data = Load();

            // 終了検知を取り逃した Running エントリの掃き出し
            foreach (var stale in data.entries)
            {
                if (stale.Status == RenderHistoryStatus.Running)
                {
                    stale.status = (int)RenderHistoryStatus.Interrupted;
                    stale.endedUnixMs = NowMs();
                    stale.note = "終了を検知できませんでした（Editor 終了 / クラッシュ等の可能性）";
                }
            }

            var entry = new RenderHistoryEntry
            {
                id = Guid.NewGuid().ToString("N"),
                timelines = string.Join(", ", timelineNames ?? Array.Empty<string>()),
                recorders = recorderInfos != null
                    ? new List<RenderHistoryRecorderInfo>(recorderInfos)
                    : new List<RenderHistoryRecorderInfo>(),
                startedUnixMs = NowMs(),
                endedUnixMs = 0,
                status = (int)RenderHistoryStatus.Running,
                progress = 0f,
                note = string.Empty,
            };
            data.entries.Add(entry);

            // 上限超過分は古い方から間引く
            if (data.entries.Count > MaxEntries)
                data.entries.RemoveRange(0, data.entries.Count - MaxEntries);

            Save(data);
            EditorPrefs.SetString(CurrentIdPrefKey, entry.id);
            return entry;
        }

        /// <summary>
        /// 現在の実行エントリを終了状態にする。冪等: 対応するエントリが無い、
        /// または既に終了済みなら何もしない（完了検知が複数経路から呼ばれるため）。
        /// </summary>
        public static void FinalizeCurrent(RenderHistoryStatus finalStatus, float progress, string note)
        {
            string currentId = EditorPrefs.GetString(CurrentIdPrefKey, string.Empty);
            if (string.IsNullOrEmpty(currentId))
                return;

            var data = Load();
            var entry = data.entries.Find(e => e.id == currentId);
            if (entry == null || entry.Status != RenderHistoryStatus.Running)
                return;

            entry.status = (int)finalStatus;
            entry.endedUnixMs = NowMs();
            entry.progress = finalStatus == RenderHistoryStatus.Completed ? 1f : Mathf.Clamp01(progress);
            entry.note = note ?? string.Empty;
            Save(data);
            EditorPrefs.DeleteKey(CurrentIdPrefKey);
        }

        /// <summary>履歴をすべて削除する。</summary>
        public static void Clear()
        {
            var data = Load();
            data.entries.Clear();
            Save(data);
            EditorPrefs.DeleteKey(CurrentIdPrefKey);
        }

        /// <summary>表示用の所要時間フォーマット（1 時間以上は h:mm:ss、未満は m:ss）。</summary>
        public static string FormatDuration(TimeSpan t)
        {
            if (t.TotalHours >= 1.0)
                return $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";
            return $"{t.Minutes}:{t.Seconds:00}";
        }

        private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        private static RenderHistoryData Load()
        {
            if (cache != null && fileOverrideForTests == null)
                return cache;

            RenderHistoryData data = null;
            try
            {
                if (File.Exists(FilePath))
                    data = JsonUtility.FromJson<RenderHistoryData>(File.ReadAllText(FilePath));
            }
            catch (Exception ex)
            {
                // 壊れた履歴ファイルは読み捨てて新規に作り直す（録画自体を止めない）
                MultiTimelineRecorderLogger.LogWarning(
                    $"[MultiTimelineRecorder] レンダリング履歴の読み込みに失敗したため初期化します: {ex.Message}");
            }

            data ??= new RenderHistoryData();
            data.entries ??= new List<RenderHistoryEntry>();
            // v1.5.28 より前に保存されたエントリには recorders が無い（null になる）
            foreach (var entry in data.entries)
                entry.recorders ??= new List<RenderHistoryRecorderInfo>();
            if (fileOverrideForTests == null)
                cache = data;
            return data;
        }

        private static void Save(RenderHistoryData data)
        {
            try
            {
                string dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(FilePath, JsonUtility.ToJson(data, true));
            }
            catch (Exception ex)
            {
                MultiTimelineRecorderLogger.LogWarning(
                    $"[MultiTimelineRecorder] レンダリング履歴の保存に失敗しました: {ex.Message}");
            }
            if (fileOverrideForTests == null)
                cache = data;
        }
    }
}
