using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DistributedRecorder.Shared;
using DistributedRecorder.Worker;
using UnityEditor;
using UnityEngine;

namespace DistributedRecorder.Setup
{
    /// <summary>
    /// Defines the possible health status for a single check item.
    /// </summary>
    public enum HealthStatus
    {
        /// <summary>Check passed.</summary>
        Ok,
        /// <summary>Check passed with a warning (e.g. partial online Workers).</summary>
        Warning,
        /// <summary>Check failed; a fix-it action is available.</summary>
        Error,
        /// <summary>Check is currently running asynchronously.</summary>
        Checking,
        /// <summary>Check does not apply in the current context (e.g. Master-only on Worker).</summary>
        NotApplicable,
    }

    /// <summary>
    /// Immutable result for a single health check item.
    /// </summary>
    public sealed class HealthCheckItem
    {
        public string      Label       { get; set; } = string.Empty;
        public HealthStatus Status     { get; set; } = HealthStatus.Checking;
        public string      Detail      { get; set; } = string.Empty;
        public string      FixLabel    { get; set; } = string.Empty;
        public Action      FixAction   { get; set; }
    }

    /// <summary>
    /// Evaluates all Setup Hub health check items and returns an up-to-date snapshot.
    ///
    /// Designed to be called from <c>EditorApplication.update</c> at a low frequency
    /// (e.g. every 5 seconds) to avoid blocking the Editor.
    ///
    /// Async checks (Worker /health probes) are started once and awaited; the result is
    /// cached until the next explicit refresh.
    /// </summary>
    public static class HealthCheck
    {
        // HTTP timeout for /health probes
        private static readonly TimeSpan HealthProbeTimeout = TimeSpan.FromSeconds(3);

        // Cached worker health results
        private static Dictionary<string, bool> _workerOnlineCache  = new Dictionary<string, bool>();
        private static bool                     _workerCheckRunning = false;

        // ------------------------------------------------------------------
        // Public API
        // ------------------------------------------------------------------

        /// <summary>
        /// Returns the current snapshot of all health check items.
        /// Long-running probes (Worker /health) use cached results from the most
        /// recent async refresh; call <see cref="RefreshWorkerHealthAsync"/> to update.
        /// </summary>
        public static IReadOnlyList<HealthCheckItem> GetSnapshot()
        {
            return new List<HealthCheckItem>
            {
                CheckSharedKey(),
                CheckRecorderPackage(),
                CheckWorkerRegistry(),
                CheckWorkerOnline(),
                CheckVersionMatch(),
                CheckWorkerListening(),
            };
        }

        /// <summary>
        /// Asynchronously probes all registered Workers' <c>/health</c> endpoints
        /// and updates the internal cache.  Should be called periodically from the UI.
        /// </summary>
        public static async Task RefreshWorkerHealthAsync()
        {
            if (_workerCheckRunning) return;
            _workerCheckRunning = true;

            try
            {
                var registry = AssetDatabase.LoadAssetAtPath<WorkerRegistryAsset>(
                    WorkerRegistryAutoFactory.DefaultAssetPath);
                if (registry == null) { _workerOnlineCache.Clear(); return; }

                var newCache = new Dictionary<string, bool>(StringComparer.Ordinal);
                var tasks    = new List<Task>();

                foreach (var worker in registry.workers)
                {
                    if (worker == null) continue;
                    string url       = $"{worker.BaseUrl}/health";
                    string key       = worker.BaseUrl;
                    tasks.Add(ProbeWorkerHealth(url, key, newCache));
                }

                await Task.WhenAll(tasks);
                _workerOnlineCache = newCache;
            }
            finally
            {
                _workerCheckRunning = false;
            }
        }

        // ------------------------------------------------------------------
        // Individual checks
        // ------------------------------------------------------------------

