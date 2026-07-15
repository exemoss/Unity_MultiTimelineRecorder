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

        [Header("Readback Backpressure")]
        // 高速GPU環境で描画がエンコーダの消費速度を上回ると、AsyncGPUReadback の
        // ステージングバッファがシステム共有メモリに際限なく積み上がり GPU デバイス
        // ロスト（クラッシュ）を起こす。一定フレームごとに描画側を待たせて滞留を
        // 上限内に抑えるための設定。
        public bool enableReadbackBackpressure = true;
        public int readbackDrainIntervalFrames = 1;

        [Header("Runtime Status")]
        public PlayableDirector renderingDirector;
        public float progress = 0f;
        public float currentTime = 0f;
        public bool isPlaying = false;
        public bool isComplete = false;
    }
}