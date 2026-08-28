using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Playables;
using UnityEditor;
using UnityEngine.SceneManagement;

namespace Unity.MultiTimelineRecorder
{
    /// <summary>
    /// MultiTimelineRecorder用の設定データを保存するScriptableObject。
    ///
    /// 【共有と個人の境界】この SO はリポジトリで共有される「チームの録画設定」の正。
    /// テイク番号・タイムライン選択・列幅・デバッグ表示などの「個人の作業状態」と、
    /// ffmpeg.exe のパスなどの「マシン固有の値」はここに保存しない
    /// (個人状態は <see cref="MtrUserState"/> = EditorPrefs、ffmpeg は
    /// Encoders.FfmpegLocator が録画のたびにマシン内で解決)。本クラスに残っている
    /// 同名フィールドは旧データからの一度きりの移行にだけ読まれ、以後書き込まれない。
    /// </summary>
    public class MultiTimelineRecorderSettings : ScriptableObject
    {
        // 基本録画設定
        public int frameRate = 24;
        public int width = 1920;
        public int height = 1080;
        public string fileName = "<Scene>_<Recorder>_<Take>";
        public OutputPathSettings globalOutputPath = new OutputPathSettings();

        // 【移行用・書き込み禁止】既定テイク番号。実体は MtrUserState (EditorPrefs) へ移動済み
        public int takeNumber = 1;
        public int preRollFrames = 0;

        [Tooltip("ON: 録画開始時に出力先へ同名ファイルが既にある場合、_001 _002 … を付けた" +
                 "空き名へ自動リネームして上書きを防ぐ (リネーム時はコンソールへ通知)。" +
                 "OFF: 従来どおり同名ファイルを黙って上書きする")]
        public bool autoRenameOnCollision = true;

        // 音ズレ対策ギャップ（フレーム）。範囲録画で音声を録る Movie の録画開始を
        // セクション再生開始より手前に前倒しする量（AudioSafeGapPolicy 参照）。0 = 無効
        public int audioSafeGapFrames = Utilities.AudioSafeGapPolicy.DefaultGapFrames;
        public string cameraTag = "MainCamera";
        public OutputResolution outputResolution = OutputResolution.HD1080p;

        // AsyncGPUReadback 滞留対策（GPU device-removed クラッシュ対策）:
        // 高速GPU x 4K長尺のようにエンコーダの消費が描画に追いつかない環境では、
        // 読み戻し要求が無制限に積み上がりクラッシュし得るため、一定フレームごとに
        // AsyncGPUReadback.WaitAllRequests() で描画側を待たせて滞留を上限内に抑える。
        public bool enableReadbackBackpressure = true;
        public int readbackDrainIntervalFrames = 1;

        // v1.5.7/v1.5.10/v1.5.13-16 に存在した「エンコーダ入力キュー（プロセス RAM）の
        // 増分監視 + director 一時停止」方式（enableEncoderMemoryBackpressure 等）は
        // v1.5.17 で完全に撤去した。Play Mode 全体 pause・director 単体 pause のいずれも
        // 「背圧を逃がす当のフレーム消費処理まで一緒に止めてしまい resume が来ず恒久ハング/
        // 0秒凍結する」という同型の構造的欠陥を2世代にわたって実証したため
        // （specs/mtr-nvenc-encoder/investigation.md イテレーション2・3）。
        //
        // 後継（Encoder Output Stall Guard）: 内蔵 CoreEncoder には未処理フレーム数に
        // 相当する信号が公開されていないため、真の in-flight 有界化はできない。代わりに
        // 録画中の Movie 出力ファイルが一定時間まったく成長していないかだけを監視する
        // 最終安全弁。director/Play Mode は一切止めない
        // （詳細は PlayModeTimelineRenderer.cs / specs/mtr-nvenc-encoder/implementation.md）。
        public bool enableEncoderOutputStallGuard = true;
        public int encoderStallCheckIntervalSec = 2;
        public int encoderStallTimeoutSec = 120;

