using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Playables;
using UnityEditor;
using UnityEngine.SceneManagement;

namespace Unity.MultiTimelineRecorder
{
    /// <summary>
    /// MultiTimelineRecorder用の設定データを保存するScriptableObject
    /// </summary>
    public class MultiTimelineRecorderSettings : ScriptableObject
    {
        // 基本録画設定
        public int frameRate = 24;
        public int width = 1920;
        public int height = 1080;
        public string fileName = "<Scene>_<Recorder>_<Take>";
        public OutputPathSettings globalOutputPath = new OutputPathSettings();
        public int takeNumber = 1;
        public int preRollFrames = 0;
        public string cameraTag = "MainCamera";
        public OutputResolution outputResolution = OutputResolution.HD1080p;

        // AsyncGPUReadback 滞留対策（GPU device-removed クラッシュ対策）:
        // 高速GPU x 4K長尺のようにエンコーダの消費が描画に追いつかない環境では、
        // 読み戻し要求が無制限に積み上がりクラッシュし得るため、一定フレームごとに
        // AsyncGPUReadback.WaitAllRequests() で描画側を待たせて滞留を上限内に抑える。
        public bool enableReadbackBackpressure = true;
        public int readbackDrainIntervalFrames = 1;

        // エンコーダ入力キュー滞留対策（RAM/OOM クラッシュ対策）:
        // 上記の読み戻し背圧は GPU 共有メモリ側の滞留は防ぐが、ドレインされたフレームは
        // 下流のエンコーダ入力キュー（Unity Recorder 内部実装、プロセス RAM）へ引き渡される
        // だけで、そちらの滞留には上限が無い（実測: 約80MB/sで無制限増加、135GB到達を確認）。
        // レンダリング開始時からのプロセスメモリ増分が Stall Watermark を超えたら、
        // このTimelineのDirectorだけを一時停止し（Play Mode全体は動作継続）、
        // Resume Watermarkまで下がったら自動的に再開する。下がらないまま
        // Stall Timeout秒を超えたら、ハングさせず録画を安全に中断する
        // （v1.5.13: EditorApplication.isPaused によるPlay Mode全体停止は、フレーム消費側
        // まで止めてresume不能な恒久ハングを起こすため廃止。specs/mtr-nvenc-encoder/
        // investigation.md イテレーション2）。
        public bool enableEncoderMemoryBackpressure = true;
        public int encoderMemoryHighWatermarkMB = 2048;
        public int encoderMemoryResumeWatermarkMB = 1024;
        public int encoderMemoryPollIntervalMs = 500;
        public int encoderMemoryStallTimeoutSec = 120;

        // Image Recorder設定（Single Recorder Mode用）
        public UnityEditor.Recorder.ImageRecorderSettings.ImageRecorderOutputFormat imageOutputFormat = UnityEditor.Recorder.ImageRecorderSettings.ImageRecorderOutputFormat.PNG;
        public bool imageCaptureAlpha = false;
        public int jpegQuality = 75;
        public UnityEditor.Recorder.CompressionUtility.EXRCompressionType exrCompression = UnityEditor.Recorder.CompressionUtility.EXRCompressionType.None;
        public ImageRecorderSourceType imageSourceType = ImageRecorderSourceType.GameView;
        public Camera imageTargetCamera = null;
        public RenderTexture imageRenderTexture = null;
        
        // タイムライン選択状態
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
            public List<int> selectedDirectorIndices = new List<int>();
            public int selectedDirectorIndex = 0;
            public int currentTimelineIndexForRecorder = 0;
            public List<TimelineRecorderConfigEntry> timelineRecorderConfigEntries = new List<TimelineRecorderConfigEntry>();
            public List<TimelineTakeNumberEntry> timelineTakeNumbers = new List<TimelineTakeNumberEntry>();
            public List<TimelineExclusiveRootEntry> timelineExclusiveRootOverrides = new List<TimelineExclusiveRootEntry>();

            public SceneSpecificSettings(string path, string name)
            {
                scenePath = path;
                sceneName = name;
            }
        }
        
        [SerializeField]
        private List<SceneSpecificSettings> sceneSettings = new List<SceneSpecificSettings>();
        
        // UIレイアウト設定
        public float leftColumnWidth = 250f;
        public float centerColumnWidth = 250f;
        
        // デバッグ設定
        public bool debugMode = false;
        public bool showStatusSection = true;
        public bool showDebugSettings = false;
        
        // SignalEmitter設定 (TODO-282)
        public bool useSignalEmitterTiming = false;
        public string startTimingName = "pre";
        public string endTimingName = "post";
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
        /// 特定のTimelineのTake番号を取得
        /// </summary>
        public int GetTimelineTakeNumber(int timelineIndex)
        {
            var entry = timelineTakeNumbers.Find(e => e.timelineIndex == timelineIndex);
            if (entry != null)
            {
                return entry.takeNumber;
            }
            // エントリーが存在しない場合は、グローバルのtakeNumberを返す
            return takeNumber;
        }
        
        /// <summary>
        /// 特定のTimelineのTake番号を設定
        /// </summary>
        public void SetTimelineTakeNumber(int timelineIndex, int take)
        {
            var entry = timelineTakeNumbers.Find(e => e.timelineIndex == timelineIndex);
            if (entry != null)
            {
                entry.takeNumber = take;
            }
            else
            {
                timelineTakeNumbers.Add(new TimelineTakeNumberEntry(timelineIndex, take));
            }
            Save();
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
        /// すべてのTimelineのTake番号をDictionaryとして取得
        /// </summary>
        public Dictionary<int, int> GetAllTimelineTakeNumbers()
        {
            var dict = new Dictionary<int, int>();
            foreach (var entry in timelineTakeNumbers)
            {
                dict[entry.timelineIndex] = entry.takeNumber;
            }
            return dict;
        }

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