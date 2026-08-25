using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Unity.MultiTimelineRecorder.Encoders
{
    /// <summary>
    /// ffmpeg を winget（Windows 標準のパッケージマネージャ）経由でセットアップする。
    /// 自前のダウンロード・展開コードは持たず、取得先・ハッシュ検証・更新の責務を
    /// winget 側に委ねる（本パッケージが外部ホストへ直接通信しないための設計判断）。
    /// インストール先は WinGet 管理下（%LOCALAPPDATA%\Microsoft\WinGet\...）で、
    /// <see cref="FfmpegLocator"/> が既に探索する場所のため、完了後は自動検出がそのまま拾う。
    ///
    /// インストールは非同期で走り、Editor はブロックしない（進捗バーからキャンセル可）。
    /// 完了時のパス反映は呼び出し側のコールバックで行う（このクラスは設定を書き換えない）。
    /// </summary>
    public static class FfmpegInstaller
    {
        /// <summary>winget パッケージ ID。既存の手動導入手順（winget install Gyan.FFmpeg）と同一。</summary>
        const string PackageId = "Gyan.FFmpeg";

        /// <summary>
        /// インストールする ffmpeg のバージョン（最新に追従せず固定する）。
        /// ffmpeg 8.1 以降 / 9.0 は NVENC API 13.1（NVIDIA ドライバ 610 以降）を要求し、
        /// それ未満のドライバでは NVENC が全滅する（8bit H.264 含む。RTX 4070 Ti +
        /// ドライバ 591.86 で実測）。8.0.1 は API 13.0（ドライバ 570 台）で動作することを
        /// 同環境の実エンコードで確認済み。より古いドライバのマシンでは、ドライバ更新か
        /// 手動での旧版導入（FFmpeg Path 欄で指定）で対応する。
        /// </summary>
        const string PinnedVersion = "8.0.1";

        static Process running;
        static StringBuilder capturedOutput;
        static Action<string> onCompleted;
        static double startTime;
        static bool cancelRequested;

        /// <summary>インストール処理が進行中か（多重起動防止・UI のボタン無効化用）。</summary>
        public static bool IsRunning => running != null;

        /// <summary>この環境で winget が使えるか（Windows 以外・winget 未導入では false）。</summary>
        public static bool IsWingetAvailable() => ResolveWingetPath() != null;

        /// <summary>
        /// winget で ffmpeg のインストールを開始する。完了時に <paramref name="completed"/> が
        /// メインスレッドで呼ばれる（引数は検出された ffmpeg.exe の絶対パス、失敗時は null）。
        /// 既にインストール済みなら winget を起動せず即座に完了する。実行中の多重呼び出しは無視。
        /// </summary>
        public static void InstallAsync(Action<string> completed)
        {
            if (IsRunning)
            {
                Debug.LogWarning("[FfmpegInstaller] ffmpeg のセットアップは既に実行中です");
                return;
            }

            // 既に導入済みならインストール不要（「セットアップ」ボタン連打の正常系）
            var existing = FfmpegLocator.TryFindFfmpeg();
            if (!string.IsNullOrEmpty(existing))
            {
                Debug.Log($"[FfmpegInstaller] ffmpeg は導入済みです: {existing}");
                completed?.Invoke(existing);
                return;
            }

            var winget = ResolveWingetPath();
            if (winget == null)
            {
                EditorUtility.DisplayDialog("ffmpeg セットアップ",
                    "winget が見つかりません（Windows 10/11 の App Installer が必要です）。\n\n" +
                    "手動でセットアップする場合:\n" +
                    "  1. https://ffmpeg.org などから ffmpeg を導入\n" +
                    "  2. FFmpeg Path 欄でパスを指定（または PATH に追加して自動検出）",
                    "OK");
                completed?.Invoke(null);
                return;
            }

            capturedOutput = new StringBuilder();
            onCompleted = completed;
            startTime = EditorApplication.timeSinceStartup;
            cancelRequested = false;

            var psi = new ProcessStartInfo
            {
                FileName = winget,
                // --exact: ID 完全一致 / --version: NVENC ドライバ互換の固定版（PinnedVersion 参照）/
                // agreements 系: 対話プロンプトで固まらないための明示同意 /
                // --disable-interactivity: 進捗描画等の対話出力を抑制（リダイレクト先が非端末のため）
                Arguments = $"install --id {PackageId} --exact --version {PinnedVersion} " +
                            "--accept-source-agreements --accept-package-agreements --disable-interactivity",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            try
            {
                running = Process.Start(psi);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                running = null;
                FinishWith(null);
                return;
            }

            // 出力はイベントで吸い上げる（同期 ReadToEnd はバッファ満杯でデッドロックし得る）。
            // ハンドラはワーカースレッドで呼ばれるため Unity API には触れない
            running.OutputDataReceived += (_, e) => AppendOutput(e.Data);
            running.ErrorDataReceived += (_, e) => AppendOutput(e.Data);
            running.BeginOutputReadLine();
            running.BeginErrorReadLine();

            Debug.Log($"[FfmpegInstaller] winget install {PackageId} を開始しました" +
                      "（ネットワーク経由のダウンロードを含みます。進捗バーからキャンセル可能）");
            EditorApplication.update += Tick;
        }

        static void AppendOutput(string line)
        {
            if (string.IsNullOrEmpty(line))
                return;
            lock (capturedOutput)
            {
                capturedOutput.AppendLine(line);
            }
        }

        /// <summary>
        /// EditorApplication.update から毎フレーム呼ばれる進行監視。
        /// 完了率は winget から取れないため、進捗バーは経過秒数のループ表示にする。
        /// </summary>
        static void Tick()
        {
            if (running == null)
            {
                EditorApplication.update -= Tick;
                EditorUtility.ClearProgressBar();
                return;
            }

            var elapsed = EditorApplication.timeSinceStartup - startTime;
            if (!cancelRequested && EditorUtility.DisplayCancelableProgressBar(
                    "ffmpeg セットアップ",
                    $"winget install {PackageId} 実行中... ({elapsed:F0} 秒経過)",
                    Mathf.Repeat((float)elapsed / 10f, 1f)))
            {
                cancelRequested = true;
                try { running.Kill(); }
                catch (Exception) { /* 既に終了していれば無視 */ }
            }

            if (!running.HasExited)
                return;

            int exitCode;
            try { exitCode = running.ExitCode; }
            catch (Exception) { exitCode = -1; }
            running.Dispose();
            running = null;
            EditorApplication.update -= Tick;
            EditorUtility.ClearProgressBar();

            if (cancelRequested)
            {
                Debug.LogWarning("[FfmpegInstaller] ffmpeg のセットアップをキャンセルしました");
                FinishWith(null);
                return;
            }

            // 成否は winget の終了コードではなく「実際に ffmpeg.exe を検出できたか」で判定する
            // （already-installed 等のコード分岐に依存せず、目的の状態そのものを確認する）
            var found = FfmpegLocator.TryFindFfmpeg();
            if (!string.IsNullOrEmpty(found))
            {
                Debug.Log($"[FfmpegInstaller] ffmpeg をセットアップしました: {found}");
            }
            else
            {
                string tail;
                lock (capturedOutput)
                {
                    var all = capturedOutput.ToString();
                    tail = all.Length > 2000 ? all.Substring(all.Length - 2000) : all;
                }
                Debug.LogError(
                    $"[FfmpegInstaller] winget が終了コード {exitCode} で終了しましたが、" +
                    $"ffmpeg.exe を検出できませんでした。winget の出力(末尾):\n{tail}");
                EditorUtility.DisplayDialog("ffmpeg セットアップ",
                    "セットアップに失敗しました。Console のログを確認してください。\n\n" +
                    "手動でセットアップする場合はコマンドプロンプトで:\n" +
                    $"  winget install {PackageId}",
                    "OK");
            }

            FinishWith(found);
        }

        static void FinishWith(string foundPath)
        {
            var callback = onCompleted;
            onCompleted = null;
            callback?.Invoke(string.IsNullOrEmpty(foundPath) ? null : foundPath);
        }

        /// <summary>
        /// winget.exe の場所を解決する。Unity プロセスの PATH は起動時のスナップショットで
        /// 古いことがあるため、App Installer の実体パス（WindowsApps）も直接確認する。
        /// </summary>
        static string ResolveWingetPath()
        {
            if (Application.platform != RuntimePlatform.WindowsEditor)
                return null;

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(localAppData))
            {
                var direct = Path.Combine(localAppData, "Microsoft", "WindowsApps", "winget.exe");
                if (File.Exists(direct))
                    return direct;
            }

            var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var dir in pathEnv.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir))
                    continue;
                try
                {
                    var candidate = Path.Combine(dir.Trim(), "winget.exe");
                    if (File.Exists(candidate))
                        return candidate;
                }
                catch (Exception)
                {
                    // PATH 内の不正なエントリ。無視して次の候補へ
                }
            }

            return null;
        }
    }
}
