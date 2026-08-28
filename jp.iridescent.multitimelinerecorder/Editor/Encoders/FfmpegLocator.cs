using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Unity.MultiTimelineRecorder.Encoders
{
    /// <summary>
    /// このマシンにインストール済みの ffmpeg 実行ファイルを定番の場所から探し、
    /// 録画時に実際に使うパスを解決する。
    ///
    /// ffmpeg.exe の場所はマシンごとに違うため、共有される設定アセット
    /// (MultiTimelineRecorderSettings / MultiRecorderConfig) には保存しない。
    /// 誰かが自分のローカルパスをコミットすると、他の全マシンで FFmpeg 系レコーダが
    /// バリデーションに落ちる → 各自が自分のパスで上書きコミットし合う事故になる
    /// (2026-08 に実際に発生)。代わりに:
    ///   1. このマシンの個人設定 (<see cref="UserOverride"/>、EditorPrefs)
    ///   2. 設定に残っている旧パス (他マシンの値なら実在しないので自動で飛ばされる)
    ///   3. 自動検出 (<see cref="TryFindFfmpeg"/>)
    /// の順で録画のたびに解決する (<see cref="Resolve"/>)。
    /// </summary>
    public static class FfmpegLocator
    {
        // マシン単位の個人設定。ffmpeg の導入場所はプロジェクトに依らないため
        // プロジェクトスコープは付けない
        private const string UserOverrideKey = "MTR.Ffmpeg.UserPath";

        // key = 設定に書かれていた値(空文字含む) / value = 解決結果(見つからなければ空文字)。
        // GUI から毎フレーム呼ばれるためディスク走査の結果をキャッシュする
        private static readonly Dictionary<string, string> resolveCache = new Dictionary<string, string>();

        /// <summary>このマシン専用の明示パス (EditorPrefs)。未設定なら空文字。</summary>
        public static string UserOverride
        {
            get { return EditorPrefs.GetString(UserOverrideKey, ""); }
            set
            {
                EditorPrefs.SetString(UserOverrideKey, value ?? "");
                ClearCache();
            }
        }

        /// <summary>
        /// 探索結果のキャッシュを破棄する。ffmpeg を導入・移動した直後に呼ぶ
        /// (FfmpegInstaller の完了コールバック等)。
        /// </summary>
        public static void ClearCache()
        {
            resolveCache.Clear();
        }

        /// <summary>
        /// 録画に使う ffmpeg.exe のパスを解決する。
        /// 個人設定 → 設定値 (実在する場合のみ) → 自動検出 の順。
        /// どこにも見つからなければ <paramref name="configured"/> をそのまま返す
        /// (バリデーション側に「見つからない」と報告させるため)。
        /// </summary>
        public static string Resolve(string configured)
        {
            var key = configured ?? "";
            if (resolveCache.TryGetValue(key, out var hit))
            {
                return string.IsNullOrEmpty(hit) ? key : hit;
            }

            hit = ResolveUncached(key) ?? "";
            resolveCache[key] = hit;
            return string.IsNullOrEmpty(hit) ? key : hit;
        }

        /// <summary>解決済みの ffmpeg.exe が実在するか (UI の状態表示・事前チェック用)。</summary>
        public static bool IsResolved(string configured)
        {
            return ExistsSafe(Resolve(configured));
        }

        /// <summary>いま解決される場所と、その理由 (UI・ログ用)。</summary>
        public static string Describe(string configured)
        {
            var resolved = Resolve(configured);
            if (!ExistsSafe(resolved))
            {
                return "ffmpeg.exe が見つかりません (個人設定・PATH・WinGet いずれも無し)";
            }
            var via = ExistsSafe(UserOverride) && PathEquals(resolved, UserOverride) ? "個人設定"
                    : PathEquals(resolved, configured) ? "設定値"
                    : "自動検出";
            return via + ": " + resolved;
        }

        private static string ResolveUncached(string configured)
        {
            var user = UserOverride;
            if (ExistsSafe(user)) return user;
            if (ExistsSafe(configured)) return configured;
            return TryFindFfmpeg();
        }

        private static bool ExistsSafe(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            try { return File.Exists(path); }
            catch { return false; }   // 権限やネットワークドライブで例外になることがある
        }

        private static bool PathEquals(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try
            {
                return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>
        /// ffmpeg 実行ファイルの絶対パスを返す。見つからなければ null。
        /// 探索順: PATH → WinGet Links → WinGet パッケージ実体 → Chocolatey →
        /// Scoop → C:\ffmpeg\bin → macOS/Linux の定番。
        /// </summary>
        public static string TryFindFfmpeg()
        {
            foreach (var candidate in EnumerateCandidates())
            {
                try
                {
                    if (!string.IsNullOrEmpty(candidate) && File.Exists(candidate))
                        return Path.GetFullPath(candidate);
                }
                catch
                {
                    // PATH 内の不正なエントリ等。無視して次の候補へ
                }
            }
            return null;
        }

        private static IEnumerable<string> EnumerateCandidates()
        {
            bool isWindows = Application.platform == RuntimePlatform.WindowsEditor;
            string exeName = isWindows ? "ffmpeg.exe" : "ffmpeg";

            // 1) PATH 環境変数
            string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var dir in pathEnv.Split(Path.PathSeparator))
            {
                if (!string.IsNullOrWhiteSpace(dir))
                    yield return CombineSafe(dir.Trim(), exeName);
            }

            if (isWindows)
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

                // 2) WinGet のリンク(シンボリックリンク。パッケージ更新後も安定)
                yield return CombineSafe(localAppData, "Microsoft", "WinGet", "Links", "ffmpeg.exe");

                // 3) WinGet パッケージ実体 (例: Gyan.FFmpeg_...\ffmpeg-x.y-full_build\bin\ffmpeg.exe)
                string wingetPackages = CombineSafe(localAppData, "Microsoft", "WinGet", "Packages");
                foreach (var package in SafeGetDirectories(wingetPackages))
                {
                    foreach (var sub in SafeGetDirectories(package))
                        yield return CombineSafe(sub, "bin", "ffmpeg.exe");
                    yield return CombineSafe(package, "bin", "ffmpeg.exe");
                }

                // 4) Chocolatey / Scoop / 手動配置の定番
                yield return @"C:\ProgramData\chocolatey\bin\ffmpeg.exe";
                yield return CombineSafe(userProfile, "scoop", "shims", "ffmpeg.exe");
                yield return @"C:\ffmpeg\bin\ffmpeg.exe";
            }
            else
            {
                // 5) macOS (Homebrew) / Linux の定番
                yield return "/opt/homebrew/bin/ffmpeg";
                yield return "/usr/local/bin/ffmpeg";
                yield return "/usr/bin/ffmpeg";
            }
        }

        private static string CombineSafe(params string[] parts)
        {
            try { return Path.Combine(parts); }
            catch { return null; }
        }

        private static string[] SafeGetDirectories(string path)
        {
            try
            {
                return Directory.Exists(path) ? Directory.GetDirectories(path) : Array.Empty<string>();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }
    }
}