        // Image Recorder設定（Single Recorder Mode用）
        public UnityEditor.Recorder.ImageRecorderSettings.ImageRecorderOutputFormat imageOutputFormat = UnityEditor.Recorder.ImageRecorderSettings.ImageRecorderOutputFormat.PNG;
        public bool imageCaptureAlpha = false;
        public int jpegQuality = 75;
        public UnityEditor.Recorder.CompressionUtility.EXRCompressionType exrCompression = UnityEditor.Recorder.CompressionUtility.EXRCompressionType.None;
        public ImageRecorderSourceType imageSourceType = ImageRecorderSourceType.GameView;
        public Camera imageTargetCamera = null;
        public RenderTexture imageRenderTexture = null;
        
        // 【移行用・書き込み禁止】タイムライン選択状態。実体は MtrUserState (EditorPrefs) へ移動済み
        public int selectedDirectorIndex = 0;
        public List<int> selectedDirectorIndices = new List<int>();

        public int timelineMarginFrames = 30;
        
        // PlayableDirectorの識別情報を保存するクラス
        [Serializable]
        public class TimelineDirectorInfo
        {
            public string gameObjectName;
            public string gameObjectPath; // HierarchyPath
            public string assetName; // TimelineAssetの名前
            
            public TimelineDirectorInfo(PlayableDirector director)
            {
                if (director != null && director.gameObject != null)
                {
                    gameObjectName = director.gameObject.name;
                    gameObjectPath = GetGameObjectPath(director.gameObject);
                    assetName = director.playableAsset != null ? director.playableAsset.name : "";
                }
            }
            
            private static string GetGameObjectPath(GameObject obj)
            {
                string path = obj.name;
                Transform parent = obj.transform.parent;
                while (parent != null)
                {
                    path = parent.name + "/" + path;
                    parent = parent.parent;
                }
                return path;
            }
        }
        
        // 保存されたPlayableDirectorの識別情報リスト
        [SerializeField]
        public List<TimelineDirectorInfo> savedTimelineDirectorInfos = new List<TimelineDirectorInfo>();
        
        // 互換性のために古いフィールドも残す（後で削除可能）
        [SerializeField]
        [Obsolete("Use savedTimelineDirectorInfos instead")]
        public List<PlayableDirector> savedTimelineDirectors = new List<PlayableDirector>();
        
        // Multi-recorder設定
        [SerializeField]
        public MultiRecorderConfig multiRecorderConfig = new MultiRecorderConfig();
        
        // タイムライン固有のrecorder設定（Dictionaryは直接シリアライズできないため、別の形式で保存）
        [Serializable]
        public class TimelineRecorderConfigEntry
        {
            public int timelineIndex;
            public MultiRecorderConfig config;
            
            public TimelineRecorderConfigEntry(int index, MultiRecorderConfig cfg)
            {
                timelineIndex = index;
                config = cfg;
            }
        }
        public List<TimelineRecorderConfigEntry> timelineRecorderConfigEntries = new List<TimelineRecorderConfigEntry>();
        
        // タイムライン固有のTake番号管理
        [Serializable]
        public class TimelineTakeNumberEntry
        {
            public int timelineIndex;
            public int takeNumber;

            public TimelineTakeNumberEntry(int index, int take)
            {
                timelineIndex = index;
                takeNumber = take;
            }
        }

        // 【移行用・書き込み禁止】実体は MtrUserState (EditorPrefs) へ移動済み。
        // Get/Set/IncrementTimelineTakeNumber は MtrUserState を読み書きする
        public List<TimelineTakeNumberEntry> timelineTakeNumbers = new List<TimelineTakeNumberEntry>();

        // タイムライン固有の「排他ルート」明示上書き管理
        // Refs: mtr-batch-scene-activation 案1
        // 既定では ControlClip の排他対象は「Directorの外側プレハブインスタンスルート」を
        // 自動推定するが、命名/構造の例外セクション向けに明示上書きできるようにする。
        [Serializable]
        public class TimelineExclusiveRootEntry
        {
            public int timelineIndex;
            public GameObjectReference rootOverride;

            public TimelineExclusiveRootEntry(int index, GameObjectReference root)
            {
                timelineIndex = index;
                rootOverride = root;
            }
        }
        public List<TimelineExclusiveRootEntry> timelineExclusiveRootOverrides = new List<TimelineExclusiveRootEntry>();