        private static HealthCheckItem CheckSharedKey()
        {
            // Pivot v2: key is derived from EditorPrefs password — check that instead of the file.
            string savedPw = UnityEditor.EditorPrefs.GetString(SharedKeyLoader.PasswordPrefsKey, string.Empty);
            bool hasPw     = !string.IsNullOrEmpty(savedPw);

            // Also check legacy file as fallback indicator.
            string keyPath    = SharedKeyLoader.DefaultKeyPath;
            bool legacyExists = File.Exists(keyPath);
            bool ok           = hasPw || legacyExists;

            string detail;
            if (hasPw)
                detail = $"パスワード設定済み (EditorPrefs: {SharedKeyLoader.PasswordPrefsKey})";
            else if (legacyExists)
                detail = $"レガシー鍵ファイルあり: {keyPath}";
            else
                detail = $"パスワード未設定 / 鍵ファイルなし";

            return new HealthCheckItem
            {
                Label    = "共有鍵 / パスワード",
                Status   = ok ? HealthStatus.Ok : HealthStatus.Error,
                Detail   = detail,
                FixLabel = ok ? string.Empty : "Setup Hub でパスワードを設定",
                FixAction = ok ? null : (Action)(() => Open()),
            };
        }

        private static void Open()
        {
            SetupHubWindow.Open();
        }

        private static HealthCheckItem CheckRecorderPackage()
        {
            string version = VersionChecker.RecorderVersion;
            bool installed = !string.IsNullOrEmpty(version);
            return new HealthCheckItem
            {
                Label    = "Unity Recorder パッケージ",
                Status   = installed ? HealthStatus.Ok : HealthStatus.Error,
                Detail   = installed ? $"{version} インストール済み" : "未インストール",
                FixLabel = installed ? string.Empty : "インストール",
                FixAction = installed ? null : (Action)(() => RecorderPackageInstaller.StartInstall()),
            };
        }

        private static HealthCheckItem CheckWorkerRegistry()
        {
            var registry = AssetDatabase.LoadAssetAtPath<WorkerRegistryAsset>(
                WorkerRegistryAutoFactory.DefaultAssetPath);

            if (registry == null)
            {
                // Also search anywhere in Assets
                var guids = AssetDatabase.FindAssets("t:WorkerRegistryAsset");
                if (guids.Length > 0)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guids[0]);
                    registry   = AssetDatabase.LoadAssetAtPath<WorkerRegistryAsset>(path);
                }
            }

            if (registry == null)
            {
                return new HealthCheckItem
                {
                    Label    = "登録済みワーカー",
                    Status   = HealthStatus.Error,
                    Detail   = $"アセットが存在しません ({WorkerRegistryAutoFactory.DefaultAssetPath})",
                    FixLabel = "自動作成",
                    FixAction = () =>
                    {
                        WorkerRegistryAutoFactory.EnsureExists();
                        AssetDatabase.Refresh();
                    },
                };
            }

            int count    = registry.workers?.Count ?? 0;
            int online   = 0;
            foreach (var w in registry.workers ?? new System.Collections.Generic.List<WorkerInfo>())
            {
                if (w != null && _workerOnlineCache.TryGetValue(w.BaseUrl, out bool up) && up)
                    online++;
            }

            HealthStatus status = count == 0 ? HealthStatus.Warning
                                : online > 0 ? HealthStatus.Ok
                                :              HealthStatus.Warning;

            string detail;
            if (count == 0)
                detail = $"ワーカー未登録 — 「Worker を探す」で追加してください  " +
                         $"({WorkerRegistryAutoFactory.DefaultAssetPath})";
            else
                detail = $"{count} 台登録 / {online} 台オンライン  " +
                         $"({WorkerRegistryAutoFactory.DefaultAssetPath})";

