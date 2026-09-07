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
        //
        // これは「フレーム発行の瞬間に in-flight 数（ここでは未完了の AsyncGPUReadback
        // リクエスト数）を確認し、上限超過ならその場で同期的に処理完了を待ってから発行する」
        // 方式の実例（v1.5.6）。director も Play Mode も一切止めず、
        // AsyncGPUReadback.WaitAllRequests() で GPU→CPU の読み戻しキューだけを同期的に
        // ドレインする。エンコーダ入力キュー側の背圧（旧 Encoder Memory Backpressure、
        // 下記参照）を v1.5.17 で撤去した後も、この読み戻し背圧は唯一の実測で有効な機構
        // として残す（specs/mtr-nvenc-encoder/investigation.md イテレーション3）。
        public bool enableReadbackBackpressure = true;
        public int readbackDrainIntervalFrames = 1;

        // v1.5.7/v1.5.10/v1.5.13-16 に存在した「エンコーダ入力キュー（プロセス RAM）の
        // 増分監視 + 一時停止」方式（enableEncoderMemoryBackpressure 等）は v1.5.17 で
        // 完全に撤去した。Play Mode 全体 pause（v1.5.7/v1.5.10）・director 単体 pause
        // （v1.5.13-16）のいずれも「背圧を逃がす当のフレーム消費処理まで一緒に止めてしまい
        // resume が来ず恒久ハング/0秒凍結する」という同型の構造的欠陥を2世代にわたって
        // 実証した（specs/mtr-nvenc-encoder/investigation.md イテレーション2・3）。
        // 撤去の経緯・後継方針（NVENC 経路は MtrFFmpegPipe.SyncFrameData の同期待ちで
        // 既に真の in-flight 有界化ができている一方、内蔵 CoreEncoder 経路は Recorder 側に
        // キュー深度・消費進捗を取得できる公開 API が無く同等の有界化ができない既知の
        // 残課題であること）は specs/mtr-nvenc-encoder/implementation.md を参照。

        [Header("Encoder Output Stall Guard")]
        // 内蔵 CoreEncoder 経路には「未処理フレーム数」に相当する信号が公開されていない
        // ため、真の in-flight 有界化（フレーム発行を待たせて詰まりを解消する）はできない。
        // その代わりの最終安全弁として、録画中の Movie 出力ファイルが一定時間まったく
        // 成長していないかだけを監視する。増分（何フレーム分溜まっているか）の推定は
        // 行わない（ビットレートの仮定が必要になり、旧 RAM watermark 方式と同じ誤検知の
        // 温床になるため）。ここで検知するのは「エンコーダが完全に消費を止めている」と
        // 曖昧さ無く言える状態のみで、director/Play Mode は一切止めない
        // （フレーム発行は通常どおり進む。何も Pause しないという v1.5.17 の方針を維持）。
        // 「遅いが進んでいる」バックログの有界化はできないため、内蔵 CoreEncoder + 4K
        // 長尺は引き続き NVENC 経路を推奨する（判断根拠は
        // specs/mtr-nvenc-encoder/implementation.md 参照）。
        public bool enableEncoderOutputStallGuard = true;
        // レンダリング対象 Timeline 中の最初の Movie Recorder Track の出力先絶対パス。
        // MultiTimelineRecorder（Editor側）が RenderingData 構築時に解決してここへ渡す。
        // 解決できなかった場合（Movie Recorder Track が無い、ワイルドカードが残る等）は
        // 空文字列のままとし、その場合このガードは自動的に無効化される（フェイルセーフ）。
        public string expectedOutputFilePath = "";
        public int encoderStallCheckIntervalSec = 2;
        public int encoderStallTimeoutSec = 120;

        /// <summary>
        /// レンダー Timeline 上で RecorderClip が終わる時刻（秒）。0 = 不明。
        /// 停滞ガードはこの時刻を過ぎたら監視をやめる（エンコーダは閉じられ、出力ファイルが
        /// 成長しないのが正常な区間。範囲録画では再生窓がクリップより長く続き得る）。
        /// 不明なときは進捗 99% で代用する。
        /// </summary>
        public double recordingEndTime = 0;

        [Header("Runtime Status")]
        public PlayableDirector renderingDirector;
        public float progress = 0f;
        public float currentTime = 0f;
        public bool isPlaying = false;
        public bool isComplete = false;
    }
}