        // シーンごとの設定管理
        [Serializable]
        public class SceneSpecificSettings
        {
            public string scenePath;  // シーンのフルパス（Assets/Scenes/SampleScene.unity）
            public string sceneName;  // シーン名（SampleScene）
            public List<TimelineDirectorInfo> timelineDirectorInfos = new List<TimelineDirectorInfo>();

            // 【移行用・書き込み禁止】選択状態とテイク番号は個人の作業状態のため
            // MtrUserState (EditorPrefs) へ移動済み。以下 4 つは旧データの移行にだけ読む
            public List<int> selectedDirectorIndices = new List<int>();
            public int selectedDirectorIndex = 0;
            public int currentTimelineIndexForRecorder = 0;
            public List<TimelineTakeNumberEntry> timelineTakeNumbers = new List<TimelineTakeNumberEntry>();

            public List<TimelineRecorderConfigEntry> timelineRecorderConfigEntries = new List<TimelineRecorderConfigEntry>();
            public List<TimelineExclusiveRootEntry> timelineExclusiveRootOverrides = new List<TimelineExclusiveRootEntry>();

            public SceneSpecificSettings(string path, string name)
            {
                scenePath = path;
                sceneName = name;
            }
        }
        
        [SerializeField]
        private List<SceneSpecificSettings> sceneSettings = new List<SceneSpecificSettings>();
        
        // 【移行用・書き込み禁止】UIレイアウト・デバッグ表示は個人の作業状態のため
        // MtrUserState (EditorPrefs) へ移動済み
        public float leftColumnWidth = 250f;
        public float centerColumnWidth = 250f;
        public bool debugMode = false;
        public bool showStatusSection = true;
        public bool showDebugSettings = false;

        // SignalEmitter設定 (TODO-282)
        public bool useSignalEmitterTiming = false;
        public string startTimingName = "pre";
        public string endTimingName = "post";

        // 【移行用・書き込み禁止】表示単位の好み。実体は MtrUserState (EditorPrefs) へ移動済み
        public bool showTimingInFrames = false; // false=秒数表示, true=フレーム数表示
        
        // 設定ファイルのパス
        private const string SETTINGS_PATH = "Assets/MultiTimelineRecorder/Settings/MultiTimelineRecorderSettings.asset";
        
        /// <summary>
        /// 設定をロードまたは作成
        /// </summary>
        public static MultiTimelineRecorderSettings LoadOrCreateSettings()
        {
            var settings = AssetDatabase.LoadAssetAtPath<MultiTimelineRecorderSettings>(SETTINGS_PATH);
            
            if (settings == null)
            {
                // ディレクトリの作成
                string directory = System.IO.Path.GetDirectoryName(SETTINGS_PATH);
                if (!System.IO.Directory.Exists(directory))
                {
                    System.IO.Directory.CreateDirectory(directory);
                }
                
                // 設定の作成
                settings = CreateInstance<MultiTimelineRecorderSettings>();
                AssetDatabase.CreateAsset(settings, SETTINGS_PATH);
                AssetDatabase.SaveAssets();
            }
            
            return settings;
        }
        
        /// <summary>
        /// 設定を保存
        /// </summary>
        public void Save()
        {
            EditorUtility.SetDirty(this);
            AssetDatabase.SaveAssets();
        }
        
        /// <summary>
        /// Dictionaryから変換して保存
        /// </summary>
        public void SetTimelineRecorderConfigs(Dictionary<int, MultiRecorderConfig> configs)
        {
            timelineRecorderConfigEntries.Clear();
            foreach (var kvp in configs)
            {
                timelineRecorderConfigEntries.Add(new TimelineRecorderConfigEntry(kvp.Key, kvp.Value));
            }
        }
        
        /// <summary>
        /// Dictionaryに変換して取得
        /// </summary>
        public Dictionary<int, MultiRecorderConfig> GetTimelineRecorderConfigs()
        {
            var dict = new Dictionary<int, MultiRecorderConfig>();
            foreach (var entry in timelineRecorderConfigEntries)
            {
                dict[entry.timelineIndex] = entry.config;
            }
            return dict;
        }
        
        /// <summary>
        /// 特定のTimelineのTake番号を取得 (アクティブシーン単位)。
        /// テイク番号は個人の作業状態のため MtrUserState (EditorPrefs) が正
        /// (旧データはそこへの初回移行で引き継がれる)。エントリーが無ければ
        /// グローバル既定テイク番号を返す。
        /// </summary>
        public int GetTimelineTakeNumber(int timelineIndex)
        {
            return MtrUserState.GetTimelineTake(ActiveScenePath, timelineIndex, this);
        }

