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
    /// Cached version data for a single registered Worker, obtained from GET /health.
    /// </summary>
    internal sealed class WorkerVersionCache
    {
        public bool   Online          { get; set; }
        public string RecorderVersion { get; set; } = string.Empty;
        public string UnityVersion    { get; set; } = string.Empty;
    }
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

        // Cached worker health results — keyed by BaseUrl
        private static Dictionary<string, bool>               _workerOnlineCache   = new Dictionary<string, bool>();
        private static Dictionary<string, WorkerVersionCache> _workerVersionCache  = new Dictionary<string, WorkerVersionCache>();
        private static bool                                   _workerCheckRunning  = false;

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
                if (registry == null) { _workerOnlineCache.Clear(); _workerVersionCache.Clear(); return; }

                var newOnlineCache  = new Dictionary<string, bool>(StringComparer.Ordinal);
                var newVersionCache = new Dictionary<string, WorkerVersionCache>(StringComparer.Ordinal);
                var tasks           = new List<Task>();

                foreach (var worker in registry.workers)
                {
                    if (worker == null) continue;
                    string url = $"{worker.BaseUrl}/health";
                    string key = worker.BaseUrl;
                    tasks.Add(ProbeWorkerHealth(url, key, newOnlineCache, newVersionCache));
                }

                await Task.WhenAll(tasks);
                _workerOnlineCache  = newOnlineCache;
                _workerVersionCache = newVersionCache;
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
            string masterUnity    = VersionChecker.UnityVersion;
            string masterRecorder = VersionChecker.RecorderVersion;

            // Base item: Master versions
            if (string.IsNullOrEmpty(masterRecorder))
            {
                return new HealthCheckItem
                {
                    Label    = "バージョン確認",
                    Status   = HealthStatus.Warning,
                    Detail   = $"Master — Unity {masterUnity} / Recorder 未インストール",
                    FixLabel = "インストール",
                    FixAction = () => RecorderPackageInstaller.StartInstall(),
                };
            }

            var registry = FindRegistry();

            // No registered workers — show Master versions only.
            if (registry == null || registry.workers == null || registry.workers.Count == 0)
            {
                return new HealthCheckItem
                {
                    Label  = "バージョン確認",
                    Status = HealthStatus.Ok,
                    Detail = $"Master — Recorder {masterRecorder} / Unity {masterUnity} (Worker 未登録)",
                };
            }

            // Aggregate Worker version comparisons from the cached probe results.
            int totalWorkers    = 0;
            int mismatchWorkers = 0;
            int offlineWorkers  = 0;
            var mismatchLines   = new System.Text.StringBuilder();

            foreach (var worker in registry.workers)
            {
                if (worker == null) continue;
                totalWorkers++;

                if (!_workerVersionCache.TryGetValue(worker.BaseUrl, out var cache))
                {
                    // Not yet probed — treat as unknown (not a mismatch).
                    continue;
                }

                if (!cache.Online)
                {
                    offlineWorkers++;
                    continue;
                }

                var cmp = SetupVersionHelper.CompareVersions(
                    masterRecorder, masterUnity,
                    cache.RecorderVersion, cache.UnityVersion);

                if (cmp.Result != SetupVersionHelper.VersionMatchResult.Match)
                {
                    mismatchWorkers++;
                    mismatchLines.AppendLine(
                        $"  {worker.displayName}: " +
                        SetupVersionHelper.FormatVersionLabel(
                            masterRecorder, masterUnity,
                            cache.RecorderVersion, cache.UnityVersion));
                }
            }

            bool hasProbeResults = _workerVersionCache.Count > 0;

            HealthStatus status;
            string detail;

            if (!hasProbeResults)
            {
                status = HealthStatus.Checking;
                detail = $"Master — Recorder {masterRecorder} / Unity {masterUnity}" +
                         $"\nWorker 版の確認中（「ヘルスチェックを今すぐ更新」を押してください）";
            }
            else if (mismatchWorkers > 0)
            {
                status = HealthStatus.Warning;
                detail = $"Master — Recorder {masterRecorder} / Unity {masterUnity}" +
                         $"\n版ズレあり ({mismatchWorkers}/{totalWorkers} Worker):\n" +
                         mismatchLines.ToString().TrimEnd();
            }
            else if (offlineWorkers == totalWorkers && totalWorkers > 0)
            {
                status = HealthStatus.Warning;
                detail = $"Master — Recorder {masterRecorder} / Unity {masterUnity}" +
                         $"\n全 Worker オフライン（版確認不可）";
            }
            else
            {
                status = HealthStatus.Ok;
                int checkedCount = totalWorkers - offlineWorkers;
                detail = $"Master — Recorder {masterRecorder} / Unity {masterUnity}" +
                         $"\n全 Worker 版一致 ({checkedCount}/{totalWorkers} オンライン確認済み)";
            }

            return new HealthCheckItem
            {
                Label     = "バージョン確認",
                Status    = status,
                Detail    = detail,
                FixLabel  = "再確認",
                FixAction = () => { _ = RefreshWorkerHealthAsync(); },
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
            string url,
            string key,
            Dictionary<string, bool>               onlineCache,
            Dictionary<string, WorkerVersionCache> versionCache)
        {
            try
            {
                using var http    = new HttpClient { Timeout = HealthProbeTimeout };
                var response      = await http.GetAsync(url);
                bool online       = response.IsSuccessStatusCode;
                onlineCache[key]  = online;

                if (online)
                {
                    string json   = await response.Content.ReadAsStringAsync();
                    var health    = ProtocolSerializer.Deserialize<WorkerHealth>(json);
                    versionCache[key] = new WorkerVersionCache
                    {
                        Online          = true,
                        RecorderVersion = health?.recorderVersion ?? string.Empty,
                        UnityVersion    = health?.unityVersion    ?? string.Empty,
                    };
                }
                else
                {
                    versionCache[key] = new WorkerVersionCache { Online = false };
                }
            }
            catch
            {
                onlineCache[key]  = false;
                versionCache[key] = new WorkerVersionCache { Online = false };
            }
        }
    }
}