            return new HealthCheckItem
            {
                Label    = "登録済みワーカー",
                Status   = status,
                Detail   = detail,
                FixLabel = "Registry を開く",
                FixAction = () => Selection.activeObject = registry,
            };
        }

        private static HealthCheckItem CheckWorkerOnline()
        {
            var registry = FindRegistry();
            if (registry == null)
                return new HealthCheckItem { Label = "Worker 疎通確認", Status = HealthStatus.NotApplicable, Detail = "レジストリなし" };

            int total  = registry.workers?.Count ?? 0;
            int online = 0;
            var offlineNames = new System.Text.StringBuilder();
            foreach (var w in registry.workers ?? new System.Collections.Generic.List<WorkerInfo>())
            {
                if (w == null) continue;
                if (_workerOnlineCache.TryGetValue(w.BaseUrl, out bool up) && up) { online++; }
                else offlineNames.Append($" {w.displayName}({w.host})");
            }

            HealthStatus status = total == 0 ? HealthStatus.NotApplicable
                                : online == total ? HealthStatus.Ok
                                : online > 0 ? HealthStatus.Warning
                                : HealthStatus.Error;

            return new HealthCheckItem
            {
                Label  = "Worker 疎通 (Master のみ)",
                Status = status,
                Detail = total == 0 ? "Worker 未登録"
                        : $"{online}/{total} オンライン" + (offlineNames.Length > 0 ? $" オフライン:{offlineNames}" : ""),
                FixLabel  = "再チェック",
                FixAction = () => { _ = RefreshWorkerHealthAsync(); },
            };
        }

        private static HealthCheckItem CheckVersionMatch()
        {
            var registry = FindRegistry();
            if (registry == null)
                return new HealthCheckItem { Label = "バージョン一致確認", Status = HealthStatus.NotApplicable, Detail = "レジストリなし" };

            // Version mismatch detection is done at job dispatch time via VersionChecker.MatchesLocal.
            // In Setup Hub we can only report local versions and suggest sync.
            string localUnity    = VersionChecker.UnityVersion;
            string localRecorder = VersionChecker.RecorderVersion;

            return new HealthCheckItem
            {
                Label  = "バージョン確認 (Master)",
                Status = string.IsNullOrEmpty(localRecorder) ? HealthStatus.Warning : HealthStatus.Ok,
                Detail = $"Unity {localUnity} / Recorder {(string.IsNullOrEmpty(localRecorder) ? "未インストール" : localRecorder)}",
                FixLabel  = "プロジェクトを同期",
                FixAction = null, // Sync UI is in SetupHubWindow
            };
        }

        private static HealthCheckItem CheckWorkerListening()
        {
            // Reflect Bootstrap._httpListener != null via the menu validator proxy.
            // Bootstrap.StopWorkerValidate() returns true when a listener is running.
            bool running = IsWorkerRunning();
            return new HealthCheckItem
            {
                Label    = "Worker 待受 (この PC)",
                Status   = running ? HealthStatus.Ok : HealthStatus.Warning,
                Detail   = running ? "待受中" : "停止中",
                FixLabel = running ? "Worker を停止" : "Worker を起動",
                FixAction = running
                    ? (Action)(() => Bootstrap.StopWorker())
                    : (Action)(() => Bootstrap.Run()),
            };
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private static WorkerRegistryAsset FindRegistry()
        {
            var r = AssetDatabase.LoadAssetAtPath<WorkerRegistryAsset>(
                WorkerRegistryAutoFactory.DefaultAssetPath);
            if (r != null) return r;

            var guids = AssetDatabase.FindAssets("t:WorkerRegistryAsset");
            if (guids.Length == 0) return null;
            return AssetDatabase.LoadAssetAtPath<WorkerRegistryAsset>(
                AssetDatabase.GUIDToAssetPath(guids[0]));
        }

        private static bool IsWorkerRunning()
        {
            return Bootstrap.IsWorkerRunning;
        }

        private static async Task ProbeWorkerHealth(
            string url, string key, Dictionary<string, bool> cache)
        {
            try
            {
                using var http  = new HttpClient { Timeout = HealthProbeTimeout };
                var response    = await http.GetAsync(url);
                cache[key]      = response.IsSuccessStatusCode;
            }
            catch
            {
                cache[key] = false;
            }
        }
    }
}
