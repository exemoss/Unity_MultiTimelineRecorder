using System;
using System.Reflection;
using UnityEngine;

namespace Unity.MultiTimelineRecorder.Utilities
{
    /// <summary>
    /// Unity Recorder（com.unity.recorder）の AudioRendererWrapper が抱える
    /// 参照カウントのリークを、録画開始前に検出・修復するガード。
    ///
    /// 背景（2026-08-31〜09-01 分散レンダリングの Worker 録画が全滅した無音バグの根因）:
    /// Recorder の AudioInput は ScriptableSingleton の AudioRendererWrapper 経由で
    /// AudioRenderer を起動し、s_StartCount == 0 のときだけ AudioRenderer.Start() を呼ぶ。
    /// ところが RecordingSession.BeginRecording は、入力群の BeginRecording
    /// （ここでカウントが ++ される）の後にエンコーダ初期化へ失敗すると
    /// CleanupFailedRecording（シェーダ設定の復元のみ）で抜け、入力の EndRecording を
    /// 呼ばない。入力側 EndRecording 中の例外や、EndRecording 前のドメインリロードでも同様。
    /// カウントは HideAndDontSave の ScriptableSingleton に載っておりドメインリロードや
    /// PlayMode 出入りを跨いで生存するため、一度リークすると以降そのエディタセッションの
    /// 全録画で AudioRenderer.Start() が二度と呼ばれず、AudioRenderer.Render() は
    /// ゼロサンプルを返し続ける = 尺・ストリームは正常なままの完全なデジタル無音になる。
    /// Editor 再起動までは自然回復しない。
    ///
    /// このガードは「録画がまだ始まっていない安全な時点」（RenderTimelineCoroutine の
    /// 冒頭 = PlayMode 突入前）でカウントを検査し、0 でなければ警告してから 0 へ戻す。
    /// Recorder パッケージの内部型のためリフレクションで触る。型・フィールドが見つからない
    /// 場合（Recorder 更新で内部が変わった場合）は一度だけ警告して何もしない。
    /// </summary>
    public static class AudioRendererLeakGuard
    {
        const string TypeName = "UnityEditor.Recorder.Input.AudioRendererWrapper, Unity.Recorder.Editor";
        const string CountFieldName = "s_StartCount";

        static bool reflectionFailureWarned;

        /// <summary>
        /// 録画開始前に呼ぶ。リークしたカウントがあれば警告して 0 に戻し、
        /// 念のため AudioRenderer.Stop() でネイティブ側も停止状態へ揃える。
        /// アクティブな録画セッションが存在しない時点でのみ呼ぶこと。
        /// </summary>
        /// <param name="context">ログ用の呼び出し元識別子。</param>
        /// <returns>リークを検出して修復したら true。</returns>
        public static bool EnsureCleanState(string context)
        {
            if (!TryGetCounter(out var wrapper, out var field))
            {
                return false;
            }

            var count = (int)field.GetValue(wrapper);
            if (count == 0)
            {
                return false;
            }

            field.SetValue(wrapper, 0);
            try
            {
                // ネイティブ側が起動しっぱなしのリークだった場合に停止へ揃える
                // （未起動なら false を返すだけで無害）
                AudioRenderer.Stop();
            }
            catch (Exception)
            {
                // ネイティブ状態と無関係にカウントの修復だけで目的は果たせている
            }

            Debug.LogWarning(
                $"[MultiTimelineRecorder] AudioRenderer の参照カウントリークを検出し修復しました" +
                $"（s_StartCount={count} → 0, context={context}）。" +
                "直前の録画セッションが AudioInput を閉じずに終了しています（録画開始失敗・" +
                "録画中の例外/リロードなど）。放置するとこのエディタセッションの以降の録画音声が" +
                "すべて無音になります（今回この修復により回避）。");
            return true;
        }

        /// <summary>
        /// 現在のカウント値。リフレクションが使えない場合は -1
        /// （テスト・診断用。-1 は Recorder パッケージ内部変更のシグナル）。
        /// </summary>
        public static int GetStartCount()
        {
            if (!TryGetCounter(out var wrapper, out var field))
            {
                return -1;
            }

            return (int)field.GetValue(wrapper);
        }

        /// <summary>テスト用: カウントを強制設定する（リーク状態の再現用）。</summary>
        internal static bool SetStartCountForTest(int value)
        {
            if (!TryGetCounter(out var wrapper, out var field))
            {
                return false;
            }

            field.SetValue(wrapper, value);
            return true;
        }

        static bool TryGetCounter(out ScriptableObject wrapper, out FieldInfo field)
        {
            wrapper = null;
            field = null;

            var type = Type.GetType(TypeName);
            if (type != null)
            {
                // ScriptableSingleton<T>.instance（基底クラスの static プロパティ）
                var instanceProp = type.GetProperty(
                    "instance",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.FlattenHierarchy);
                field = type.GetField(
                    CountFieldName,
                    BindingFlags.Instance | BindingFlags.NonPublic);

                if (instanceProp != null && field != null)
                {
                    wrapper = instanceProp.GetValue(null) as ScriptableObject;
                }
            }

            if (wrapper == null || field == null)
            {
                if (!reflectionFailureWarned)
                {
                    reflectionFailureWarned = true;
                    Debug.LogWarning(
                        "[MultiTimelineRecorder] AudioRendererLeakGuard: Recorder 内部の " +
                        $"{TypeName} / {CountFieldName} を解決できませんでした。" +
                        "com.unity.recorder の内部構造が変わった可能性があります。" +
                        "無音リークの自動修復は無効です（録画自体は続行します）。");
                }

                return false;
            }

            return true;
        }
    }
}
