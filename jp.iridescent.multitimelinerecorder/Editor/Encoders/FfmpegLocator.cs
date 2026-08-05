using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Unity.MultiTimelineRecorder.Encoders
{
    /// <summary>
    /// このマシンにインストール済みの ffmpeg 実行ファイルを定番の場所から探す。
    /// UI の「自動検出」ボタン用。見つからなくても例外にせず null を返す
    /// (導入手段はユーザーごとに異なるため、検出失敗は正常系として扱う)。
    /// </summary>
    public static class FfmpegLocator
    {
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