        /// <summary>
        /// 特定のTimelineのTake番号を設定 (アクティブシーン単位、EditorPrefs へ保存)。
        /// 共有 SO には書き込まない (録画のたびにアセットが変更される事故防止)。
        /// </summary>
        public void SetTimelineTakeNumber(int timelineIndex, int take)
        {
            MtrUserState.SetTimelineTake(ActiveScenePath, timelineIndex, take, this);
        }

        /// <summary>
        /// 特定のTimelineのTake番号をインクリメント
        /// </summary>
        public void IncrementTimelineTakeNumber(int timelineIndex)
        {
            int currentTake = GetTimelineTakeNumber(timelineIndex);
            SetTimelineTakeNumber(timelineIndex, currentTake + 1);
        }

        /// <summary>
        /// すべてのTimelineのTake番号をDictionaryとして取得 (アクティブシーン単位)
        /// </summary>
        public Dictionary<int, int> GetAllTimelineTakeNumbers()
        {
            var dict = new Dictionary<int, int>();
            foreach (var entry in MtrUserState.LoadScene(ActiveScenePath, this).timelineTakeNumbers)
            {
                dict[entry.timelineIndex] = entry.takeNumber;
            }
            return dict;
        }

        private static string ActiveScenePath => SceneManager.GetActiveScene().path;

        /// <summary>
        /// 指定Timelineの「排他ルート」明示上書きを取得する（未設定なら null）。
        /// 呼び出し側（ControlClip生成側）は null の場合、外側プレハブインスタンス
        /// ルートを既定値として使う。
        /// </summary>
        public GameObject GetTimelineExclusiveRootOverride(int timelineIndex)
        {
            var entry = timelineExclusiveRootOverrides.Find(e => e.timelineIndex == timelineIndex);
            return entry?.rootOverride?.GameObject;
        }

        /// <summary>
        /// 指定Timelineの「排他ルート」明示上書きを設定する。null を渡すとエントリを削除し
        /// 既定推定（外側プレハブインスタンスルート）に戻す。
        /// </summary>
        public void SetTimelineExclusiveRootOverride(int timelineIndex, GameObject root)
        {
            var entry = timelineExclusiveRootOverrides.Find(e => e.timelineIndex == timelineIndex);
            if (root == null)
            {
                if (entry != null)
                {
                    timelineExclusiveRootOverrides.Remove(entry);
                }
            }
            else if (entry != null)
            {
                entry.rootOverride.GameObject = root;
            }
            else
            {
                var reference = new GameObjectReference { GameObject = root };
                timelineExclusiveRootOverrides.Add(new TimelineExclusiveRootEntry(timelineIndex, reference));
            }
            Save();
        }
        
        /// <summary>
        /// 指定されたシーンの設定を取得
        /// </summary>
        public SceneSpecificSettings GetSceneSettings(string scenePath)
        {
            return sceneSettings.Find(s => s.scenePath == scenePath);
        }
        
        /// <summary>
        /// 指定されたシーンの設定を取得または作成
        /// </summary>
        public SceneSpecificSettings GetOrCreateSceneSettings(string scenePath)
        {
            var settings = GetSceneSettings(scenePath);
            if (settings == null)
            {
                string sceneName = System.IO.Path.GetFileNameWithoutExtension(scenePath);
                settings = new SceneSpecificSettings(scenePath, sceneName);
                sceneSettings.Add(settings);
                Save();
            }
            return settings;
        }
        
        /// <summary>
        /// 現在のシーンの設定を取得
        /// </summary>
        public SceneSpecificSettings GetCurrentSceneSettings()
        {
            var scene = SceneManager.GetActiveScene();
            return GetSceneSettings(scene.path);
        }
        
        /// <summary>
        /// 現在のシーンの設定を取得または作成
        /// </summary>
        public SceneSpecificSettings GetOrCreateCurrentSceneSettings()
        {
            var scene = SceneManager.GetActiveScene();
            return GetOrCreateSceneSettings(scene.path);
        }
    }
}