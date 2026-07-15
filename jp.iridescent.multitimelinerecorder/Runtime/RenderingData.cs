using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Timeline;
using UnityEngine.Playables;

namespace Unity.MultiTimelineRecorder
{
    /// <summary>
    /// PlayMode内でレンダリングに必要なデータを保持するコンポーネント
    /// </summary>
    public class RenderingData : MonoBehaviour
    {
        [Header("Target")]
        public string directorName;
        public TimelineAsset renderTimeline;

        [Header("Settings")]
        public float duration;
        public int frameRate = 24;
        public int preRollFrames = 0;
        public RecorderSettingsType recorderType;

        [Header("Exclusive Root Activation")]
        // 結合レンダーTimelineの「Exclusive Root Activation Track」から収集した、
        // 各セクションの排他ルート一覧（重複除去済み）。PlayModeTimelineRenderer が
        // director.Play() を呼ぶ直前に、ここに列挙された全ルートを一時的に無効化する。
        // Refs: mtr-batch-scene-activation 案1
        public List<GameObject> exclusiveRoots = new List<GameObject>();

        [Header("Readback Backpressure")]
        // 高速GPU環境で描画がエンコーダの消費速度を上回ると、AsyncGPUReadback の
        // ステージングバッファがシステム共有メモリに際限なく積み上がり GPU デバイス
        // ロスト（クラッシュ）を起こす。一定フレームごとに描画側を待たせて滞留を
        // 上限内に抑えるための設定。
        public bool enableReadbackBackpressure = true;
        public int readbackDrainIntervalFrames = 1;

        [Header("Encoder Memory Backpressure")]
        // 上記の読み戻し背圧は GPU 共有メモリ側の滞留は防ぐが、ドレインされた
        // フレームは下流のエンコーダ入力キュー（Unity Recorder 内部実装、プロセス RAM）
        // へ引き渡されるだけで、そちらの滞留には上限が無い（実測: 約80MB/sで無制限増加、
        // 135GB到達を確認。RAM/コミット枯渇による OOM クラッシュに至る）。
        // レンダリング開始時からのプロセスメモリ増分を監視し、上限（High Watermark）を
        // 超えたら Play Mode 自体を一時停止して新規フレームの発行（PlayableGraph の評価）
        // を止め、下限（Resume Watermark）まで下がったら自動的に再開する。
        public bool enableEncoderMemoryBackpressure = true;
        public int encoderMemoryHighWatermarkMB = 2048;
        public int encoderMemoryResumeWatermarkMB = 1024;
        public int encoderMemoryPollIntervalMs = 500;

        [Header("Runtime Status")]
        public PlayableDirector renderingDirector;
        public float progress = 0f;
        public float currentTime = 0f;
        public bool isPlaying = false;
        public bool isComplete = false;
    }
}