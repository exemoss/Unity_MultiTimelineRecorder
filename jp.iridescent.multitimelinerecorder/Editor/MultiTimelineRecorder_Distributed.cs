using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DistributedRecorder.Master;
using DistributedRecorder.Shared;
using UnityEditor;
using UnityEditor.Recorder;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;
using UnityEngine.Timeline;
using Unity.MultiTimelineRecorder.Utilities;

namespace Unity.MultiTimelineRecorder
{
    /// <summary>
    /// Distributed rendering integration for Multi Timeline Recorder.
    /// Appended as a partial class so that the MTR core file is not modified
    /// beyond the single <c>DrawDistributedSection()</c> call-site hook.
    ///
    /// Supports M4 (MTR seam), M5 (dispatch), and M6 (progress monitoring +
    /// result download + hash/version override dialog).
    /// </summary>
    public partial class MultiTimelineRecorder
    {
        // -----------------------------------------------------------------------
        // State
        // -----------------------------------------------------------------------

        /// <summary>Whether distributed rendering mode is enabled in the UI.</summary>
        private bool _distributedMode;

        /// <summary>
        /// Assigned WorkerRegistryAsset.
        /// Loaded from EditorPrefs asset GUID on first use; can be changed via the UI.
        /// </summary>
        private WorkerRegistryAsset _distWorkerRegistry;

        /// <summary>
        /// Per-job view models for progress display and result tracking.
        /// Replaces the M5 <c>DistributedJobRecord</c> list with a richer model.
        /// </summary>
        private readonly List<MtrJobViewModel> _dispatchedJobs = new List<MtrJobViewModel>();

        /// <summary>
        /// Scroll position for the job list in DrawDistributedSection.
        /// </summary>
        private Vector2 _distJobScrollPos;

        /// <summary>
        /// When true, hash/version mismatch "Send anyway" is applied to all
        /// subsequent jobs in the current dispatch batch without asking again.
        /// Reset to false at the start of each dispatch batch.
        /// </summary>
        private bool _sessionSkipHashCheck;
        private bool _sessionSkipVersionCheck;

        // --- worker-recorder-version-align state --------------------------------

        /// <summary>
        /// Cached version status for each Worker, refreshed by the probe button or
        /// automatically after DrawDistributedSection is first shown each session.
        /// Key = worker.displayName.
        /// </summary>
        private readonly Dictionary<string, WorkerVersionStatus> _workerVersionStatus =
            new Dictionary<string, WorkerVersionStatus>();

        /// <summary>Workers currently undergoing an align operation (display spinner).</summary>
        private readonly HashSet<string> _aligningWorkers = new HashSet<string>();

        /// <summary>
        /// Last status message from an align operation, keyed by worker.displayName.
        /// </summary>
        private readonly Dictionary<string, string> _alignStatusMessages =
            new Dictionary<string, string>();

        // --- worker-registry-management: self-detection cache -------------------

        /// <summary>
        /// Local addresses of this machine, collected once and cached for the session.
        /// Refreshed when the user presses "バージョン確認" (same cadence as version badges)
        /// and on first draw of the distributed section.
        /// Never null after first access; read via <see cref="LocalAddressCache"/>.
        /// </summary>
        private IReadOnlyList<string> _localAddressCache;

        /// <summary>Lazy-initialised local address cache.</summary>
        private IReadOnlyList<string> LocalAddressCache
        {
            get
            {
                if (_localAddressCache == null)
                    _localAddressCache = SelfHostDetector.CollectLocalAddresses();
                return _localAddressCache;
            }
        }

        // EditorPrefs keys (MTR window scope)
        private const string PrefKeyDistMode     = "MTR.DistributedMode";
        private const string PrefKeyDistRegistry = "MTR.DistributedRegistryGuid";
        private const string PrefKeyCollectDir   = "MultiTimelineRecorder.distributed.collectDir";

        // ── collect-to-dir state (v1.4.10) ──────────────────────────────────────

        /// <summary>
        /// User-specified absolute path for bulk collection of render results.
        /// Empty = feature disabled.  Snapshotted into <see cref="MtrJobViewModel.CollectDir"/>
        /// at dispatch time so that UI edits do not affect in-flight jobs.
        /// Persisted via <see cref="PrefKeyCollectDir"/> in <see cref="EditorPrefs"/>.
        /// </summary>
        private string _collectDir = string.Empty;

        /// <summary>
        /// Guard flag for the "まとめて DL" bulk-collect button.
        /// Prevents concurrent invocations from rapid double-clicks.
        /// </summary>
        private bool _isBulkCollecting;

        /// <summary>
        /// Guard flag for the "失敗した回収を一括再試行" button (retry-failed-collection
        /// phase 1). Prevents concurrent invocations from rapid double-clicks; mirrors
        /// the <see cref="_isBulkCollecting"/> pattern.
        /// </summary>
        private bool _isRetryingDownloads;

        // Default relative output root (relative to project root)
        private const string DistOutputRelRoot = "Recordings/Distributed";

        // Maximum length for a sanitized timeline name component in the output path
        private const int MaxSanitizedTimelineNameLength = 64;

        // -----------------------------------------------------------------------
        // Dispatch-retry-queue scheduler state (dispatch-retry-queue feature)
        //
        // All fields are only accessed on the Editor main thread (inside
        // EditorCallbackOnce delegates or synchronous GUI/async methods) so no
        // additional locking is required.
        // -----------------------------------------------------------------------

        /// <summary>
        /// Maximum number of times a single job may be retried after a WorkerBusy
        /// or other transient failure before it is permanently marked Failed.
        /// </summary>
        private const int MaxJobRetries = 5;

        /// <summary>
        /// Failsafe: if no progress event is received for a job within this many
        /// seconds, health-poll the assigned Worker to check whether it is idle.
        /// Keeps the queue from stalling when the NDJSON push stream silently drops.
        /// </summary>
        private const double FailsafeStallSeconds = 30.0;

        // worker-reload-survival 案D: timeout constants ─────────────────────────
        //
        // Two timeout tiers to survive the Worker's domain-reload gap:
        //   • pre-flight (dispatch-time liveness probe): fast fail 3 s.
        //     Worker must be reachable before we even try to send a job.
        //   • in-flight (health poll during stall watchdog): patient 20 s.
        //     During recording the Worker may be unresponsive for the duration of
        //     the domain reload (HDRP shader warmup can add several seconds).
        //     Real reload time is typically 5–15 s; 20 s gives a 1.5× safety margin.
        //     Final tuning must be confirmed on the live machine (§F-4).
        //
        // ProgressMonitor reconnect: after a stream drop the monitor is restarted
        // up to ReconnectMaxAttempts times with ReconnectDelaySeconds between tries
        // before the job is marked Failed.

        /// <summary>
        /// Timeout for the /health poll issued by the stall watchdog while a job is
        /// in-flight. Longer than the pre-flight probe to absorb domain-reload gaps.
        /// Public for EditMode tests.
        /// </summary>
        public const double InFlightHealthTimeoutSeconds = 20.0;

        /// <summary>
        /// Maximum number of times the stall watchdog will restart the progress
        /// monitor after a stream drop before giving up and marking the job Failed.
        /// Public for EditMode tests.
        /// </summary>
        public const int ReconnectMaxAttempts = 6;

        /// <summary>
        /// Seconds to wait between consecutive ProgressMonitor reconnect attempts.
        /// Public for EditMode tests.
        /// </summary>
        public const double ReconnectDelaySeconds = 5.0;

        /// <summary>
        /// Public accessor for <see cref="ProgressMonitor.ConnectTimeout"/> (seconds)
        /// used by EditMode tests to assert separation from the pre-flight 3 s probe.
        /// </summary>
        public static double ProgressStreamConnectTimeoutSecondsPublic
            => ProgressMonitor.ConnectTimeout.TotalSeconds;

        // retry-failed-collection (phase 1): automatic re-collection watchdog constants ──
        //
        // A periodic EditorApplication.update tick (throttled to AutoRetryTickIntervalSeconds)
        // scans _dispatchedJobs for jobs stuck in DownloadState.Failed with a Connection-kind
        // failure (NotFound is a phase-2 concern — never auto-retried) and retries them once
        // the exponential-backoff delay has elapsed, capped at MaxAutoRetries attempts.
        // Manual retries (per-job / bulk buttons) reset AutoRetryCount, so they are never
        // blocked by this cap.

        /// <summary>
        /// Maximum number of automatic re-collection attempts per job. Once exceeded, the
        /// watchdog stops retrying that job automatically (the user can still use the manual
        /// "再回収" / bulk buttons, which reset the counter). Public for EditMode tests.
        /// </summary>
        public const int MaxAutoRetries = 4;

        /// <summary>
        /// Base delay (seconds) for the exponential backoff between automatic retry
        /// attempts: delay for attempt <c>n</c> (1-indexed) is
        /// <c>AutoRetryBaseDelaySeconds * 2^(n-1)</c> — 5s, 10s, 20s, 40s for
        /// <see cref="MaxAutoRetries"/> = 4. Public for EditMode tests.
        /// </summary>
        public const double AutoRetryBaseDelaySeconds = 5.0;

        /// <summary>
        /// Minimum real-time interval between successive <see cref="AutoRetryDownloadsTick"/>
        /// scans, to avoid re-evaluating backoff eligibility on every single Editor frame.
        /// Public for EditMode tests.
        /// </summary>
        public const double AutoRetryTickIntervalSeconds = 2.0;

        /// <summary>
        /// Whether the auto-retry-downloads EditorApplication.update callback is registered.
        /// </summary>
        private bool _autoRetryWatchdogRegistered;

        /// <summary>
        /// <see cref="EditorApplication.timeSinceStartup"/> at the last
        /// <see cref="AutoRetryDownloadsTick"/> scan; used to throttle scans to
        /// <see cref="AutoRetryTickIntervalSeconds"/>.
        /// </summary>
        private double _lastAutoRetryTickTime;

        /// <summary>
        /// Job IDs currently being auto-retried (re-entrancy guard, mirrors
        /// <see cref="_stallWatchdogActive"/>). Prevents the tick from firing a second
        /// concurrent <see cref="DownloadResultsAsync"/> for a job whose previous
        /// auto-retry attempt has not yet completed.
        /// </summary>
        private readonly HashSet<string> _autoRetryActive = new HashSet<string>();

        // reprobe-add-worker: guard flag ─────────────────────────────────────────
        //
        // Set to true while ReprobeAndAddWorkersAsync is running to prevent
        // concurrent button-clicks from starting a second probe pass.
        // Only accessed on the Editor main thread.

        /// <summary>
        /// True while the "ワーカーを再プローブ/追加" operation is in progress.
        /// Prevents concurrent invocations from a rapid button double-click.
        /// </summary>
        private bool _reprobing;

        // worker-git-sync (v1.4.11): guard flag ──────────────────────────────────
        //
        // Set to true while SyncWorkersToLatestAsync is running to prevent concurrent
        // button-clicks.  Only accessed on the Editor main thread.

        /// <summary>
        /// True while the "Worker を最新に同期" operation is in progress.
        /// Prevents concurrent invocations from a rapid button double-click.
        /// </summary>
        private bool _syncingWorkers;

        /// <summary>
        /// Last result message from the most recent git sync operation, shown in the UI.
        /// Cleared at the start of each new sync.
        /// </summary>
        private string _syncResultMessage;

        /// <summary>
        /// Last result message from the most recent reprobe operation, shown in the UI.
        /// Cleared at the start of each new reprobe.
        /// </summary>
        private string _reprobeResultMessage;

        /// <summary>
        /// Jobs waiting to be dispatched (not yet sent to any Worker).
        /// Populated at batch start; consumed by <see cref="TryDispatchNextQueuedJob"/>.
        /// Only accessed on the main thread.
        /// </summary>
        private readonly Queue<MtrJobViewModel> _pendingQueue = new Queue<MtrJobViewModel>();

        /// <summary>
        /// Tracks which Worker each in-flight job is running on.
        /// Key = JobId, Value = WorkerInfo.
        /// Used to direct the "next job" to the Worker that just became free.
        /// Only accessed on the main thread.
        /// </summary>
        private readonly Dictionary<string, WorkerInfo> _jobWorkerMap =
            new Dictionary<string, WorkerInfo>();

        /// <summary>
        /// Tracks how many jobs are currently in-flight on each Worker.
        /// Workers with in-flight count == 0 are considered available for dispatch.
        /// Key = worker.displayName (unique per registry entry).
        /// Only accessed on the main thread.
        /// </summary>
        private readonly Dictionary<string, int> _workerInflightCount =
            new Dictionary<string, int>();

        /// <summary>
        /// Shared <see cref="JobDispatcher"/> for the current batch.
        /// Created at batch start; disposed when all jobs reach terminal state.
        /// </summary>
        private JobDispatcher _batchDispatcher;

        /// <summary>
        /// Shared <see cref="HttpTransport"/> for the current batch.
        /// Disposed alongside <see cref="_batchDispatcher"/>.
        /// </summary>
        private HttpTransport _batchTransport;

        /// <summary>
        /// Shared <see cref="HmacAuthenticator"/> for the current batch.
        /// </summary>
        private HmacAuthenticator _batchAuth;

        /// <summary>
        /// Workers that responded to the pre-flight liveness probe at batch start.
        /// Used as the failover candidate pool so that pre-flight-offline Workers are
        /// excluded from the initial failover list (Major fix: dispatch-worker-liveness §B2).
        /// A Worker absent from this list can still be promoted to the failover pool once
        /// all online candidates are exhausted, matching plan.md 不明点5 (one-chance policy).
        ///
        /// Set in <see cref="StartDistributedRecordingInternalAsync"/>; cleared when the
        /// batch finalizes (<see cref="FinalizeBatchIfDone"/>).
        /// Only accessed on the Editor main thread.
        /// </summary>
        private List<WorkerInfo> _batchOnlineWorkers;

        /// <summary>
        /// Last progress-event time (EditorApplication.timeSinceStartup) keyed by JobId.
        /// Used by the failsafe polling to detect stalled jobs.
        /// Only accessed on the main thread.
        /// </summary>
        private readonly Dictionary<string, double> _jobLastProgressTime =
            new Dictionary<string, double>();

        // worker-reload-survival 案D: stall watchdog ────────────────────────────

        /// <summary>
        /// Tracks how many ProgressMonitor reconnect attempts have been made for
        /// each in-flight JobId.  Reset when the job terminates.
        /// Only accessed on the main thread.
        /// </summary>
        private readonly Dictionary<string, int> _jobReconnectCount =
            new Dictionary<string, int>();

        /// <summary>
        /// Set of JobIds currently being processed by the stall watchdog to prevent
        /// concurrent re-entrant watchdog invocations for the same job.
        /// Only accessed on the main thread.
        /// </summary>
        private readonly HashSet<string> _stallWatchdogActive =
            new HashSet<string>();

        /// <summary>
        /// Whether the stall watchdog EditorApplication.update callback is registered.
        /// </summary>
        private bool _stallWatchdogRegistered;

        // -----------------------------------------------------------------------
        // UI hook (called from MultiTimelineRecorder.cs DrawRecordControls area)
        // -----------------------------------------------------------------------

        /// <summary>
        /// Draws the distributed rendering UI section.
        /// Called by the core partial class (<c>MultiTimelineRecorder.cs</c>) inside
        /// <c>DrawRecordControls()</c>.  Internal visibility so tests can verify
        /// compilation without instantiating the window.
        /// </summary>
        internal void DrawDistributedSection()
        {
            // Lazily load persisted state (called once per OnEnable cycle via GUI)
            if (!_distributedSectionInitialized)
                InitDistributedSection();

            EditorGUILayout.Space(4);

            // ── Foldout header ──────────────────────────────────────────────
            EditorGUILayout.BeginVertical("HelpBox");

            // Wrap entire body in try/finally so Begin/End calls are always balanced
            // even if an exception occurs mid-draw (prevents layout stack corruption).
            try
            {
                EditorGUILayout.BeginHorizontal();
                bool newMode = EditorGUILayout.Toggle(_distributedMode, GUILayout.Width(16));
                if (newMode != _distributedMode)
                {
                    _distributedMode = newMode;
                    PersistDistributedState();
                }
                EditorGUILayout.LabelField("分散レンダリング (Distributed Render)", EditorStyles.boldLabel);
                EditorGUILayout.EndHorizontal();

                if (!_distributedMode)
                    return;

                EditorGUILayout.Space(4);

                // ── Worker registry selector ─────────────────────────────────
                EditorGUI.BeginChangeCheck();
                _distWorkerRegistry = (WorkerRegistryAsset)EditorGUILayout.ObjectField(
                    "Worker Registry",
                    _distWorkerRegistry,
                    typeof(WorkerRegistryAsset),
                    false);
                if (EditorGUI.EndChangeCheck())
                    PersistDistributedState();

                int enabledWorkerCount = _distWorkerRegistry != null
                    ? _distWorkerRegistry.EnabledWorkers.Count
                    : 0;

                if (_distWorkerRegistry == null)
                {
                    EditorGUILayout.HelpBox(
                        "WorkerRegistryAsset を割り当ててください。\n" +
                        "Assets > Create > DistributedRecorder > WorkerRegistry で作成できます。",
                        MessageType.Info);
                }
                else if (_distWorkerRegistry.workers == null || _distWorkerRegistry.workers.Count == 0)
                {
                    EditorGUILayout.HelpBox(
                        "Worker がレジストリに登録されていません。",
                        MessageType.Warning);
                }
                else
                {
                    if (enabledWorkerCount == 0)
                    {
                        EditorGUILayout.HelpBox(
                            "有効な Worker がレジストリに登録されていません。",
                            MessageType.Warning);
                    }
                    else
                    {
                        EditorGUILayout.LabelField(
                            $"有効 Worker: {enabledWorkerCount} 台",
                            EditorStyles.miniLabel);
                    }

                    // ── Worker version badges + self badge + delete button ────
                    // Pass all workers (enabled and disabled) so delete buttons
                    // are accessible for disabled entries too.
                    DrawWorkerVersionBadges(_distWorkerRegistry.workers);
                }

                // ── Lightweight target count (no CollectRenderTargets / AssetDatabase) ──
                // CollectRenderTargets() is called only at dispatch time (button click in
                // DrawRecordControls). Here we just show a cheap count so the UI can
                // display an informational hint without heavy processing every frame.
                int imageTimelineCount = CountSupportedTimelinesCheap();

                if (imageTimelineCount == 0 && selectedDirectorIndices.Count > 0)
                {
                    EditorGUILayout.HelpBox(
                        "分散対応の Recorder が選択 Timeline に見つかりません。\n" +
                        "対応形式: Image Sequence / Movie（AOV・FBX 等はスキップ）。",
                        MessageType.Warning);
                }
                else if (imageTimelineCount > 0)
                {
                    EditorGUILayout.LabelField(
                        $"対象 Timeline: {imageTimelineCount} 件 → 上の「分散実行」ボタンで開始",
                        EditorStyles.miniLabel);
                }

                // ── Job progress list ────────────────────────────────────────
                if (_dispatchedJobs.Count > 0)
                {
                    EditorGUILayout.Space(4);
                    DrawDistributedJobList();
                }
            }
            finally
            {
                EditorGUILayout.EndVertical();
            }
        }

        // -----------------------------------------------------------------------
        // Job progress UI
        // -----------------------------------------------------------------------

        private void DrawDistributedJobList()
        {
            // Summary line
            int total     = _dispatchedJobs.Count;
            int completed = CountJobsByState(JobState.Completed);
            int failed    = CountJobsByState(JobState.Failed);
            int queued    = CountJobsByState(JobState.Queued);
            int running   = CountJobsByState(JobState.Running);
            int active    = total - completed - failed;

            string summary;
            if (active > 0)
            {
                summary = queued > 0
                    ? $"実行中: {running} / 待機中: {queued} / 完了: {completed} / 失敗: {failed}"
                    : $"実行中: {running} / 完了: {completed} / 失敗: {failed}";
            }
            else
            {
                summary = $"完了: {completed} / 失敗: {failed} (合計 {total})";
            }

            EditorGUILayout.LabelField(summary, EditorStyles.miniLabel);

            // All-complete notice with output path
            if (active == 0 && total > 0)
            {
                string outRoot = Path.Combine(ProjectPaths.ProjectRoot, DistOutputRelRoot);
                EditorGUILayout.LabelField(
                    $"回収先: {outRoot}",
                    EditorStyles.miniLabel);
            }

            // ── reprobe-add-worker: re-probe button ───────────────────────────
            // Only enabled while a batch is in flight (_batchOnlineWorkers != null)
            // and a probe is not already running.
            // Placed above the job list so it is visible without scrolling.
            EditorGUILayout.BeginHorizontal();
            bool batchActive = _batchOnlineWorkers != null && _batchDispatcher != null && _batchAuth != null;
            GUI.enabled = batchActive && !_reprobing;
            if (GUILayout.Button(
                    _reprobing ? "再プローブ中…" : "ワーカーを再プローブ/追加",
                    GUILayout.Height(22)))
            {
                _ = ReprobeAndAddWorkersAsync();
            }
            GUI.enabled = true;
            if (!string.IsNullOrEmpty(_reprobeResultMessage))
            {
                EditorGUILayout.LabelField(_reprobeResultMessage, EditorStyles.miniLabel);
            }
            EditorGUILayout.EndHorizontal();
            // ─────────────────────────────────────────────────────────────────

            // ── stop-button: batch stop button ───────────────────────────────
            // Enabled only while a batch is in flight. Shown in red to signal a
            // destructive action. Confirmation dialog prevents accidental clicks.
            EditorGUILayout.BeginHorizontal();
            GUI.enabled = batchActive;
            var prevColor = GUI.color;
            GUI.color = batchActive ? new Color(1f, 0.4f, 0.4f) : GUI.color;
            if (GUILayout.Button("停止", GUILayout.Height(22)))
            {
                bool confirmed = EditorUtility.DisplayDialog(
                    "分散レンダリングを停止",
                    "実行中のバッチを停止しますか？\n\n" +
                    "・待機中ジョブはキャンセルされます。\n" +
                    "・実行中ジョブは Worker に中断リクエストを送信します。\n" +
                    "  古い Worker（v1.4.9 未満）は中断リクエストを無視して録画を継続しますが、\n" +
                    "  master は新規ジョブを送信しません。\n\n" +
                    "この操作は取り消せません。",
                    "停止する",
                    "キャンセル");
                if (confirmed)
                    _ = StopBatchAsync();
            }
            GUI.color = prevColor;
            GUI.enabled = true;
            EditorGUILayout.EndHorizontal();
            // ─────────────────────────────────────────────────────────────────

            // ── worker-git-sync (v1.4.11): "Worker を最新に同期" button ─────────
            // Destructive operation: discards Worker's local uncommitted changes.
            // - Enabled when a registry is loaded (not batch-gated: useful before dispatch).
            // - Master's own branch is fetched via GitInfo; Workers on a different branch
            //   are skipped to prevent accidental cross-branch sync.
            // - Confirmation dialog required before execution.
            // - 404 from old Workers (< v1.4.11) is handled gracefully (skip + log).
            EditorGUILayout.BeginHorizontal();
            bool hasRegistry = _distWorkerRegistry != null
                               && _distWorkerRegistry.workers != null
                               && _distWorkerRegistry.workers.Count > 0;
            GUI.enabled = hasRegistry && !_syncingWorkers;
            if (GUILayout.Button(
                    _syncingWorkers ? "同期中…" : "Worker を最新に同期",
                    GUILayout.Height(22)))
            {
                _ = SyncWorkersToLatestAsync();
            }
            GUI.enabled = true;
            if (!string.IsNullOrEmpty(_syncResultMessage))
            {
                EditorGUILayout.LabelField(_syncResultMessage, EditorStyles.miniLabel);
            }
            EditorGUILayout.EndHorizontal();
            // ─────────────────────────────────────────────────────────────────

            // ── collect-to-dir UI (v1.4.10) ──────────────────────────────────
            // Placed directly below the stop button so both controls are visible
            // in the same section of the MTR window.
            DrawCollectDirUI();
            // ─────────────────────────────────────────────────────────────────

            // ── retry-failed-collection (phase 1): bulk retry button ────────────
            // "失敗した回収を一括再試行 [N 件]" — retries every job currently in
            // DownloadState.Failed. Disabled while empty (N==0) or already running.
            EditorGUILayout.BeginHorizontal();
            int failedDownloadCount = CountFailedDownloads();
            using (new EditorGUI.DisabledScope(_isRetryingDownloads || failedDownloadCount == 0))
            {
                string retryLabel = _isRetryingDownloads
                    ? "再試行中..."
                    : $"失敗した回収を一括再試行 [{failedDownloadCount} 件]";
                if (GUILayout.Button(retryLabel, GUILayout.Height(22)))
                {
                    RetryFailedDownloadsAsync();
                }
            }
            EditorGUILayout.EndHorizontal();
            // ─────────────────────────────────────────────────────────────────

            // Scrollable job list (max 150px)
            _distJobScrollPos = EditorGUILayout.BeginScrollView(
                _distJobScrollPos, GUILayout.Height(Mathf.Min(150, _dispatchedJobs.Count * 36 + 4)));
            try
            {
                foreach (var vm in _dispatchedJobs)
                    DrawMtrJobRow(vm);
            }
            finally
            {
                EditorGUILayout.EndScrollView();
            }
        }

        // ── dispatch-status-labels: UI label constants ──────────────────────────
        //
        // All display strings are centralized here so they can be changed without
        // touching test code (tests compare against these constants directly).
        // Named with a k_ prefix to match Unity Editor coding convention.

        internal const string LabelQueued      = "待機中";
        internal const string LabelSending     = "データ送信中";
        internal const string LabelPreparing   = "Editor 起動中…";
        internal const string LabelCompleted   = "完了";
        internal const string LabelFailed      = "失敗";
        internal const string LabelUnreachable = "到達不能";
        internal const string LabelCollecting  = "収集中";
        internal const string LabelCancelled   = "停止";
        // retry-failed-collection (phase 1): distinct label for Completed+DownloadState==Failed
        // so the UI does not misreport a failed collection as "完了".
        internal const string LabelDownloadFailed = "回収失敗";
        // "録画中 N/M" is built at call-site from LabelRecordingFmt
        internal const string LabelRecordingFmt = "録画中 {0}/{1}";

        /// <summary>
        /// Pure function: computes the human-readable status label for the job list UI.
        ///
        /// Decision tree:
        /// <list type="bullet">
        ///   <item>Terminal: Completed → "完了" (or "完了" even if DL still in progress;
        ///     the Collecting label is shown while DownloadState == InProgress)</item>
        ///   <item>Terminal: Failed     → "失敗"</item>
        ///   <item>Terminal: Unreachable → "到達不能"</item>
        ///   <item>Terminal: Completed + DownloadState == Failed → "回収失敗"
        ///     (retry-failed-collection phase 1: recording succeeded but the result
        ///     pull failed; previously mis-shown as "完了")</item>
        ///   <item>DownloadState == InProgress → "収集中" (overrides state label)</item>
        ///   <item>Queued (Phase == Queued / Sending) → label based on Phase</item>
        ///   <item>Running + TotalFrames == 0 (Phase == Preparing) → "Editor 起動中…"</item>
        ///   <item>Running + TotalFrames &gt; 0 → "録画中 N/M"</item>
        ///   <item>Pending (just dispatched, ack not yet received) → "データ送信中"</item>
        ///   <item>All others → Phase label as fallback</item>
        /// </list>
        ///
        /// Made <c>public static</c> for hermetic EditMode tests.
        /// Added in dispatch-status-labels. Extended in retry-failed-collection (phase 1).
        /// </summary>
        public static string ComputeJobStatusLabel(MtrJobViewModel vm)
        {
            if (vm == null) return LabelFailed;

            // Terminal states (evaluated first; DownloadState may still be InProgress
            // briefly after Completed — the Collecting label takes priority in that window).
            switch (vm.State)
            {
                case JobState.Failed:
                    return LabelFailed;
                case JobState.Unreachable:
                    return LabelUnreachable;
                case JobState.Cancelled:
                    return LabelCancelled; // stop-button: show "停止" for cancelled jobs
            }

            // Download in-progress: show "収集中" even when State == Completed,
            // because the result files are still being pulled from the Worker.
            if (vm.DownloadState == DownloadState.InProgress)
                return LabelCollecting;

            // retry-failed-collection (phase 1): recording finished but the result
            // pull failed — surface this distinctly instead of "完了" so the user
            // knows a manual/automatic re-collection is needed.
            if (vm.State == JobState.Completed && vm.DownloadState == DownloadState.Failed)
                return LabelDownloadFailed;

            // Completed (and download done or not started)
            if (vm.State == JobState.Completed)
                return LabelCompleted;

            // Active / queued states — use Phase for finer granularity.
            // Exception: Phase.Queued while State==Running and TotalFrames>0 is a stale-Phase
            // scenario (the ProgressEvent arrived before Phase was updated); treat it as
            // Recording so the user sees "録画中 N/M" instead of "待機中".
            switch (vm.Phase)
            {
                case JobPhase.Queued:
                    // Stale-Phase guard: if we are already Running with frames, show recording.
                    if (vm.State == JobState.Running && vm.TotalFrames > 0)
                        return string.Format(LabelRecordingFmt, vm.CurrentFrame, vm.TotalFrames);
                    return LabelQueued;
                case JobPhase.Sending:
                    return LabelSending;
                case JobPhase.Preparing:
                    return LabelPreparing;
                case JobPhase.Recording:
                    return string.Format(LabelRecordingFmt, vm.CurrentFrame, vm.TotalFrames);
                case JobPhase.Collecting:
                    return LabelCollecting;
                case JobPhase.Terminal:
                    // Shouldn't normally reach here (terminal handled above), but guard.
                    return LabelCompleted;
            }

            // Fallback: derive from State + TotalFrames for robustness
            // (covers Pending = just-dispatched-not-yet-acked, or Phase not yet set)
            switch (vm.State)
            {
                case JobState.Queued:
                case JobState.Pending:
                    return vm.TotalFrames > 0
                        ? string.Format(LabelRecordingFmt, vm.CurrentFrame, vm.TotalFrames)
                        : LabelPreparing;
                case JobState.Running:
                    return vm.TotalFrames > 0
                        ? string.Format(LabelRecordingFmt, vm.CurrentFrame, vm.TotalFrames)
                        : LabelPreparing;
                default:
                    return LabelQueued;
            }
        }

        // ── collect-to-dir UI (v1.4.10) ──────────────────────────────────────────
        //
        // "収集先ディレクトリ" text field + "..." folder picker + "まとめて DL" button.
        // Rendered inside DrawDistributedJobList(), directly below the stop button,
        // so it appears in the same MTR window section that users already interact with.
        //
        // _collectDir is persisted to EditorPrefs (PrefKeyCollectDir) by
        // InitDistributedSection / PersistDistributedState.
        // ─────────────────────────────────────────────────────────────────────────

        private void DrawCollectDirUI()
        {
            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField("収集先ディレクトリ", EditorStyles.boldLabel);

            // Path text field + folder picker on the same line.
            EditorGUILayout.BeginHorizontal();
            string newDir = EditorGUILayout.TextField(
                new GUIContent("収集先",
                    "完了したジョブの成果物をまとめてコピーするディレクトリ。\n" +
                    "空の場合は従来の Output Directory のみに保存されます。"),
                _collectDir);

            if (newDir != _collectDir)
            {
                _collectDir = newDir;
                PersistDistributedState();
            }

            if (GUILayout.Button("...", GUILayout.Width(30)))
            {
                string picked = EditorUtility.OpenFolderPanel(
                    "収集先ディレクトリを選択",
                    string.IsNullOrEmpty(_collectDir) ? string.Empty : _collectDir,
                    string.Empty);

                if (!string.IsNullOrEmpty(picked))
                {
                    if (CollectPathValidator.Validate(picked, out string pathErr))
                    {
                        _collectDir = picked;
                        PersistDistributedState();
                    }
                    else
                    {
                        EditorUtility.DisplayDialog("無効なパス", pathErr, "OK");
                    }
                }
            }
            EditorGUILayout.EndHorizontal();

            // Inline validation feedback.
            if (!string.IsNullOrEmpty(_collectDir))
            {
                if (!CollectPathValidator.Validate(_collectDir, out string valErr))
                    EditorGUILayout.HelpBox($"パスが無効です: {valErr}", MessageType.Error);
                else
                    EditorGUILayout.LabelField("  " + _collectDir, EditorStyles.miniLabel);
            }
            else
            {
                EditorGUILayout.LabelField("(未設定 – 収集機能は無効)", EditorStyles.miniLabel);
            }

            EditorGUILayout.Space(2);

            // "まとめて DL（収集先へ）" button.
            bool hasValidCollectDir = !string.IsNullOrEmpty(_collectDir) &&
                                      CollectPathValidator.Validate(_collectDir, out _);

            // Count eligible jobs: Completed + Done + LocalOutputDir present.
            int eligibleCount = 0;
            foreach (var job in _dispatchedJobs)
            {
                if (job.State == JobState.Completed &&
                    job.DownloadState == DownloadState.Done &&
                    !string.IsNullOrEmpty(job.LocalOutputDir))
                {
                    eligibleCount++;
                }
            }

            using (new EditorGUI.DisabledScope(!hasValidCollectDir || _isBulkCollecting))
            {
                string btnLabel = _isBulkCollecting
                    ? "収集中..."
                    : $"まとめて DL（収集先へ）[完了済み {eligibleCount} 件]";

                if (GUILayout.Button(btnLabel, GUILayout.Height(24)))
                {
                    if (!hasValidCollectDir)
                    {
                        EditorUtility.DisplayDialog(
                            "収集先を指定してください",
                            "収集先ディレクトリが設定されていないか無効です。\n" +
                            "「収集先」欄にディレクトリを指定してから実行してください。",
                            "OK");
                    }
                    else
                    {
                        BulkCollectAsync();
                    }
                }
            }

            if (!hasValidCollectDir && string.IsNullOrEmpty(_collectDir))
            {
                EditorGUILayout.LabelField(
                    "収集先を指定するとボタンが有効になります",
                    EditorStyles.miniLabel);
            }

            EditorGUILayout.Space(2);
        }

        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Draws a single job row in the distributed job list.
        ///
        /// retry-failed-collection (phase 1): no longer <c>static</c> — the "再回収"
        /// button needs to call the instance method <see cref="DownloadResultsAsync"/>.
        /// Only call site is the single <c>foreach</c> loop in
        /// <see cref="DrawDistributedJobList"/>.
        /// </summary>
        private void DrawMtrJobRow(MtrJobViewModel vm)
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);

            // Job ID short + Timeline name + Worker
            string idShort = vm.JobId.Length >= 8 ? vm.JobId.Substring(0, 8) : vm.JobId;
            EditorGUILayout.LabelField(
                $"[{vm.State}] {idShort}… {vm.TimelineName} → {vm.WorkerName}",
                GUILayout.Width(280));

            // Progress bar
            float progress;
            if (vm.TotalFrames > 0)
                progress = (float)vm.CurrentFrame / vm.TotalFrames;
            else if (vm.State == JobState.Running)
                progress = 0.5f;
            else if (vm.State == JobState.Queued || vm.State == JobState.Pending)
                progress = 0f;
            else
                progress = 1f;

            // dispatch-status-labels: use ComputeJobStatusLabel instead of the
            // previous ad-hoc barLabel switch.  This replaces "0/0" with a
            // human-readable phase string ("Editor 起動中…", "データ送信中", etc.).
            string barLabel = ComputeJobStatusLabel(vm);

            EditorGUI.ProgressBar(
                EditorGUILayout.GetControlRect(GUILayout.Width(120), GUILayout.Height(16)),
                progress, barLabel);

            // retry-failed-collection (phase 1): "再回収" button for jobs whose
            // recording succeeded but whose result pull failed. Disabled while a
            // download is already in flight (multi-fire guard).
            if (vm.State == JobState.Completed && vm.DownloadState == DownloadState.Failed)
            {
                if (GUILayout.Button("再回収", GUILayout.Width(56)))
                {
                    _ = DownloadResultsAsync(vm.Worker, vm, isManualRetry: true);
                }
            }

            // "Open" button for completed+downloaded jobs
            if (vm.State == JobState.Completed && vm.DownloadState == DownloadState.Done
                && !string.IsNullOrEmpty(vm.LocalOutputDir))
            {
                if (GUILayout.Button("開く", GUILayout.Width(40)))
                    EditorUtility.RevealInFinder(vm.LocalOutputDir);
            }

            EditorGUILayout.EndHorizontal();
        }

        // -----------------------------------------------------------------------
        // Worker version badges (worker-recorder-version-align)
        // -----------------------------------------------------------------------

        /// <summary>
        /// Draws per-Worker version badges and the "揃える" (align) button.
        ///
        /// For each enabled Worker:
        ///  - If version status is known: shows recorder / Unity version,
        ///    a red badge if recorder mismatches, a warning if Unity mismatches.
        ///  - If aligning: shows a spinner message.
        ///  - "バージョン確認" button (re-)fetches /health for all Workers.
        ///  - "揃える" button appears when recorder mismatches and Worker is reachable.
        ///    Clicking shows an EditorUtility.DisplayDialog to confirm, then sends
        ///    POST /align-recorder and starts polling /health.
        ///
        /// All async work uses the shared key (same as dispatch); if the key is not
        /// loaded the button shows an error. No ConfigureAwait(false) (main-thread).
        /// </summary>
        private void DrawWorkerVersionBadges(IReadOnlyList<WorkerInfo> workers)
        {
            EditorGUILayout.Space(4);

            // Header row
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Worker バージョン", EditorStyles.boldLabel, GUILayout.Width(140));
            if (GUILayout.Button("バージョン確認", GUILayout.Width(90)))
            {
                // Refresh local address cache at the same time as version probing
                // (DHCP address may have changed since last probe).
                _localAddressCache = SelfHostDetector.CollectLocalAddresses();
                _ = RefreshAllWorkerVersionsAsync(workers);
            }
            EditorGUILayout.EndHorizontal();

            // Iterate the raw registry workers list (not EnabledWorkers) so self-detection
            // and the delete button apply to every registered Worker, including disabled
            // ones. The raw list can contain null slots (Unity serialization), so guard
            // against null before dereferencing (plan acceptance: null elements must not throw).
            WorkerInfo workerToDelete = null;

            foreach (var worker in workers)
            {
                if (worker == null) continue;
                EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);

                // Worker name
                EditorGUILayout.LabelField(worker.displayName, GUILayout.Width(120));

                // ── Self badge (worker-registry-management) ─────────────────
                bool isSelf = SelfHostDetector.IsSelf(worker.host, LocalAddressCache);
                if (isSelf)
                {
                    var prevColor = GUI.color;
                    GUI.color = new Color(1f, 0.85f, 0.2f); // yellow-amber
                    var selfBadgeContent = new GUIContent(
                        "自分自身(self)",
                        "Master 自身を指しています。Worker 用途には別マシンの listener が必要です");
                    EditorGUILayout.LabelField(selfBadgeContent, EditorStyles.miniLabel, GUILayout.Width(90));
                    GUI.color = prevColor;
                }

                bool isAligning = _aligningWorkers.Contains(worker.displayName);

                if (isAligning)
                {
                    // Show spinner / status message
                    _alignStatusMessages.TryGetValue(worker.displayName, out string msg);
                    EditorGUILayout.LabelField(
                        string.IsNullOrEmpty(msg) ? "アライン中…" : msg,
                        EditorStyles.miniLabel);
                }
                else if (_workerVersionStatus.TryGetValue(worker.displayName, out var status))
                {
                    if (!status.Reachable)
                    {
                        // Unreachable
                        var prevColor = GUI.color;
                        GUI.color = Color.gray;
                        EditorGUILayout.LabelField("オフライン", EditorStyles.miniLabel, GUILayout.Width(60));
                        GUI.color = prevColor;
                    }
                    else
                    {
                        // Recorder version badge
                        var prevColor = GUI.color;
                        if (status.RecorderMismatch)
                            GUI.color = new Color(1f, 0.4f, 0.4f);
                        EditorGUILayout.LabelField(
                            $"Recorder: {status.WorkerRecorderVersion}",
                            EditorStyles.miniLabel,
                            GUILayout.Width(130));
                        GUI.color = prevColor;

                        // Unity version (warning only — cannot auto-align)
                        if (status.UnityMismatch)
                        {
                            GUI.color = new Color(1f, 0.8f, 0.2f);
                            EditorGUILayout.LabelField(
                                $"Unity: {status.WorkerUnityVersion} ≠ {status.MasterUnityVersion}",
                                EditorStyles.miniLabel);
                            GUI.color = prevColor;
                        }
                        else
                        {
                            EditorGUILayout.LabelField(
                                "Unity: OK",
                                EditorStyles.miniLabel,
                                GUILayout.Width(60));
                        }

                        // Align button — only when recorder mismatches
                        if (status.RecorderMismatch)
                        {
                            string targetVer = status.MasterRecorderVersion;
                            if (GUILayout.Button($"{targetVer} に揃える", GUILayout.Width(110)))
                            {
                                bool confirmed = EditorUtility.DisplayDialog(
                                    "com.unity.recorder バージョンを揃える",
                                    $"Worker '{worker.displayName}' の com.unity.recorder を\n" +
                                    $"  現在: {status.WorkerRecorderVersion}\n" +
                                    $"  目標: {targetVer}\n" +
                                    "に変更します。\n\n" +
                                    "Worker の Unity Editor がドメインリロードするため、\n" +
                                    "実行中のジョブがあれば先に完了させてください。",
                                    "揃える",
                                    "キャンセル");

                                if (confirmed)
                                    _ = AlignWorkerRecorderVersionAsync(worker, targetVer);
                            }
                        }
                        else
                        {
                            EditorGUILayout.LabelField("Recorder: OK", EditorStyles.miniLabel, GUILayout.Width(80));
                        }
                    }

                    // Last align status message
                    if (_alignStatusMessages.TryGetValue(worker.displayName, out string lastMsg)
                        && !string.IsNullOrEmpty(lastMsg))
                    {
                        EditorGUILayout.LabelField(lastMsg, EditorStyles.miniLabel);
                    }
                }
                else
                {
                    // Not yet probed
                    EditorGUILayout.LabelField("未確認 → 「バージョン確認」を押してください", EditorStyles.miniLabel);
                }

                // ── Delete button (worker-registry-management) ───────────────
                // Note: the `enabled` toggle already exists on WorkerInfo (see WorkerRegistryAsset.cs).
                // Use "無効化 (enabled=false)" to temporarily exclude a Worker from dispatch
                // without losing its registration. Use "外す" to permanently remove it.
                GUILayout.FlexibleSpace();
                var prevCol = GUI.color;
                GUI.color = new Color(1f, 0.45f, 0.45f); // light-red
                if (GUILayout.Button("外す", GUILayout.Width(40)))
                    workerToDelete = worker;
                GUI.color = prevCol;

                EditorGUILayout.EndHorizontal();
            }

            // Perform deferred deletion outside the foreach to avoid collection-modified errors.
            if (workerToDelete != null && _distWorkerRegistry != null)
                DeleteWorkerFromRegistry(_distWorkerRegistry, workerToDelete);
        }

        /// <summary>
        /// Shows a confirmation dialog and, if confirmed, removes <paramref name="worker"/>
        /// from the registry and persists the change.
        ///
        /// In-flight check: if the Worker currently has jobs in-flight, the user is warned
        /// but deletion is still allowed (Q3 decision: warn and permit).  Removing an
        /// in-flight Worker means its results will still arrive (the HTTP connection is
        /// already open), but the registry entry is gone so it will not receive new jobs
        /// after the current batch completes.
        /// </summary>
        private void DeleteWorkerFromRegistry(WorkerRegistryAsset registry, WorkerInfo worker)
        {
            // Check in-flight status
            _workerInflightCount.TryGetValue(worker.displayName, out int inflightCount);
            bool hasInflightJobs = inflightCount > 0;

            string message = hasInflightJobs
                ? $"Worker '{worker.displayName}' ({worker.host}:{worker.port}) を登録から外しますか?\n\n" +
                  $"⚠ この Worker には現在 {inflightCount} 件の実行中ジョブがあります。\n" +
                  "削除しても実行中のジョブは継続しますが、結果回収後に再登録が必要です。"
                : $"Worker '{worker.displayName}' ({worker.host}:{worker.port}) を登録から外しますか?\n\n" +
                  "この操作は元に戻せません。再登録するには SetupHub から再度追加してください。";

            bool confirmed = EditorUtility.DisplayDialog(
                "Worker を登録から外す",
                message,
                "外す",
                "キャンセル");

            if (!confirmed)
                return;

            // Remove from registry using pure helper (reference equality)
            int removed = WorkerRegistryOperations.RemoveWorker(registry.workers, worker);
            if (removed > 0)
            {
                // Clear version status cache for the removed Worker
                _workerVersionStatus.Remove(worker.displayName);
                _alignStatusMessages.Remove(worker.displayName);
                _aligningWorkers.Remove(worker.displayName);

                // Persist to disk
                EditorUtility.SetDirty(registry);
                AssetDatabase.SaveAssets();

                Debug.Log($"[DistributedRecorder] Worker を登録から外しました: " +
                          $"{worker.displayName} ({worker.host}:{worker.port})");
                Repaint();
            }
        }

        /// <summary>
        /// Fetches /health for all <paramref name="workers"/> in sequence and caches
        /// the results in <see cref="_workerVersionStatus"/>.
        ///
        /// Uses the batch shared key if a batch is active, otherwise loads the key once.
        /// Does NOT use ConfigureAwait(false) — continuations must stay on the main thread.
        /// </summary>
        private async Task RefreshAllWorkerVersionsAsync(IReadOnlyList<WorkerInfo> workers)
        {
            if (!SharedKeyLoader.TryLoad(out byte[] keyBytes, out string keyError))
            {
                Debug.LogError($"[DistributedRecorder] バージョン確認: 共有キーのロードに失敗しました: {keyError}");
                return;
            }

            using var transport  = new HttpTransport(new HmacAuthenticator(keyBytes));
            var       dispatcher = new JobDispatcher(transport, ProjectPaths.ProjectRoot);

            foreach (var worker in workers)
            {
                if (worker == null) continue;
                var status = await dispatcher.GetWorkerVersionStatusAsync(worker);
                _workerVersionStatus[worker.displayName] = status;
                Repaint();
            }
        }

        /// <summary>
        /// Sends POST /align-recorder to <paramref name="worker"/>, then polls /health
        /// until the version matches <paramref name="targetVersion"/> or a timeout elapses.
        ///
        /// Must be called from the main thread (no ConfigureAwait(false)).
        /// Shows a final success/error dialog when done.
        /// </summary>
        private async Task AlignWorkerRecorderVersionAsync(WorkerInfo worker, string targetVersion)
        {
            if (!SharedKeyLoader.TryLoad(out byte[] keyBytes, out string keyError))
            {
                EditorUtility.DisplayDialog(
                    "アラインエラー",
                    $"共有キーのロードに失敗しました: {keyError}",
                    "OK");
                return;
            }

            _aligningWorkers.Add(worker.displayName);
            _alignStatusMessages[worker.displayName] = "POST /align-recorder 送信中…";
            Repaint();

            using var transport  = new HttpTransport(new HmacAuthenticator(keyBytes));
            var       dispatcher = new JobDispatcher(transport, ProjectPaths.ProjectRoot);

            var (result, message) = await dispatcher.AlignRecorderAsync(
                worker, targetVersion,
                progressMsg =>
                {
                    _alignStatusMessages[worker.displayName] = progressMsg;
                    Repaint();
                });

            _aligningWorkers.Remove(worker.displayName);

            switch (result)
            {
                case JobDispatcher.AlignResult.Success:
                    _alignStatusMessages[worker.displayName] = $"完了: {message}";
                    // Refresh badge
                    var updatedStatus = await dispatcher.GetWorkerVersionStatusAsync(worker);
                    _workerVersionStatus[worker.displayName] = updatedStatus;
                    break;

                case JobDispatcher.AlignResult.Rejected:
                    _alignStatusMessages[worker.displayName] = $"拒否: {message}";
                    EditorUtility.DisplayDialog(
                        "アライン拒否",
                        $"Worker '{worker.displayName}' がリクエストを拒否しました:\n{message}",
                        "OK");
                    break;

                case JobDispatcher.AlignResult.Timeout:
                    _alignStatusMessages[worker.displayName] = $"タイムアウト: {message}";
                    EditorUtility.DisplayDialog(
                        "アラインタイムアウト",
                        message,
                        "OK");
                    break;

                case JobDispatcher.AlignResult.NetworkError:
                    _alignStatusMessages[worker.displayName] = $"ネットワークエラー: {message}";
                    EditorUtility.DisplayDialog(
                        "アラインエラー",
                        $"Worker '{worker.displayName}' との通信に失敗しました:\n{message}",
                        "OK");
                    break;
            }

            Repaint();
        }

        // -----------------------------------------------------------------------
        // Worker git sync (worker-git-sync, v1.4.11)
        // -----------------------------------------------------------------------

        /// <summary>
        /// Sends POST /git-sync to each online Worker that is on the same git branch
        /// as the master.  Workers on a different branch (or unreachable Workers) are
        /// skipped to prevent accidental cross-branch sync.
        ///
        /// Security:
        ///  - Branch name is never sent in the request body.  The Worker uses its own
        ///    local git state, preventing injection.
        ///  - A confirmation dialog is shown before any destructive operation.
        ///  - Wire-compat: old Workers (< v1.4.11) return 404 and are skipped.
        ///
        /// Must NOT use ConfigureAwait(false) — continuations must stay on the main
        /// thread for Unity API calls (Repaint, EditorUtility.DisplayDialog).
        /// </summary>
        private async Task SyncWorkersToLatestAsync()
        {
            if (_syncingWorkers)
                return;

            // 1. Determine master's current branch.
            string masterBranch;
            if (!DistributedRecorder.Shared.GitInfo.TryGetCurrentBranch(
                    ProjectPaths.ProjectRoot, out masterBranch, out string branchErr))
            {
                EditorUtility.DisplayDialog(
                    "ブランチ取得失敗",
                    $"Master の現在のブランチを取得できませんでした:\n{branchErr}\n\n" +
                    "git リポジトリ内でのみ使用できます。",
                    "OK");
                return;
            }

            // 2. Load shared key.
            if (!SharedKeyLoader.TryLoad(out byte[] keyBytes, out string keyError))
            {
                EditorUtility.DisplayDialog(
                    "共有キーエラー",
                    $"共有キーのロードに失敗しました: {keyError}",
                    "OK");
                return;
            }

            using var transport  = new HttpTransport(new HmacAuthenticator(keyBytes));
            var       dispatcher = new JobDispatcher(transport, ProjectPaths.ProjectRoot);

            // 3. Probe each enabled Worker's gitBranch.
            var workers = _distWorkerRegistry?.EnabledWorkers;
            if (workers == null || workers.Count == 0)
            {
                EditorUtility.DisplayDialog(
                    "Worker が見つかりません",
                    "有効な Worker がレジストリに登録されていません。",
                    "OK");
                return;
            }

            _syncResultMessage = "ブランチ確認中…";
            Repaint();

            var samebranchWorkers = new List<WorkerInfo>();
            var skipMessages      = new System.Text.StringBuilder();

            foreach (var worker in workers)
            {
                if (worker == null) continue;
                string workerBranch = await dispatcher.GetWorkerGitBranchAsync(worker);

                if (string.IsNullOrEmpty(workerBranch))
                {
                    // Unreachable or no git info — skip.
                    skipMessages.AppendLine(
                        $"  {worker.displayName}: ブランチ取得不可（オフラインまたは旧バージョン）→ スキップ");
                    Debug.Log(
                        $"[DistributedRecorder] git sync: skipping '{worker.displayName}' " +
                        "(unreachable or pre-v1.4.11 Worker).");
                    continue;
                }

                if (!string.Equals(workerBranch, masterBranch, StringComparison.Ordinal))
                {
                    // Different branch — skip to prevent accidental sync.
                    skipMessages.AppendLine(
                        $"  {worker.displayName}: ブランチ違い (Worker={workerBranch}, Master={masterBranch}) → スキップ");
                    Debug.Log(
                        $"[DistributedRecorder] git sync: skipping '{worker.displayName}' " +
                        $"(branch mismatch: worker='{workerBranch}', master='{masterBranch}').");
                    continue;
                }

                samebranchWorkers.Add(worker);
            }

            if (samebranchWorkers.Count == 0)
            {
                string skipInfo = skipMessages.Length > 0
                    ? $"\n\nスキップされた Worker:\n{skipMessages}"
                    : string.Empty;
                EditorUtility.DisplayDialog(
                    "同期対象なし",
                    $"Master と同じブランチ '{masterBranch}' の Worker がありませんでした。{skipInfo}",
                    "OK");
                _syncResultMessage = string.Empty;
                Repaint();
                return;
            }

            // 4. Confirmation dialog (destructive: discards local changes on Workers).
            string skipInfo2 = skipMessages.Length > 0
                ? $"\n\n以下はスキップされます:\n{skipMessages}"
                : string.Empty;

            string targetList = string.Join(
                "\n", samebranchWorkers.Select(w => $"  • {w.displayName}"));

            bool confirmed = EditorUtility.DisplayDialog(
                "Worker を最新に同期",
                $"以下の Worker を最新コミットに同期しますか？\n\n{targetList}\n\n" +
                $"ブランチ: {masterBranch}\n" +
                "実行内容: git fetch origin → git reset --hard origin/<branch>\n\n" +
                "この操作は Worker のローカルの未コミット変更を破棄します。\n" +
                "取り消せません。" + skipInfo2,
                "同期する",
                "キャンセル");

            if (!confirmed)
            {
                _syncResultMessage = string.Empty;
                Repaint();
                return;
            }

            // 5. Send /git-sync to each same-branch Worker.
            _syncingWorkers    = true;
            _syncResultMessage = "同期中…";
            Repaint();

            var results      = new System.Text.StringBuilder();
            int successCount = 0;
            int failCount    = 0;

            foreach (var worker in samebranchWorkers)
            {
                _syncResultMessage = $"{worker.displayName} に同期リクエスト送信中…";
                Repaint();

                var (accepted, message, summary) = await dispatcher.SendGitSyncAsync(worker);

                if (accepted)
                {
                    successCount++;
                    Debug.Log(
                        $"[DistributedRecorder] git sync: '{worker.displayName}' accepted. {summary}");
                    results.AppendLine($"  {worker.displayName}: 同期開始 (202 Accepted)");
                }
                else
                {
                    failCount++;
                    Debug.LogWarning(
                        $"[DistributedRecorder] git sync: '{worker.displayName}' failed: {message}");
                    results.AppendLine($"  {worker.displayName}: 失敗 ({message})");
                }
            }

            _syncingWorkers    = false;
            _syncResultMessage = $"同期完了: {successCount} 件成功, {failCount} 件失敗";

            if (failCount > 0)
            {
                EditorUtility.DisplayDialog(
                    "同期結果",
                    $"同期リクエストを送信しました。\n\n{results}\n" +
                    "Worker の Console ログで結果を確認してください。\n" +
                    "（git reset --hard はバックグラウンドで実行されます。ドメインリロードが発生する場合があります。）",
                    "OK");
            }
            else
            {
                Debug.Log(
                    $"[DistributedRecorder] git sync: all {successCount} Worker(s) accepted sync request.\n" +
                    results);
            }

            Repaint();
        }

        // -----------------------------------------------------------------------
        // sync-before-dispatch (v1.4.14) – pre-flight git sync gate
        // -----------------------------------------------------------------------

        // How long to wait for each re-health poll after sending /git-sync.
        private static readonly TimeSpan SyncRePollInterval = TimeSpan.FromSeconds(5);
        // Total time to wait for workers to converge before giving up and falling
        // through to the per-job CommitMismatch "send anyway" path.
        private static readonly TimeSpan SyncRePollTimeout  = TimeSpan.FromSeconds(90);

        /// <summary>
        /// Worker classification used by <see cref="RunPreFlightSyncGateAsync"/>.
        /// Internal so EditMode tests can reference it via InternalsVisibleTo.
        /// </summary>
        internal enum WorkerSyncClass
        {
            /// <summary>Same branch, same commit — already in sync.</summary>
            Synced,
            /// <summary>Same branch, different commit — sync candidate.</summary>
            NeedsSync,
            /// <summary>Different branch from master — skip (never cross-branch reset).</summary>
            DifferentBranch,
            /// <summary>gitCommitShort empty (pre-v1.4.11 worker or git unavailable) — skip.</summary>
            CommitUnknown,
        }

        /// <summary>
        /// Pure-function worker classifier for the pre-flight sync gate.
        ///
        /// Compares 8-character short SHAs (case-insensitive).  Both inputs are
        /// trimmed and compared with <see cref="StringComparison.OrdinalIgnoreCase"/>
        /// because <c>gitCommitShort</c> from the Worker may use a different case
        /// than the master-side abbreviation.
        ///
        /// This is a pure static helper with no side effects — directly EditMode-testable.
        ///
        /// Security: no external input is used as a git argument here; classification
        /// is purely in-process string comparison.
        /// </summary>
        /// <param name="masterBranch">Branch currently checked out on the master.</param>
        /// <param name="masterCommitFull">Full 40-char HEAD SHA of the master.</param>
        /// <param name="workerBranch">Branch reported by the Worker's /health.</param>
        /// <param name="workerCommitShort">Short (8-char) commit SHA from Worker /health.</param>
        /// <returns>The appropriate <see cref="WorkerSyncClass"/> for this Worker.</returns>
        internal static WorkerSyncClass ClassifyWorkerSync(
            string masterBranch, string masterCommitFull,
            string workerBranch, string workerCommitShort)
        {
            // (d) No commit info from worker → treat as unknown.
            if (string.IsNullOrEmpty(workerCommitShort))
                return WorkerSyncClass.CommitUnknown;

            // (c) Different branch → skip (never cross-branch reset).
            if (!string.Equals(workerBranch, masterBranch, StringComparison.Ordinal))
                return WorkerSyncClass.DifferentBranch;

            // Abbreviate master commit to the same length as workerCommitShort for comparison.
            // Worker reports 8 chars; master HEAD is 40 chars.
            string workerShort = workerCommitShort.Trim();
            int    abbrevLen   = workerShort.Length;  // typically 8
            string masterShort = masterCommitFull.Length >= abbrevLen
                ? masterCommitFull.Substring(0, abbrevLen)
                : masterCommitFull;

            // (a) Commits match → already synced.
            // (b) Commits differ → needs sync.
            return string.Equals(masterShort, workerShort, StringComparison.OrdinalIgnoreCase)
                ? WorkerSyncClass.Synced
                : WorkerSyncClass.NeedsSync;
        }

        /// <summary>
        /// Executes the pre-flight git sync gate for <paramref name="onlineWorkers"/>.
        ///
        /// Returns <c>true</c> when dispatch should be aborted (user cancelled or
        /// master-ahead guard blocked), <c>false</c> when dispatch may proceed.
        ///
        /// Design (sync-before-dispatch plan.md 案3):
        ///   A. Master ahead-count guard: block if master has unpushed commits.
        ///   B. Classify each online worker.
        ///   C. Sync candidates: confirmation dialog → /git-sync → re-health polling.
        ///   D. User declines → abort (return true).
        ///
        /// Must NOT use ConfigureAwait(false): called from StartDistributedRecordingInternalAsync
        /// which is on the Unity main thread (EditorUtility.DisplayDialog, Repaint).
        /// </summary>
        private async Task<bool> RunPreFlightSyncGateAsync(
            List<WorkerInfo> onlineWorkers, string barTitle)
        {
            // ── A. Master ahead-count guard ──────────────────────────────────────
            // Obtain master branch + HEAD commit (both needed for worker classification too).
            bool masterGitAvailable = false;
            string masterBranch      = string.Empty;
            string masterCommitFull  = string.Empty;

            string branchErr;
            bool gotBranch = GitInfo.TryGetCurrentBranch(
                ProjectPaths.ProjectRoot, out masterBranch, out branchErr);

            string commitErr;
            bool gotCommit = GitInfo.TryGetHeadCommit(
                ProjectPaths.ProjectRoot, out masterCommitFull, out commitErr);

            masterGitAvailable = gotBranch && gotCommit;

            if (masterGitAvailable)
            {
                // Check whether master has unpushed commits.
                int aheadCount;
                string aheadErr;
                bool gotAhead = GitInfo.TryGetAheadCount(
                    ProjectPaths.ProjectRoot, masterBranch, out aheadCount, out aheadErr);

                if (gotAhead && aheadCount > 0)
                {
                    // Master is ahead of origin: syncing workers would not make them
                    // match master HEAD (they can only reach origin/<branch>).
                    // Block and prompt the user to push first.
                    EditorUtility.DisplayDialog(
                        "Master が未 push です",
                        $"Master は origin/{masterBranch} より {aheadCount} コミット先行しています。\n\n" +
                        "Worker を同期しても Master と同じコミットにはなりません\n" +
                        "（Worker は origin/<branch> にしか揃えられないため）。\n\n" +
                        "先に Master を commit & push してから再度「分散処理を開始」を押してください。",
                        "OK");
                    Debug.LogWarning(
                        $"[DistributedRecorder] sync-gate: master is {aheadCount} commit(s) ahead of " +
                        $"origin/{masterBranch}. Dispatch blocked until master is pushed.");
                    return true; // abort
                }
                // gotAhead=false means upstream ref absent (never pushed / no origin configured):
                // treat as "unknown" and skip the ahead guard — fall through to worker classification.
                if (!gotAhead)
                {
                    Debug.Log(
                        $"[DistributedRecorder] sync-gate: ahead-count check failed " +
                        $"({aheadErr}). Skipping master-ahead guard.");
                }
            }
            else
            {
                // git not available on master or not a git repo — skip entire sync gate.
                Debug.Log(
                    "[DistributedRecorder] sync-gate: master git not available " +
                    $"(branch:{branchErr} / commit:{commitErr}). Skipping sync gate.");
                return false; // proceed with dispatch as-is
            }

            // ── B. Classify each online worker ───────────────────────────────────
            EditorUtility.DisplayCancelableProgressBar(
                barTitle, "Worker のコミットを確認しています…", 0.78f);

            var needsSyncWorkers    = new List<WorkerInfo>();
            var diffBranchWorkers   = new List<WorkerInfo>();
            var unknownCommitWorkers = new List<WorkerInfo>();

            foreach (var worker in onlineWorkers)
            {
                var status = await _batchDispatcher.ProbeWithCommitAsync(worker);
                if (!status.Online)
                {
                    // fell offline between initial probe and now — skip silently.
                    Debug.LogWarning(
                        $"[DistributedRecorder] sync-gate: '{worker.displayName}' went offline. Skipping.");
                    continue;
                }

                WorkerSyncClass cls = ClassifyWorkerSync(
                    masterBranch, masterCommitFull,
                    status.Branch, status.CommitShort);

                switch (cls)
                {
                    case WorkerSyncClass.Synced:
                        Debug.Log(
                            $"[DistributedRecorder] sync-gate: '{worker.displayName}' commit matches master. OK.");
                        break;

                    case WorkerSyncClass.NeedsSync:
                        Debug.Log(
                            $"[DistributedRecorder] sync-gate: '{worker.displayName}' commit mismatch " +
                            $"(worker={status.CommitShort}, master={masterCommitFull.Substring(0, System.Math.Min(8, masterCommitFull.Length))}). " +
                            "Queued for sync.");
                        needsSyncWorkers.Add(worker);
                        break;

                    case WorkerSyncClass.DifferentBranch:
                        Debug.LogWarning(
                            $"[DistributedRecorder] sync-gate: '{worker.displayName}' is on branch " +
                            $"'{status.Branch}' (master: '{masterBranch}'). Skipping sync.");
                        diffBranchWorkers.Add(worker);
                        break;

                    case WorkerSyncClass.CommitUnknown:
                        Debug.LogWarning(
                            $"[DistributedRecorder] sync-gate: '{worker.displayName}' commit unknown " +
                            "(pre-v1.4.11 worker or git unavailable). Skipping sync.");
                        unknownCommitWorkers.Add(worker);
                        break;
                }
            }

            // If nothing needs sync, proceed.
            if (needsSyncWorkers.Count == 0)
            {
                // Build info about skipped workers for the log.
                if (diffBranchWorkers.Count > 0 || unknownCommitWorkers.Count > 0)
                {
                    var skippedLog = new System.Text.StringBuilder();
                    foreach (var w in diffBranchWorkers)
                        skippedLog.AppendLine(
                            $"  {w.displayName}: 別ブランチ → スキップ（commit mismatch は per-job で対処）");
                    foreach (var w in unknownCommitWorkers)
                        skippedLog.AppendLine(
                            $"  {w.displayName}: commit 不明 → スキップ（旧 Worker）");
                    Debug.Log(
                        $"[DistributedRecorder] sync-gate: no workers need sync.\n{skippedLog}");
                }
                return false; // proceed
            }

            // ── C. Confirmation dialog for sync ──────────────────────────────────
            string masterShortForDisplay =
                masterCommitFull.Length >= 8 ? masterCommitFull.Substring(0, 8) : masterCommitFull;

            string targetList = string.Join(
                "\n", needsSyncWorkers.Select(w => $"  • {w.displayName}"));

            var skipWarnSb = new System.Text.StringBuilder();
            foreach (var w in diffBranchWorkers)
                skipWarnSb.AppendLine(
                    $"  {w.displayName}: 別ブランチ（{masterBranch} ではない）→ スキップ");
            foreach (var w in unknownCommitWorkers)
                skipWarnSb.AppendLine(
                    $"  {w.displayName}: commit 不明（旧 Worker / git なし）→ スキップ");

            string skipSection = skipWarnSb.Length > 0
                ? $"\n\n以下はスキップされます:\n{skipWarnSb}"
                : string.Empty;

            bool confirmed = EditorUtility.DisplayDialog(
                "Worker のコミットがずれています",
                $"以下の {needsSyncWorkers.Count} 台の Worker が Master と異なるコミットです。\n\n" +
                $"Master commit: {masterShortForDisplay}…\n\n" +
                $"{targetList}\n\n" +
                "同期しますか？\n" +
                "（git fetch origin → git reset --hard origin/<branch>）\n" +
                "Worker のローカルの未コミット変更は破棄されます。取り消せません。" +
                skipSection,
                "同期する",
                "キャンセル（ディスパッチ中止）");

            if (!confirmed)
            {
                Debug.Log(
                    "[DistributedRecorder] sync-gate: user declined sync. Aborting dispatch.");
                return true; // abort
            }

            // ── Send /git-sync to each NeedsSync worker ──────────────────────────
            EditorUtility.DisplayCancelableProgressBar(
                barTitle, "Worker を同期中…", 0.82f);

            foreach (var worker in needsSyncWorkers)
            {
                EditorUtility.DisplayCancelableProgressBar(
                    barTitle, $"{worker.displayName} に同期リクエスト送信中…", 0.84f);

                var (accepted, msg, summary) = await _batchDispatcher.SendGitSyncAsync(worker);
                if (accepted)
                {
                    Debug.Log(
                        $"[DistributedRecorder] sync-gate: '{worker.displayName}' accepted /git-sync. {summary}");
                }
                else
                {
                    Debug.LogWarning(
                        $"[DistributedRecorder] sync-gate: '{worker.displayName}' rejected /git-sync: {msg}. " +
                        "Will fall through to per-job send-anyway.");
                }
            }

            // ── Re-health polling: wait for commits to converge ──────────────────
            // /git-sync is async (202): worker runs fetch+reset in background.
            // We poll /health until commits match or SyncRePollTimeout elapses.
            // Workers that still mismatch after timeout are left in _batchOnlineWorkers
            // so they get the per-job CommitMismatch "send anyway" dialog (F7 fallback).
            EditorUtility.DisplayCancelableProgressBar(
                barTitle, "Worker の同期完了を待機中…", 0.86f);

            var deadline = DateTime.UtcNow + SyncRePollTimeout;
            var stillWaiting = new HashSet<string>(
                needsSyncWorkers.Select(w => w.displayName));

            while (stillWaiting.Count > 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay((int)SyncRePollInterval.TotalMilliseconds);

                var converged = new List<string>();
                foreach (var worker in needsSyncWorkers)
                {
                    if (!stillWaiting.Contains(worker.displayName)) continue;

                    var reStatus = await _batchDispatcher.ProbeWithCommitAsync(worker);
                    if (!reStatus.Online)
                    {
                        // Worker went offline during reset (domain reload). Keep waiting.
                        Debug.Log(
                            $"[DistributedRecorder] sync-gate poll: '{worker.displayName}' offline " +
                            "(may be domain-reloading). Retrying…");
                        continue;
                    }

                    WorkerSyncClass reCls = ClassifyWorkerSync(
                        masterBranch, masterCommitFull,
                        reStatus.Branch, reStatus.CommitShort);

                    if (reCls == WorkerSyncClass.Synced)
                    {
                        Debug.Log(
                            $"[DistributedRecorder] sync-gate: '{worker.displayName}' synced OK " +
                            $"({reStatus.CommitShort}).");
                        converged.Add(worker.displayName);
                    }
                }

                foreach (string name in converged)
                    stillWaiting.Remove(name);
            }

            if (stillWaiting.Count > 0)
            {
                // Some workers did not converge within the timeout.
                // Log a warning and let per-job CommitMismatch fallback handle them.
                string notSynced = string.Join(", ", stillWaiting);
                Debug.LogWarning(
                    $"[DistributedRecorder] sync-gate: {stillWaiting.Count} worker(s) did not converge " +
                    $"within {SyncRePollTimeout.TotalSeconds}s: {notSynced}. " +
                    "Per-job 'send anyway' fallback will handle them.");
            }

            return false; // proceed with dispatch
        }

        // -----------------------------------------------------------------------
        // Lazy initialization
        // -----------------------------------------------------------------------

        private bool _distributedSectionInitialized;

        private void InitDistributedSection()
        {
            _distributedSectionInitialized = true;
            _distributedMode = EditorPrefs.GetBool(PrefKeyDistMode, false);

            string guid = EditorPrefs.GetString(PrefKeyDistRegistry, string.Empty);
            if (!string.IsNullOrEmpty(guid))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(path))
                    _distWorkerRegistry = AssetDatabase.LoadAssetAtPath<WorkerRegistryAsset>(path);
            }

            // collect-to-dir (v1.4.10): restore persisted collect dir.
            _collectDir = EditorPrefs.GetString(PrefKeyCollectDir, string.Empty);
        }

        private void PersistDistributedState()
        {
            EditorPrefs.SetBool(PrefKeyDistMode, _distributedMode);
            if (_distWorkerRegistry != null)
            {
                string path = AssetDatabase.GetAssetPath(_distWorkerRegistry);
                string guid = AssetDatabase.AssetPathToGUID(path);
                EditorPrefs.SetString(PrefKeyDistRegistry, guid);
            }

            // collect-to-dir (v1.4.10): persist collect dir.
            EditorPrefs.SetString(PrefKeyCollectDir, _collectDir ?? string.Empty);
        }

        // -----------------------------------------------------------------------
        // Target collection
        // -----------------------------------------------------------------------

        /// <summary>
        /// Collects render-targets from the currently selected + enabled Timeline
        /// directors.  <see cref="RecorderSettingsType.Image"/> and
        /// <see cref="RecorderSettingsType.Movie"/> items are included; other types
        /// (AOV / FBX / Animation / Alembic) are skipped with a log message.
        ///
        /// Movie note: 1 Timeline = 1 Job = 1 Machine. Frame-range splitting is not
        /// applied (WholeJobSplitter only). This is mandatory for simulation-dependent
        /// content (MagicaCloth2 etc.) and for video file coherence.
        /// Refs: movie-recorder-support §A / plan.md F5
        /// </summary>
        internal List<DistributedTimelineJob> CollectRenderTargets()
        {
            var result = new List<DistributedTimelineJob>();

            if (selectedDirectorIndices == null || recordingQueueDirectors == null)
                return result;

            string activeScenePath = SceneManager.GetActiveScene().path;

            // Content-hash (jobScopeHash) is only a fallback for non-git projects. When a
            // git commit is available the Worker verifies by commit, so we skip the
            // expensive per-job AssetDatabase.GetDependencies hashing — that whole-deps
            // scan was the main cause of the dispatch-time Editor freeze. Checked once
            // here (git rev-parse is ~tens of ms).
            bool gitAvailable = DistributedRecorder.Shared.GitInfo.TryGetHeadCommit(
                ProjectPaths.ProjectRoot, out _, out _);

            foreach (int idx in selectedDirectorIndices)
            {
                if (idx < 0 || idx >= recordingQueueDirectors.Count)
                    continue;

                var director = recordingQueueDirectors[idx];
                if (director == null)
                    continue;

                var config = GetTimelineRecorderConfig(idx);
                var enabledItems = config.GetEnabledRecorders();

                // Find the first supported item (Image or Movie).
                // AOV / FBX / Animation / Alembic remain unsupported and are skipped.
                MultiRecorderConfig.RecorderConfigItem supportedItem = null;
                foreach (var item in enabledItems)
                {
                    if (IsSupportedRecorderItem(item))
                    {
                        supportedItem = item;
                        break;
                    }
                    // Unsupported types: log skip
                    Debug.Log(
                        $"[DistributedRecorder] Timeline '{director.gameObject.name}': " +
                        $"Recorder '{item.name}' (type={item.recorderType}) は未対応形式のためスキップします。" +
                        " 現在は Image Sequence および Movie に対応しています。");
                }

                if (supportedItem == null)
                    continue;

                // Resolve recording range via SignalEmitter or full timeline
                var timelineAsset = director.playableAsset as TimelineAsset;
                double startT = 0.0;
                double endT   = 0.0;
                if (timelineAsset != null)
                {
                    RecordingRange range;
                    if (useSignalEmitterTiming)
                    {
                        range = SignalEmitterRecordControl.GetRecordingRangeFromSignalsWithFallback(
                            timelineAsset, startTimingName, endTimingName, allowFallback: true);
                    }
                    else
                    {
                        range = SignalEmitterRecordControl.GetFullTimelineRange(timelineAsset);
                    }

                    if (range.isValid)
                    {
                        startT = range.startTime;
                        endT   = range.endTime;
                    }
                    else if (timelineAsset.duration > 0)
                    {
                        startT = 0.0;
                        endT   = timelineAsset.duration;
                    }
                }

                // Resolve hierarchy path
                string hierarchyPath = BuildHierarchyPath(director.gameObject);

                // Map RecorderConfigItem → RecorderJobConfig (legacy DTO, kept for backward compat)
                var jobConfig = MapToRecorderJobConfig(supportedItem);

                // Resolve Timeline asset path
                string timelineAssetPath = string.Empty;
                if (director.playableAsset != null)
                    timelineAssetPath = AssetDatabase.GetAssetPath(director.playableAsset);

                // --- MTR fidelity: resolve new fields ---

                // Serialize full config (without Object references; they are sent as path/GUID)
                string recorderConfigJson = JsonUtility.ToJson(supportedItem);

                // Resolve camera reference to hierarchy path / name
                string targetCameraHierarchyPath = string.Empty;
                string targetCameraName = string.Empty;
                if (supportedItem.imageSourceType == ImageRecorderSourceType.TargetCamera &&
                    supportedItem.imageTargetCamera != null)
                {
                    var camGo = supportedItem.imageTargetCamera.gameObject;
                    targetCameraHierarchyPath = BuildHierarchyPath(camGo);
                    targetCameraName          = camGo.name;
                }

                // Resolve RenderTexture to GUID
                string renderTextureGuid = string.Empty;
                if (supportedItem.imageSourceType == ImageRecorderSourceType.RenderTexture &&
                    supportedItem.imageRenderTexture != null)
                {
                    string rtPath = AssetDatabase.GetAssetPath(supportedItem.imageRenderTexture);
                    if (!string.IsNullOrEmpty(rtPath))
                        renderTextureGuid = AssetDatabase.AssetPathToGUID(rtPath);
                }

                // Resolve effective width/height respecting global/per-item rule
                // MTR: settings.useGlobalResolution controls whether the global width/height is used
                var globalSettings = MultiTimelineRecorderSettings.LoadOrCreateSettings();
                int effectiveWidth;
                int effectiveHeight;
                if (globalSettings != null && globalSettings.multiRecorderConfig != null &&
                    globalSettings.multiRecorderConfig.useGlobalResolution)
                {
                    effectiveWidth  = globalSettings.width;
                    effectiveHeight = globalSettings.height;
                }
                else
                {
                    effectiveWidth  = supportedItem.width;
                    effectiveHeight = supportedItem.height;
                }

                // Effective frame rate always comes from global MTR settings (same as
                // CreateImageRecorderSettingsFromConfig which uses `this.settings.frameRate`)
                double effectiveFrameRate = globalSettings != null ? globalSettings.frameRate : supportedItem.frameRate;

                // Resolve output path:
                //  - Image: process Take/Scene wildcards; preserve <Frame> for Recorder
                //  - Movie: process all wildcards; no <Frame> wildcard (single output file)
                string resolvedOutputRelativePath = ResolveOutputRelativePath(
                    supportedItem, activeScenePath, director);

                // Compute job-scope hash (timeline + deps + scene only, no whole-Assets scan).
                // Skipped when git is available — the Worker verifies by commit, and this
                // per-job dependency hashing is the heaviest synchronous dispatch-time work.
                string jobScopeHash = string.Empty;
                if (!gitAvailable && !string.IsNullOrEmpty(timelineAssetPath) && !string.IsNullOrEmpty(activeScenePath))
                {
                    try
                    {
                        jobScopeHash = ProjectHasher.ComputeJobScope(
                            timelineAssetPath, activeScenePath);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning(
                            $"[DistributedRecorder] ジョブスコープハッシュの計算に失敗しました " +
                            $"(timeline={timelineAssetPath}): {ex.Message}");
                    }
                }

                result.Add(new DistributedTimelineJob
                {
                    Director                  = director,
                    TimelineAssetPath         = timelineAssetPath,
                    DirectorObjectName        = director.gameObject.name,
                    DirectorHierarchyPath     = hierarchyPath,
                    JobConfig                 = jobConfig,
                    StartTime                 = startT,
                    EndTime                   = endT,
                    ScenePath                 = activeScenePath,
                    // MTR fidelity fields
                    RecorderConfigJson        = recorderConfigJson,
                    TargetCameraHierarchyPath = targetCameraHierarchyPath,
                    TargetCameraName          = targetCameraName,
                    RenderTextureGuid         = renderTextureGuid,
                    EffectiveWidth            = effectiveWidth,
                    EffectiveHeight           = effectiveHeight,
                    EffectiveFrameRate        = effectiveFrameRate,
                    ResolvedOutputRelativePath = resolvedOutputRelativePath,
                    JobScopeHash              = jobScopeHash
                });
            }

            return result;
        }

        // -----------------------------------------------------------------------
        // RecorderConfigItem → RecorderJobConfig mapping
        // -----------------------------------------------------------------------

        /// <summary>
        /// Maps a <see cref="MultiRecorderConfig.RecorderConfigItem"/> to a
        /// <see cref="RecorderJobConfig"/>.  Supports Image and Movie types.
        /// Call <see cref="IsSupportedRecorderItem"/> before calling this method.
        ///
        /// Movie items produce a lossy DTO (recorderType = Image is a placeholder;
        /// the real type travels via <c>recorderConfigJson</c>). This is intentional
        /// — the lossy DTO is deprecated and the wire path uses recorderConfigJson.
        ///
        /// Made <c>public static</c> so the hermetic EditMode tests can reach it
        /// without instantiating the EditorWindow.
        /// </summary>
        public static RecorderJobConfig MapToRecorderJobConfig(
            MultiRecorderConfig.RecorderConfigItem item)
        {
            if (item == null)
                throw new ArgumentNullException(nameof(item));

            // movie-recorder-support: carry the recorder type in the Shared DTO so the
            // Worker (JobRunner, in DistributedRecorder.Editor) can branch on Image vs Movie
            // WITHOUT referencing Unity.MultiTimelineRecorder types across the asmdef boundary
            // (that assembly references DistributedRecorder.Editor, so a reference back would
            // be circular). The full per-type settings still travel in recorderConfigJson;
            // this is only the lightweight discriminator.
            var distRecorderType = item.recorderType == RecorderSettingsType.Movie
                ? DistRecorderType.Movie
                : DistRecorderType.Image;
            return new RecorderJobConfig
            {
                recorderType     = distRecorderType,
                width            = item.width,
                height           = item.height,
                frameRate        = item.frameRate,
                takeNumber       = item.takeNumber,
                fileNameTemplate = item.fileName,
                imageFormat      = ConvertImageFormat(item.imageFormat),
                captureAlpha     = item.captureAlpha
            };
        }

        /// <summary>
        /// Returns <c>true</c> when the item uses a recorder type that is currently
        /// supported for distributed dispatch (Image Sequence or Movie).
        ///
        /// AOV / FBX / Animation / Alembic remain unsupported and return false.
        ///
        /// Made <c>public static</c> for hermetic tests.
        /// </summary>
        public static bool IsSupportedRecorderItem(MultiRecorderConfig.RecorderConfigItem item)
        {
            if (item == null) return false;
            return item.recorderType == RecorderSettingsType.Image
                || item.recorderType == RecorderSettingsType.Movie;
        }

        /// <summary>
        /// Returns <c>true</c> when the item is an Image Sequence recorder.
        /// Kept for backward compatibility with existing tests and callers.
        /// New code should prefer <see cref="IsSupportedRecorderItem"/>.
        /// </summary>
        public static bool IsImageRecorderItem(MultiRecorderConfig.RecorderConfigItem item)
        {
            if (item == null) return false;
            return item.recorderType == RecorderSettingsType.Image;
        }

        private static DistImageFormat ConvertImageFormat(
            ImageRecorderSettings.ImageRecorderOutputFormat format)
        {
            switch (format)
            {
                case ImageRecorderSettings.ImageRecorderOutputFormat.JPEG: return DistImageFormat.JPEG;
                case ImageRecorderSettings.ImageRecorderOutputFormat.EXR:  return DistImageFormat.EXR;
                default:                                                    return DistImageFormat.PNG;
            }
        }

        // -----------------------------------------------------------------------
        // Round-robin assignment
        // -----------------------------------------------------------------------

        /// <summary>
        /// Assigns jobs to workers using round-robin.
        ///
        /// Made <c>public static</c> for hermetic tests.
        /// </summary>
        /// <param name="jobs">List of jobs to assign.</param>
        /// <param name="workers">List of available workers.</param>
        /// <returns>
        /// A list of (job, worker) pairs in the same order as <paramref name="jobs"/>.
        /// Returns an empty list when either input is null or empty.
        /// </returns>
        public static List<(DistributedTimelineJob Job, WorkerInfo Worker)> AssignRoundRobin(
            IReadOnlyList<DistributedTimelineJob> jobs,
            IReadOnlyList<WorkerInfo> workers)
        {
            var result = new List<(DistributedTimelineJob, WorkerInfo)>();
            if (jobs == null || workers == null || jobs.Count == 0 || workers.Count == 0)
                return result;

            for (int i = 0; i < jobs.Count; i++)
                result.Add((jobs[i], workers[i % workers.Count]));

            return result;
        }

        // -----------------------------------------------------------------------
        // Job state aggregation helpers (public static for hermetic tests)
        // -----------------------------------------------------------------------

        /// <summary>
        /// Returns true when all jobs in <paramref name="vms"/> have reached a
        /// terminal state (Completed, Failed, Cancelled, or Unreachable).
        ///
        /// Made <c>public static</c> for hermetic tests.
        /// </summary>
        public static bool AreAllJobsTerminal(IReadOnlyList<MtrJobViewModel> vms)
        {
            if (vms == null || vms.Count == 0) return true;
            foreach (var vm in vms)
            {
                if (!IsTerminalState(vm.State))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Counts the number of jobs in <paramref name="vms"/> with the given
        /// <paramref name="state"/>.
        ///
        /// Made <c>public static</c> for hermetic tests.
        /// </summary>
        public static int CountJobsInState(IReadOnlyList<MtrJobViewModel> vms, JobState state)
        {
            if (vms == null) return 0;
            int count = 0;
            foreach (var vm in vms)
            {
                if (vm.State == state) count++;
            }
            return count;
        }

        /// <summary>
        /// Builds the local output directory path for a job, ensuring no ".." traversal.
        /// Format: <c>{projectRoot}/Recordings/Distributed/{jobId}</c>
        ///
        /// Made <c>public static</c> for hermetic tests; accepts
        /// <paramref name="projectRoot"/> explicitly to avoid
        /// <see cref="Application.dataPath"/> dependency in tests.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="jobId"/> contains ".." or path separators.
        /// </exception>
        public static string BuildResultOutputDir(string projectRoot, string jobId)
        {
            if (string.IsNullOrEmpty(projectRoot))
                throw new ArgumentNullException(nameof(projectRoot));
            if (string.IsNullOrEmpty(jobId))
                throw new ArgumentNullException(nameof(jobId));

            // Reject path traversal in jobId
            if (jobId.Contains("..") || jobId.Contains('/') || jobId.Contains('\\'))
                throw new ArgumentException(
                    $"jobId contains path traversal or separators: '{jobId}'", nameof(jobId));

            return Path.Combine(projectRoot, DistOutputRelRoot, jobId);
        }

        /// <summary>
        /// Builds the Master-side result output directory using the new naming scheme:
        /// <c>Recordings/Distributed/{dispatchTimestamp}/{sanitizedTimelineName}/</c>
        ///
        /// When <paramref name="dispatchTimestamp"/> is empty (legacy Masters), falls back
        /// to <see cref="BuildResultOutputDir"/> using <paramref name="jobId"/>.
        /// </summary>
        /// <param name="projectRoot">Absolute path to the Unity project root.</param>
        /// <param name="jobId">Unique job identifier (GUID). Used for the legacy fallback.</param>
        /// <param name="dispatchTimestamp">
        /// 14-digit timestamp (yyyyMMddHHmmss) shared across all jobs in the batch.
        /// When empty, the legacy single-jobId path is used.
        /// </param>
        /// <param name="sanitizedTimelineName">
        /// Pre-sanitized Timeline name sub-folder component (no path separators or "..").
        /// Typically produced by <see cref="SanitizeTimelineName"/>.
        /// </param>
        public static string BuildResultOutputDirTimestamped(
            string projectRoot,
            string jobId,
            string dispatchTimestamp,
            string sanitizedTimelineName)
        {
            if (string.IsNullOrEmpty(dispatchTimestamp))
                return BuildResultOutputDir(projectRoot, jobId);

            if (string.IsNullOrEmpty(projectRoot))
                throw new ArgumentNullException(nameof(projectRoot));

            return Path.Combine(projectRoot, DistOutputRelRoot, dispatchTimestamp, sanitizedTimelineName);
        }

        /// <summary>
        /// Sanitizes a Timeline (director GameObject) name so it is safe to use as a
        /// file-system path component.
        ///
        /// Delegates to <see cref="DistributedRecorder.Shared.PathSanitizer.SanitizeName"/>
        /// with <see cref="MaxSanitizedTimelineNameLength"/> as the length cap, ensuring
        /// Master and Worker produce identical output path components (F2/F14).
        ///
        /// Made public for hermetic testing.
        /// </summary>
        public static string SanitizeTimelineName(string name)
            => DistributedRecorder.Shared.PathSanitizer.SanitizeName(name, MaxSanitizedTimelineNameLength);

        // -----------------------------------------------------------------------
        // Scheduler pure functions (dispatch-retry-queue)
        // Made public static for hermetic EditMode tests.
        // -----------------------------------------------------------------------

        /// <summary>
        /// Selects the first idle Worker from <paramref name="workers"/>: tries
        /// <paramref name="preferredWorker"/> first, then falls back to the first
        /// Worker with 0 in-flight jobs according to <paramref name="inflightCounts"/>.
        ///
        /// Returns <c>null</c> when all Workers are busy or the inputs are empty.
        ///
        /// Made <c>public static</c> for hermetic EditMode tests.
        ///
        /// This function drives the actual dispatch path inside
        /// <see cref="TryDispatchNextQueuedJob"/>: when the preferred (just-freed)
        /// Worker is already busy again (e.g. a concurrent dispatch raced in), the
        /// scheduler transparently picks an alternative idle Worker instead of
        /// giving up.  Replaces the earlier "preferredWorker-only" implementation
        /// that had this gap (dispatch-retry-queue iteration 2 fix for Major-2 review
        /// item).
        /// </summary>
        /// <param name="workers">Ordered list of available Workers.</param>
        /// <param name="preferredWorker">
        /// The Worker that was freed most recently.  Tried first.
        /// May be <c>null</c> (treated as no preference).
        /// </param>
        /// <param name="inflightCounts">
        /// Map of workerDisplayName → number of currently in-flight jobs.
        /// Workers absent from the map are treated as having 0 in-flight jobs.
        /// </param>
        public static WorkerInfo SelectIdleWorker(
            IReadOnlyList<WorkerInfo> workers,
            WorkerInfo preferredWorker,
            IReadOnlyDictionary<string, int> inflightCounts)
            => SelectIdleWorker(workers, preferredWorker, inflightCounts, excludedDisplayNames: null);

        /// <summary>
        /// Selects the first idle Worker from <paramref name="workers"/> that is not
        /// in <paramref name="excludedDisplayNames"/>.  Tries <paramref name="preferredWorker"/>
        /// first (if not excluded), then falls back to the first non-excluded Worker with
        /// 0 in-flight jobs.
        ///
        /// Used by the dispatch-worker-liveness failover path to prevent re-selecting a
        /// Worker that already failed for this job (per-job <c>TriedWorkers</c> set).
        ///
        /// Made <c>public static</c> for hermetic EditMode tests.
        /// </summary>
        /// <param name="workers">Ordered list of available Workers.</param>
        /// <param name="preferredWorker">
        /// The Worker that was freed most recently.  Tried first unless excluded.
        /// May be <c>null</c> (treated as no preference).
        /// </param>
        /// <param name="inflightCounts">
        /// Map of workerDisplayName → number of currently in-flight jobs.
        /// Workers absent from the map are treated as having 0 in-flight jobs.
        /// </param>
        /// <param name="excludedDisplayNames">
        /// Set of worker display names to skip entirely.  Pass <c>null</c> or empty to
        /// apply no exclusions (identical to the 3-argument overload).
        /// </param>
        public static WorkerInfo SelectIdleWorker(
            IReadOnlyList<WorkerInfo> workers,
            WorkerInfo preferredWorker,
            IReadOnlyDictionary<string, int> inflightCounts,
            IReadOnlyCollection<string> excludedDisplayNames)
        {
            if (workers == null || workers.Count == 0) return null;

            bool IsExcluded(string name) =>
                excludedDisplayNames != null && excludedDisplayNames.Contains(name);

            // Check preferred Worker first (only if not excluded and idle)
            if (preferredWorker != null && !IsExcluded(preferredWorker.displayName))
            {
                int pc = 0;
                inflightCounts?.TryGetValue(preferredWorker.displayName, out pc);
                if (pc == 0) return preferredWorker;
            }

            // Fall back: first non-excluded idle Worker in registry order
            foreach (var w in workers)
            {
                if (IsExcluded(w.displayName)) continue;
                int count = 0;
                inflightCounts?.TryGetValue(w.displayName, out count);
                if (count == 0)
                    return w;
            }
            return null;
        }

        // -----------------------------------------------------------------------
        // Dispatch entry point
        // -----------------------------------------------------------------------

        private async void StartDistributedRecordingAsync(List<DistributedTimelineJob> targets)
        {
            // async void: wrap entire body so any synchronous or async exception is logged
            // rather than silently crashing the Editor (uncaught exception in async void
            // propagates to the synchronization context and can destabilize the Editor).
            try
            {
                await StartDistributedRecordingInternalAsync(targets);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                Debug.LogError("[DistributedRecorder] 分散録画中に予期しないエラーが発生しました。詳細は上記の例外を参照してください。");
            }
        }

        /// <summary>
        /// Implementation of distributed recording dispatch (called from the async void guard).
        ///
        /// dispatch-retry-queue change: uses a pending-queue + event-driven scheduler
        /// instead of round-robin one-shot dispatch.  Each Worker receives at most one
        /// in-flight job at a time; when a job completes/fails the next queued job is
        /// dispatched to the newly-freed Worker.  This eliminates 503-busy failures when
        /// Worker count is less than Timeline count.
        ///
        /// dispatch-worker-liveness change: performs a parallel pre-flight health probe
        /// (HMAC, 3 s timeout) before seeding; offline Workers are excluded from the
        /// initial seed.  If all Workers are offline every job is immediately marked
        /// Unreachable and the batch is finalized without entering the event loop.
        /// The reactive failover path (DispatchOneWithOverrideAsync) covers the case
        /// where a Worker that passed the probe becomes unreachable later.
        /// </summary>
        private async Task StartDistributedRecordingInternalAsync(List<DistributedTimelineJob> targets)
        {
            if (targets == null || targets.Count == 0)
            {
                Debug.LogWarning("[DistributedRecorder] 投入ジョブが 0 件です。中断します。");
                return;
            }

            // ── dispatch-progress-feedback A: progress bar for pre-flight phase ──
            // The bar is shown from OpenScene through the initial job seed.
            // try-finally guarantees ClearProgressBar() even on early returns or exceptions.
            // The bar is cleared before entering the async event loop (recording phase)
            // so the per-job state UI (Repaint) takes over from that point on.
            //
            // D-constraint: DisplayCancelableProgressBar / OpenScene / Repaint are
            // main-thread-only. This method is awaited on the Unity synchronization
            // context (no ConfigureAwait(false) on any await in this method), so all
            // Unity API calls here remain on the main thread.
            const string k_BarTitle = "分散処理を準備中";
            bool         cancelled  = false;

            try
            {
            // ── F10: open target scenes before dispatch ──────────────────────────
            // Open (or ensure open) each unique scene referenced by the targets.
            // CollectRenderTargets has already run at this point, so opening scenes here
            // does NOT retroactively change the ScenePath values that were collected.
            // In practice, targets all share the same active scene path (single-scene MTR),
            // so this loop is usually a no-op.  It is retained as a defensive step for
            // future multi-scene support; if already open, EditorSceneManager.OpenScene
            // returns the existing scene without reloading.
            // Main-thread call: this method is called from StartDistributedRecordingAsync (async void
            // on the Unity synchronization context) so Unity API calls are safe here.
            var scenePaths = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var job in targets)
            {
                if (!string.IsNullOrEmpty(job.ScenePath))
                    scenePaths.Add(job.ScenePath);
            }

            var scenePathList = new List<string>(scenePaths);
            string currentScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().path;
            for (int si = 0; si < scenePathList.Count; si++)
            {
                string scenePath = scenePathList[si];
                float  sceneProgress = scenePathList.Count > 0
                    ? (float)si / scenePathList.Count * 0.2f  // 0–20 % range
                    : 0f;

                // Show the bar BEFORE the blocking OpenScene call so the user sees
                // progress while the scene loads (the bar itself won't animate during
                // the synchronous load, but it is visible and dismissable via cancel).
                cancelled = EditorUtility.DisplayCancelableProgressBar(
                    k_BarTitle,
                    $"対象シーンを開いています… ({si + 1}/{scenePathList.Count})",
                    sceneProgress);
                if (cancelled)
                {
                    Debug.Log("[DistributedRecorder] プログレスバーでキャンセルされました（シーン読込前）。処理を中断します。");
                    return;
                }

                if (!string.Equals(scenePath, currentScene, StringComparison.OrdinalIgnoreCase))
                {
                    Debug.Log($"[DistributedRecorder] 対象 scene を開きます: {scenePath}");
                    UnityEditor.SceneManagement.EditorSceneManager.OpenScene(
                        scenePath, UnityEditor.SceneManagement.OpenSceneMode.Single);
                    currentScene = scenePath;
                }
            }

            // ── F5: dirty warning for scene + timeline assets ────────────────────
            // Warn when any recording-target scene (.unity) or timeline (.playable) asset
            // has uncommitted edits.  Recorder config assets (recorderConfigJson) are
            // transferred in-request, so their dirty state is irrelevant here.
            cancelled = EditorUtility.DisplayCancelableProgressBar(
                k_BarTitle, "未コミットの変更を確認しています…", 0.2f);
            if (cancelled)
            {
                Debug.Log("[DistributedRecorder] プログレスバーでキャンセルされました（dirty チェック前）。処理を中断します。");
                return;
            }
            WarnIfTargetAssetsDirty(targets);

            if (_distWorkerRegistry == null)
            {
                Debug.LogWarning("[DistributedRecorder] WorkerRegistryAsset が未設定です。中断します。");
                return;
            }

            var workers = _distWorkerRegistry.EnabledWorkers;
            if (workers.Count == 0)
            {
                Debug.LogWarning("[DistributedRecorder] 有効な Worker が 0 台です。中断します。");
                return;
            }

            // Build services (requires shared key)
            if (!SharedKeyLoader.TryLoad(out byte[] keyBytes, out string keyError))
            {
                Debug.LogError($"[DistributedRecorder] 共有キーのロードに失敗しました: {keyError}");
                return;
            }

            // Reset session-wide skip flags for this batch
            _sessionSkipHashCheck    = false;
            _sessionSkipVersionCheck = false;

            // Set up batch-scoped shared services (disposed in FinalizeBatchIfDone)
            _batchAuth       = new HmacAuthenticator(keyBytes);
            _batchTransport  = new HttpTransport(_batchAuth);
            _batchDispatcher = new JobDispatcher(_batchTransport, ProjectPaths.ProjectRoot);

            // ── Pre-flight liveness probe (dispatch-worker-liveness §A) ─────────
            // Probe all registered Workers in parallel (HMAC transport, 3 s timeout).
            // Workers that do not respond are excluded from the initial seed;
            // the reactive failover path handles any Worker that falls offline later.
            // We do NOT treat a failed probe as a permanent verdict — a Worker that was
            // temporarily unreachable (GC stall, restart) can still be selected by the
            // failover path once it recovers (plan.md 不明点5: one-chance fallback).
            //
            // dispatch-progress-feedback A+D: probe uses Task.WhenAny loop instead of
            // Task.WhenAll so we can update the progress bar each time a Worker responds.
            // All awaits in this method are WITHOUT ConfigureAwait(false) so continuations
            // remain on the Unity main thread (same rule as before; comment preserved below).
            //
            // NOTE: must NOT use ConfigureAwait(false) here. The continuation below calls
            // Unity main-thread-only APIs (Repaint, dispatch, AssetDatabase via DispatchQueuedJobAsync).
            // ConfigureAwait(false) resumed the continuation on a ThreadPool thread, causing
            // "Repaint can only be called from the main thread" and aborting the whole batch
            // (jobs stuck Queued). Resume on the captured Unity synchronization context instead.
            var probeTasks = new List<Task<bool>>(workers.Count);
            for (int pi = 0; pi < workers.Count; pi++)
                probeTasks.Add(_batchDispatcher.ProbeAsync(workers[pi]));

            // Wait for each probe to complete and update the bar.
            // We maintain a mapping Task→WorkerInfo via index.
            var pendingProbes   = new List<Task<bool>>(probeTasks);
            var probeResultMap  = new bool[workers.Count];
            int completedProbes = 0;
            while (pendingProbes.Count > 0)
            {
                Task<bool> finished = await Task.WhenAny(pendingProbes);
                pendingProbes.Remove(finished);
                completedProbes++;

                // Map back to worker index via reference equality.
                int workerIdx = probeTasks.IndexOf(finished);
                if (workerIdx >= 0)
                    probeResultMap[workerIdx] = await finished;

                float probeProgress = 0.25f + (float)completedProbes / workers.Count * 0.5f; // 25–75 %
                cancelled = EditorUtility.DisplayCancelableProgressBar(
                    k_BarTitle,
                    $"Worker を確認しています… ({completedProbes}/{workers.Count})",
                    probeProgress);
                if (cancelled)
                {
                    Debug.Log("[DistributedRecorder] プログレスバーでキャンセルされました（probe 中）。処理を中断します。");
                    return;
                }
            }

            var onlineWorkers = new List<WorkerInfo>();
            for (int pi = 0; pi < workers.Count; pi++)
            {
                if (probeResultMap[pi])
                    onlineWorkers.Add(workers[pi]);
                else
                    Debug.LogWarning(
                        $"[DistributedRecorder] 事前 health-check: '{workers[pi].displayName}' はオフラインです。初期 seed から除外します。");
            }

            Debug.Log(
                $"[DistributedRecorder] 事前 health-check: {onlineWorkers.Count}/{workers.Count} Worker オンライン。");

            // Retain the online list for the failover candidate pool (dispatch-worker-liveness
            // §B2 – Major fix): the Unreachable failover path and TryDispatchNextQueuedJob
            // use this list instead of EnabledWorkers so that pre-flight-offline Workers are
            // not selected as failover targets during the batch.
            _batchOnlineWorkers = onlineWorkers;

            // ── sync-before-dispatch (v1.4.14): pre-flight git sync gate ────────
            // Before seeding jobs, compare each online Worker's HEAD commit to the
            // master's HEAD.  If any Worker is out of sync, offer a blocking dialog
            // that lets the user sync (via the existing /git-sync API) before dispatch.
            //
            // Flow:
            //   A. Guard: master must have no unpushed commits.  If ahead>0, block and
            //      prompt "commit & push first" (worker can only reach origin/<branch>,
            //      so syncing while master is ahead would fail to match).
            //   B. Classify each online worker:
            //      (a) same branch + same commit  → already in sync
            //      (b) same branch + different commit → sync candidate
            //      (c) different branch            → skip + warn (never cross-branch reset)
            //      (d) gitCommitShort empty (old worker / git unavailable) → skip + warn
            //   C. If sync candidates exist → confirmation dialog → /git-sync → re-health
            //      polling until commits match or timeout.  Workers still mismatched after
            //      timeout fall through to the per-job CommitMismatch "send anyway" path
            //      (DispatchOneWithOverrideAsync) – no deadlock.
            //   D. If user declines sync → abort batch (No = stop, not "send anyway").
            //      This is the safest default; the per-job fallback remains available the
            //      next time the user presses dispatch without syncing.
            //
            // All awaits here are WITHOUT ConfigureAwait(false) so continuations stay on
            // the Unity main thread (required for EditorUtility.DisplayDialog, Repaint).
            // ProbeWithCommitAsync also must not ConfigureAwait(false) for the same reason.
            if (onlineWorkers.Count > 0)
            {
                cancelled = await RunPreFlightSyncGateAsync(onlineWorkers, k_BarTitle);
                if (cancelled)
                {
                    Debug.Log("[DistributedRecorder] pre-flight sync gate: user aborted dispatch.");
                    return;
                }
            }
            // ── end sync-before-dispatch ─────────────────────────────────────────

            // Generate a single dispatch timestamp for the whole batch so all Timeline results
            // land under the same parent folder (worker-recording-fix requirement C).
            // Format: yyyyMMddHHmmss (14 digits).
            string batchDispatchTimestamp = DateTime.Now.ToString("yyyyMMddHHmmss");

            // ── Build view models and fill the pending queue ────────────────────
            // All jobs start as Queued (visible in UI immediately).
            // We build one VM per target (no pre-assignment); Worker assignment happens
            // at dispatch time (first available Worker).
            _dispatchedJobs.Clear();
            _pendingQueue.Clear();
            _jobWorkerMap.Clear();
            _workerInflightCount.Clear();
            _jobLastProgressTime.Clear();

            foreach (var worker in workers)
                _workerInflightCount[worker.displayName] = 0;

            Debug.Log($"[DistributedRecorder] {targets.Count} ジョブを {workers.Count} Worker にスケジュールします。 dispatchTimestamp={batchDispatchTimestamp}");

            for (int i = 0; i < targets.Count; i++)
            {
                var job          = targets[i];
                string jobId     = Guid.NewGuid().ToString("N");
                string sanitizedName = SanitizeTimelineName(job.DirectorObjectName);
                string outDir    = BuildResultOutputDirTimestamped(
                    ProjectPaths.ProjectRoot, jobId,
                    batchDispatchTimestamp, sanitizedName);

                var request = new JobRequest
                {
                    jobId                      = jobId,
                    scenePath                  = job.ScenePath,
                    timelineAssetPath          = job.TimelineAssetPath,
                    directorObjectName         = job.DirectorObjectName,
                    directorHierarchyPath      = job.DirectorHierarchyPath,
                    recorderConfig             = job.JobConfig,
                    startTime                  = job.StartTime,
                    endTime                    = job.EndTime,
                    outputSubDir               = jobId,
                    dispatchTimestamp          = batchDispatchTimestamp,
                    // MTR fidelity fields
                    jobScopeHash               = job.JobScopeHash,
                    recorderConfigJson         = job.RecorderConfigJson,
                    targetCameraHierarchyPath  = job.TargetCameraHierarchyPath,
                    targetCameraName           = job.TargetCameraName,
                    renderTextureGuid          = job.RenderTextureGuid,
                    effectiveWidth             = job.EffectiveWidth,
                    effectiveHeight            = job.EffectiveHeight,
                    effectiveFrameRate         = job.EffectiveFrameRate,
                    resolvedOutputRelativePath = job.ResolvedOutputRelativePath
                };

                // Hint: show a likely Worker in the UI while the job is queued.
                // Suffixed with "（予定）" to make clear this is not the final assignment.
                // Actual Worker is determined at dispatch time (first idle Worker) and
                // vm.WorkerName is overwritten in DispatchQueuedJobAsync.
                string hintWorkerName = workers[i % workers.Count].displayName + "（予定）";

                var vm = new MtrJobViewModel
                {
                    JobId          = jobId,
                    TimelineName   = job.DirectorObjectName,
                    WorkerName     = hintWorkerName,
                    State          = JobState.Queued,
                    DownloadState  = DownloadState.NotStarted,
                    LocalOutputDir = outDir,
                    Worker         = null,   // assigned at dispatch time
                    PendingRequest = request,
                    RetryCount     = 0,
                    TriedWorkers   = new HashSet<string>(),
                    Phase          = JobPhase.Queued,  // dispatch-status-labels
                    // collect-to-dir (v1.4.10): snapshot at dispatch time so later
                    // UI edits to _collectDir do not affect already-queued jobs.
                    CollectDir      = _collectDir,
                    CollectDisambig = jobId.Substring(0, Math.Min(8, jobId.Length)),
                };

                _dispatchedJobs.Add(vm);
                _pendingQueue.Enqueue(vm);
            }

            Repaint();

            // ── All Workers offline: fail every job immediately ──────────────────
            // Do this after building VMs so the UI shows the jobs (with Unreachable
            // state) before the batch is finalized.  FinalizeBatchIfDone will then
            // clean up services immediately since the queue is empty and all VMs are
            // terminal.
            if (onlineWorkers.Count == 0)
            {
                // dispatch-progress-feedback C: actionable message with firewall hint.
                const string zeroWorkerMsg =
                    "オンラインの Worker が 0 台です。全ジョブを中断します。\n\n" +
                    "確認事項:\n" +
                    "  (1) 各 Worker で Unity Editor とリスナーが起動し、ポート 11080 で待受しているか\n" +
                    "  (2) Windows ファイアウォールが受信 TCP 11080 を許可しているか\n\n" +
                    "Worker が起動中であれば「分散処理を開始」を再度押してください。";

                Debug.LogError("[DistributedRecorder] " + zeroWorkerMsg);
                EditorUtility.DisplayDialog("Worker 未検出", zeroWorkerMsg, "OK");

                _pendingQueue.Clear();
                foreach (var vm in _dispatchedJobs)
                {
                    if (!IsTerminalState(vm.State))
                        vm.State = JobState.Unreachable;
                }
                FinalizeBatchIfDone();
                Repaint();
                return;
            }

            // ── Initial dispatch: one job per online Worker (parallel seed) ──────
            // Use sequential await so override dialogs (hash/version) are shown on
            // the main thread one at a time and _sessionSkip flags propagate correctly.
            // Only online Workers are seeded; the event-driven scheduler handles the
            // remaining queue via OnJobTerminated/AfterFailedDispatch.
            //
            // dispatch-progress-feedback A: update the bar for each initial dispatch.
            // The bar is cleared AFTER the seed loop exits so the recording phase (async
            // event loop driven by EditorApplication.update + OnJobTerminated) uses
            // the per-job state UI (Repaint) from that point on, not the progress bar.
            int seedDispatched = 0;
            foreach (var worker in onlineWorkers)
            {
                if (_pendingQueue.Count == 0) break;
                var vm = _pendingQueue.Dequeue();

                cancelled = EditorUtility.DisplayCancelableProgressBar(
                    k_BarTitle,
                    $"ジョブを送信しています… ({seedDispatched + 1}/{onlineWorkers.Count})",
                    0.75f + (float)seedDispatched / onlineWorkers.Count * 0.25f); // 75–100 %
                if (cancelled)
                {
                    // Re-enqueue the dequeued VM so it is not lost.
                    _pendingQueue.Enqueue(vm);
                    vm.State = JobState.Queued;
                    Debug.Log("[DistributedRecorder] プログレスバーでキャンセルされました（ジョブ送信前）。処理を中断します。");
                    return;
                }

                await DispatchQueuedJobAsync(worker, vm);
                seedDispatched++;
                Repaint();
            }

            // Remaining jobs stay in _pendingQueue; they are dispatched by
            // OnJobTerminated (via StartProgressMonitorWithScheduler completion callbacks).
            // No transport.Dispose() here — deferred to FinalizeBatchIfDone.

            } // end try
            finally
            {
                // dispatch-progress-feedback A: always clear the bar, whether we returned
                // early (cancel, error, 0-worker) or completed the seed loop normally.
                // The recording phase does NOT use the progress bar.
                EditorUtility.ClearProgressBar();
            }
        }

        // -----------------------------------------------------------------------
        // Scheduler helpers (dispatch-retry-queue)
        // -----------------------------------------------------------------------

        /// <summary>
        /// Dispatches <paramref name="vm"/> (which must be in Queued state) to
        /// <paramref name="worker"/>, starting the progress monitor on success.
        ///
        /// Called both during the initial seed loop and from <see cref="OnJobTerminated"/>
        /// when a Worker slot frees up.
        ///
        /// Must be called from the Editor main thread (awaited in async void / async Task
        /// context; dialog calls require main thread).
        /// </summary>
        private async Task DispatchQueuedJobAsync(WorkerInfo worker, MtrJobViewModel vm)
        {
            vm.Worker     = worker;
            vm.WorkerName = worker.displayName;
            // State remains Queued until accepted by Worker
            // dispatch-status-labels: mark Sending phase so UI shows "データ送信中"
            vm.Phase = JobPhase.Sending;

            bool accepted = await DispatchOneWithOverrideAsync(
                _batchDispatcher, _batchAuth, worker, vm.PendingRequest, vm);

            if (accepted)
            {
                // Track in-flight state
                _jobWorkerMap[vm.JobId] = worker;
                _workerInflightCount[worker.displayName] =
                    (_workerInflightCount.TryGetValue(worker.displayName, out int c) ? c : 0) + 1;
                _jobLastProgressTime[vm.JobId] = EditorApplication.timeSinceStartup;

                StartProgressMonitorWithScheduler(worker, vm);
            }
            else
            {
                // DispatchOneWithOverrideAsync sets:
                //   vm.State = JobState.Queued  → WorkerBusy (transient) or Unreachable failover
                //   vm.State = terminal          → permanent rejection / exception / retry-limit
                //
                // In both cases _workerInflightCount was NOT incremented (accepted==false),
                // so we must NOT decrement it here.
                if (vm.State == JobState.Queued)
                    _pendingQueue.Enqueue(vm);

                // dispatch-worker-liveness §B Blocker fix (iter2):
                // Always call AfterFailedDispatch regardless of whether the job re-queued
                // (Queued) or terminated (terminal).  This ensures the queue pump runs after
                // BOTH WorkerBusy re-enqueue AND Unreachable failover re-enqueue.
                //
                // Without this, an Unreachable failover sets vm.State=Queued and re-enqueues
                // but IsTerminalState(Queued)==false, so AfterFailedDispatch was not called.
                // When the failed Worker had zero in-flight jobs (e.g. it was the only seeded
                // Worker and fell offline before the job was accepted), no OnJobTerminated
                // callback ever arrives, leaving the queue permanently stalled.
                //
                // WorkerBusy case: AfterFailedDispatch → TryDispatchNextQueuedJob(worker)
                //   → SelectIdleWorker sees worker.inflightCount > 0 (another job is in-flight
                //     on that Worker, that's why it was busy) and picks a different idle Worker
                //     or returns null (all busy → queue left intact for next OnJobTerminated).
                //   This is safe and actually improves throughput when another Worker is idle.
                //
                // Unreachable failover case: AfterFailedDispatch → TryDispatchNextQueuedJob
                //   → SelectIdleWorker uses _batchOnlineWorkers with TriedWorkers exclusion,
                //     so the same failed Worker is not re-selected.
                //
                // Double-pump guard: AfterFailedDispatch does NOT touch _workerInflightCount,
                // so calling it once here (whether Queued or terminal) is safe in both cases.
                AfterFailedDispatch(worker);
            }

            Repaint();
        }

        /// <summary>
        /// Called when a dispatch attempt ends in terminal failure <em>before</em> the job
        /// was ever accepted (i.e. <see cref="_workerInflightCount"/> was never incremented
        /// for this attempt).
        ///
        /// Unlike <see cref="OnJobTerminated"/>, this method does <b>not</b> decrement the
        /// in-flight counter – doing so would underflow because the job was never counted as
        /// in-flight.  It only advances the pending queue by trying to dispatch the next job
        /// to <paramref name="worker"/> and then checks whether the batch is complete.
        ///
        /// Scenarios covered:
        ///   • Permanent rejection (hash/version/duplicate/unknown) – vm.State=Failed
        ///   • Dispatch exception – vm.State=Failed
        ///   • WorkerBusy retry-limit exceeded – vm.State=Failed
        ///
        /// (dispatch-retry-queue iteration 2 – Blocker AC-6 fix)
        /// </summary>
        private void AfterFailedDispatch(WorkerInfo worker)
        {
            // Try to give the next queued job to the now-available Worker.
            TryDispatchNextQueuedJob(worker);
            // Check whether all jobs (including any just-failed one) are done.
            FinalizeBatchIfDone();
        }

        /// <summary>
        /// Called (on main thread) when a job reaches a terminal state.
        /// Frees the Worker slot and dispatches the next queued job if any.
        /// </summary>
        private void OnJobTerminated(WorkerInfo worker, MtrJobViewModel completedVm)
        {
            // Release the slot
            if (_jobWorkerMap.ContainsKey(completedVm.JobId))
                _jobWorkerMap.Remove(completedVm.JobId);

            _jobLastProgressTime.Remove(completedVm.JobId);

            // worker-reload-survival 案D: clear watchdog state for this job
            _jobReconnectCount.Remove(completedVm.JobId);
            _stallWatchdogActive.Remove(completedVm.JobId);

            if (_workerInflightCount.TryGetValue(worker.displayName, out int count))
                _workerInflightCount[worker.displayName] = Math.Max(0, count - 1);

            // Dispatch next job to the newly-freed Worker
            TryDispatchNextQueuedJob(worker);

            // If no more work remains, finalize the batch
            FinalizeBatchIfDone();
        }

        /// <summary>
        /// Picks the next job from <see cref="_pendingQueue"/> and dispatches it to an
        /// idle Worker (fire-and-forget, always called on main thread).
        ///
        /// Prefers <paramref name="preferredWorker"/> (the just-freed Worker); if that
        /// Worker is already busy again (e.g. a concurrent seed dispatch raced in),
        /// <see cref="SelectIdleWorker"/> scans the full registry for any idle Worker.
        /// If no idle Worker exists the queue item is put back at the front and the
        /// method returns – the next <see cref="OnJobTerminated"/> call will retry.
        ///
        /// dispatch-retry-queue iteration 2: uses <see cref="SelectIdleWorker"/> so the
        /// scheduler is not locked to a single freed Worker; fixes the multi-Worker
        /// "preferredWorker fixed" weakness noted in the review (Major-2).
        ///
        /// dispatch-worker-liveness change: peeks the next job's <c>TriedWorkers</c> set
        /// and passes it to <see cref="SelectIdleWorker"/> as the exclusion set, so the
        /// failover path never re-selects a Worker that already failed for this job.
        /// If no non-excluded idle Worker exists (all online Workers for this job tried),
        /// the job is terminally failed here without re-queuing it (prevents infinite loop).
        /// </summary>
        private void TryDispatchNextQueuedJob(WorkerInfo preferredWorker)
        {
            if (_pendingQueue.Count == 0) return;

            // Peek the next job to get its per-job exclusion set before selecting a Worker.
            var nextVm = _pendingQueue.Peek();

            // dispatch-worker-liveness §B2 (Major fix): use the pre-flight online list as
            // the failover candidate pool so that pre-flight-offline Workers are not
            // selected here.  Fall back to EnabledWorkers only when the batch has already
            // been finalized (null guard; should not happen in practice).
            var workers = (_batchOnlineWorkers != null && _batchOnlineWorkers.Count > 0)
                ? (IReadOnlyList<WorkerInfo>)_batchOnlineWorkers
                : _distWorkerRegistry?.EnabledWorkers;

            // Find an idle non-excluded Worker (prefer the freed one, fall back to any idle)
            WorkerInfo target = SelectIdleWorker(
                workers, preferredWorker, _workerInflightCount, nextVm.TriedWorkers);

            if (target == null)
            {
                // Two sub-cases:
                // (a) All Workers busy but some are not yet excluded → leave in queue; retry
                //     on next OnJobTerminated/AfterFailedDispatch.
                // (b) All non-excluded Workers tried → no further candidates; fail this job
                //     immediately to avoid the job stalling in the queue forever.
                bool hasUntried = HasUntried(workers, nextVm.TriedWorkers);
                if (hasUntried)
                {
                    // (a) At least one Worker is not yet excluded, but currently busy.
                    // Leave the item in the queue; it will be retried when the next slot frees.
                    return;
                }

                // (b) Every Worker has already been tried for this job.
                _pendingQueue.Dequeue(); // remove from queue
                if (!IsTerminalState(nextVm.State))
                {
                    nextVm.State = JobState.Unreachable;
                    Debug.LogError(
                        $"[DistributedRecorder] ジョブ {(nextVm.JobId.Length >= 8 ? nextVm.JobId.Substring(0, 8) : nextVm.JobId)}… :" +
                        " すべての Worker を試みましたが到達できませんでした。Unreachable に設定します。");
                }
                FinalizeBatchIfDone();
                return;
            }

            _pendingQueue.Dequeue(); // now safe to dequeue — target is confirmed
            // Fire-and-forget; exceptions are caught inside DispatchQueuedJobAsync
            _ = DispatchQueuedJobAsync(target, nextVm);
        }

        /// <summary>
        /// Returns <c>true</c> if any Worker in <paramref name="workers"/> is NOT in
        /// <paramref name="triedWorkers"/> (i.e., there is still an untried candidate).
        /// Used by <see cref="TryDispatchNextQueuedJob"/> to distinguish "all busy"
        /// from "all tried + busy" so the failover terminates cleanly.
        ///
        /// Made <c>public static</c> for hermetic EditMode tests (same pattern as
        /// <see cref="SelectIdleWorker"/>).
        /// </summary>
        public static bool HasUntried(
            IReadOnlyList<WorkerInfo> workers,
            IReadOnlyCollection<string> triedWorkers)
        {
            if (workers == null) return false;
            if (triedWorkers == null || triedWorkers.Count == 0) return workers.Count > 0;
            foreach (var w in workers)
            {
                if (!triedWorkers.Contains(w.displayName))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Disposes batch-scoped services when all jobs have reached terminal states.
        /// </summary>
        private void FinalizeBatchIfDone()
        {
            if (_pendingQueue.Count > 0) return;
            if (!AreAllJobsTerminal(_dispatchedJobs)) return;

            _batchTransport?.Dispose();
            _batchTransport     = null;
            _batchDispatcher    = null;
            _batchAuth          = null;
            _batchOnlineWorkers = null;

            // worker-reload-survival 案D: unregister stall watchdog when batch finalizes
            if (_stallWatchdogRegistered)
            {
                EditorApplication.update -= StallWatchdogTick;
                _stallWatchdogRegistered  = false;
            }
            _jobReconnectCount.Clear();
            _stallWatchdogActive.Clear();

            Debug.Log("[DistributedRecorder] 全ジョブが完了しました。バッチを終了します。");
            Repaint();
        }

        // -----------------------------------------------------------------------
        // stop-button: batch stop
        // -----------------------------------------------------------------------

        /// <summary>
        /// Whether a batch stop is in progress.  Prevents re-entrant calls.
        /// Only accessed on the main thread.
        /// </summary>
        private bool _batchStopping;

        /// <summary>
        /// Stops the current batch:
        ///  1. Drains the pending queue and marks un-dispatched jobs as Cancelled.
        ///  2. Stops the stall watchdog and progress monitors.
        ///  3. Sends POST /cancel to each in-flight Worker (HMAC authenticated).
        ///     Workers that do not understand /cancel (older than v1.4.9) return 404;
        ///     the master proceeds without waiting (wire-compat: they will finish recording
        ///     but master will not dispatch new jobs to them).
        ///  4. Marks all remaining non-terminal jobs as Cancelled.
        ///  5. Calls <see cref="FinalizeBatchIfDone"/> to release batch-scoped services.
        ///
        /// Must be called from the Editor main thread (async void guard calls this via await).
        /// </summary>
        private async System.Threading.Tasks.Task StopBatchAsync()
        {
            if (_batchStopping)
            {
                Debug.Log("[DistributedRecorder] 停止処理がすでに進行中です。");
                return;
            }
            _batchStopping = true;

            try
            {
                Debug.Log("[DistributedRecorder] バッチ停止を開始します。");

                // 1. Drain the pending queue: cancel all un-dispatched jobs immediately.
                CancelQueuedJobs();

                // 2. Stop the stall watchdog (no new stall callbacks should fire after this).
                if (_stallWatchdogRegistered)
                {
                    EditorApplication.update -= StallWatchdogTick;
                    _stallWatchdogRegistered  = false;
                }

                // 3. Collect in-flight jobs (Running or Pending, i.e., dispatched to a Worker).
                //    Snapshot the map now; it may change on callbacks after we await.
                var inflightSnapshot = new List<(string jobId, WorkerInfo worker, MtrJobViewModel vm)>();
                foreach (var vm in _dispatchedJobs)
                {
                    if (!IsTerminalState(vm.State) && vm.Worker != null &&
                        _jobWorkerMap.ContainsKey(vm.JobId))
                    {
                        inflightSnapshot.Add((vm.JobId, vm.Worker, vm));
                    }
                }

                // 4. Send /cancel to each in-flight Worker.
                //    Wire-compat: 404 from old Workers is logged and ignored — master stop
                //    still completes. We do NOT await each individually to avoid long waits;
                //    we fire all in parallel and await the aggregate.
                if (inflightSnapshot.Count > 0 && _batchDispatcher != null && _batchAuth != null)
                {
                    var cancelTasks = new List<System.Threading.Tasks.Task>(inflightSnapshot.Count);
                    foreach (var (jobId, worker, _) in inflightSnapshot)
                    {
                        cancelTasks.Add(SendCancelToWorkerAsync(worker, jobId));
                    }
                    // Wait for all cancel requests to finish (success or failure).
                    await System.Threading.Tasks.Task.WhenAll(cancelTasks);
                }

                // 5. Mark all remaining non-terminal jobs as Cancelled.
                foreach (var vm in _dispatchedJobs)
                {
                    if (!IsTerminalState(vm.State))
                    {
                        vm.State = JobState.Cancelled;
                        vm.Phase = JobPhase.Terminal;
                    }
                }

                // 6. Clean up watchdog state.
                _jobReconnectCount.Clear();
                _stallWatchdogActive.Clear();
                _jobLastProgressTime.Clear();
                _jobWorkerMap.Clear();

                Debug.Log("[DistributedRecorder] バッチ停止完了。全ジョブを Cancelled に設定しました。");

                // 7. Finalize the batch (releases transport/dispatcher/auth).
                //    FinalizeBatchIfDone checks pendingQueue.Count==0 and all-terminal, both true now.
                FinalizeBatchIfDone();
                Repaint();
            }
            finally
            {
                _batchStopping = false;
            }
        }

        /// <summary>
        /// Drains <see cref="_pendingQueue"/> and marks every dequeued job as
        /// <see cref="JobState.Cancelled"/>.  Called at the start of
        /// <see cref="StopBatchAsync"/> to prevent new dispatches.
        ///
        /// Pure side-effect (no network calls).
        /// Made internal for hermetic tests.
        /// </summary>
        internal void CancelQueuedJobs()
        {
            while (_pendingQueue.Count > 0)
            {
                var vm = _pendingQueue.Dequeue();
                if (!IsTerminalState(vm.State))
                {
                    vm.State = JobState.Cancelled;
                    vm.Phase = JobPhase.Terminal;
                    Debug.Log(
                        $"[DistributedRecorder] 待機中ジョブをキャンセル: " +
                        $"{(vm.JobId.Length >= 8 ? vm.JobId.Substring(0, 8) : vm.JobId)}…");
                }
            }
        }

        /// <summary>
        /// Sends POST /cancel to <paramref name="worker"/> for the given
        /// <paramref name="jobId"/> using the batch HMAC authenticator.
        ///
        /// Wire-compat: if the Worker returns 404 (older than v1.4.9, no /cancel endpoint),
        /// the failure is logged at Info level (not Error) and the promise resolves normally
        /// so the master-side stop is not blocked.
        ///
        /// Must be awaited on the Unity main thread (no ConfigureAwait(false)).
        ///
        /// Made internal for hermetic tests.
        /// </summary>
        internal async System.Threading.Tasks.Task SendCancelToWorkerAsync(
            WorkerInfo worker, string jobId)
        {
            if (_batchDispatcher == null || _batchAuth == null)
                return;

            try
            {
                bool sent = await _batchDispatcher.SendCancelJobAsync(worker, jobId);
                if (sent)
                {
                    Debug.Log(
                        $"[DistributedRecorder] /cancel 送信成功: Worker='{worker.displayName}' " +
                        $"jobId={jobId.Substring(0, Math.Min(8, jobId.Length))}…");
                }
                else
                {
                    // 404 or other non-fatal response from older Worker.
                    Debug.Log(
                        $"[DistributedRecorder] /cancel: Worker '{worker.displayName}' が応答しませんでした " +
                        $"(古い Worker の可能性)。master 停止は続行します。" +
                        $"jobId={jobId.Substring(0, Math.Min(8, jobId.Length))}…");
                }
            }
            catch (Exception ex)
            {
                // Network error: log and continue. Master-side stop must always complete.
                Debug.LogWarning(
                    $"[DistributedRecorder] /cancel 送信中に例外が発生しました " +
                    $"(Worker='{worker.displayName}'): {ex.Message}。master 停止は続行します。");
            }
        }

        // -----------------------------------------------------------------------
        // reprobe-add-worker: pure function + async operation
        // -----------------------------------------------------------------------

        /// <summary>
        /// Pure function: given all enabled Workers in the registry, the current
        /// batch online pool, and fresh probe results, returns the subset of Workers
        /// that should be added to the running pool.
        ///
        /// A Worker is added when ALL three conditions hold:
        ///   1. It is enabled in the registry (<paramref name="allEnabledWorkers"/>).
        ///   2. Its probe result is true (online) in <paramref name="probeResults"/>.
        ///   3. It is NOT already in <paramref name="batchOnlineWorkers"/> (no duplicates).
        ///
        /// Workers absent from <paramref name="probeResults"/> are treated as offline.
        ///
        /// Made <c>public static</c> for hermetic EditMode tests.
        /// </summary>
        /// <param name="allEnabledWorkers">
        /// All enabled Workers from the registry at the time of reprobe.
        /// </param>
        /// <param name="batchOnlineWorkers">
        /// Workers already in the running batch pool (must not be duplicated).
        /// </param>
        /// <param name="probeResults">
        /// Map of workerDisplayName → online result from the reprobe.
        /// </param>
        /// <returns>
        /// Ordered list of Workers to add to the pool (may be empty; never null).
        /// </returns>
        public static List<WorkerInfo> ComputeWorkersToAdd(
            IReadOnlyList<WorkerInfo> allEnabledWorkers,
            IReadOnlyList<WorkerInfo> batchOnlineWorkers,
            IReadOnlyDictionary<string, bool> probeResults)
        {
            var result = new List<WorkerInfo>();
            if (allEnabledWorkers == null || allEnabledWorkers.Count == 0)
                return result;

            // Build a set of display names already in the batch pool for O(1) lookup.
            var alreadyInPool = new HashSet<string>();
            if (batchOnlineWorkers != null)
            {
                foreach (var w in batchOnlineWorkers)
                {
                    if (w != null)
                        alreadyInPool.Add(w.displayName);
                }
            }

            foreach (var w in allEnabledWorkers)
            {
                if (w == null) continue;

                // Skip if already in pool
                if (alreadyInPool.Contains(w.displayName)) continue;

                // Skip if probe did not return true (offline or not probed)
                if (probeResults == null) continue;
                if (!probeResults.TryGetValue(w.displayName, out bool online) || !online) continue;

                result.Add(w);
            }
            return result;
        }

        /// <summary>
        /// Probes all enabled Workers in the registry, adds newly online Workers to
        /// <see cref="_batchOnlineWorkers"/>, initialises their inflight counter, and
        /// pumps the pending queue by dispatching the next queued job to each newly
        /// idle added Worker.
        ///
        /// Called from the UI button; no-ops safely when no batch is active.
        /// Guard: <see cref="_reprobing"/> prevents concurrent invocations.
        ///
        /// Main-thread constraint: no <c>ConfigureAwait(false)</c>.  All Unity API
        /// calls (Repaint, EditorCallbackOnce, _workerInflightCount) remain on the
        /// main thread throughout.
        /// </summary>
        private async Task ReprobeAndAddWorkersAsync()
        {
            // Guard: no-op when batch not active or already probing
            if (_batchOnlineWorkers == null || _batchDispatcher == null || _batchAuth == null)
                return;
            if (_reprobing) return;

            _reprobing = true;
            _reprobeResultMessage = "再プローブ中…";
            Repaint();

            try
            {
                var registry = _distWorkerRegistry;
                if (registry == null)
                {
                    _reprobeResultMessage = "レジストリが未設定です。";
                    return;
                }

                var allEnabled = registry.EnabledWorkers;
                if (allEnabled.Count == 0)
                {
                    _reprobeResultMessage = "有効 Worker が 0 台です。";
                    return;
                }

                // Probe all enabled Workers (same probe path as pre-flight: HMAC, 3s timeout).
                // Use the batch dispatcher (already authenticated).
                var probeResults = new Dictionary<string, bool>();
                int probeCount = 0;
                foreach (var worker in allEnabled)
                {
                    if (worker == null) continue;
                    bool online = await _batchDispatcher.ProbeAsync(worker);
                    probeResults[worker.displayName] = online;
                    probeCount++;
                }

                // Compute which Workers should be added (pure function — hermetic testable).
                var toAdd = ComputeWorkersToAdd(allEnabled, _batchOnlineWorkers, probeResults);

                // Add new Workers to the batch pool and initialise their inflight counter.
                int addedCount = 0;
                foreach (var w in toAdd)
                {
                    _batchOnlineWorkers.Add(w);
                    if (!_workerInflightCount.ContainsKey(w.displayName))
                        _workerInflightCount[w.displayName] = 0;
                    addedCount++;

                    Debug.Log(
                        $"[DistributedRecorder] 再プローブ: Worker '{w.displayName}' をバッチプールに追加しました。");
                }

                // Log offline Workers that were probed but not in the pool.
                // "Already in pool" Workers that now probe offline are expected (Worker may
                // be temporarily unreachable) — no log needed; they stay in the pool.
                var toAddNames = new HashSet<string>();
                foreach (var w2 in toAdd) if (w2 != null) toAddNames.Add(w2.displayName);

                foreach (var worker in allEnabled)
                {
                    if (worker == null) continue;
                    if (!probeResults.TryGetValue(worker.displayName, out bool workerOnline)
                        || workerOnline)
                        continue; // online or missing (should not happen): skip

                    // Offline and NOT in the result set → log only once per offline Worker
                    if (!toAddNames.Contains(worker.displayName))
                    {
                        Debug.Log(
                            $"[DistributedRecorder] 再プローブ: Worker '{worker.displayName}' " +
                            "はオフラインのためプールに追加しませんでした。");
                    }
                }

                // Pump the pending queue: for each newly added (idle) Worker, dispatch
                // the next job if any are waiting.
                int pumpedCount = 0;
                foreach (var w in toAdd)
                {
                    if (_pendingQueue.Count == 0) break;

                    // Only pump if the newly added Worker is idle (inflight == 0).
                    // It was just added so its count is 0, but guard defensively.
                    _workerInflightCount.TryGetValue(w.displayName, out int inflight);
                    if (inflight > 0) continue;

                    // Peek next job – avoid dispatching if all workers for that job
                    // have been tried and this newly added Worker is also excluded.
                    // SelectIdleWorker with the next job's TriedWorkers will handle this.
                    var nextVm = _pendingQueue.Peek();
                    WorkerInfo target = SelectIdleWorker(
                        _batchOnlineWorkers, w, _workerInflightCount, nextVm.TriedWorkers);

                    if (target == null) continue;

                    _pendingQueue.Dequeue();
                    _ = DispatchQueuedJobAsync(target, nextVm);
                    pumpedCount++;
                }

                // Build result message for UI
                int alreadyInPoolCount = CountAlreadyInPool(allEnabled, toAdd);
                int offlineCount = probeCount - addedCount - alreadyInPoolCount;
                string offlineSuffix = offlineCount > 0
                    ? $"、{offlineCount} 台オフライン"
                    : string.Empty;
                _reprobeResultMessage =
                    $"{probeCount} 台プローブ / {addedCount} 台追加 / {pumpedCount} ジョブ再割当{offlineSuffix}";

                Debug.Log(
                    $"[DistributedRecorder] 再プローブ完了: {_reprobeResultMessage}");
                Repaint();
            }
            finally
            {
                _reprobing = false;
                Repaint();
            }
        }

        /// <summary>
        /// Helper: counts how many workers in <paramref name="allEnabled"/> are already
        /// in the batch pool (i.e. not in <paramref name="toAdd"/> and not offline).
        /// Used only for building the result message string in
        /// <see cref="ReprobeAndAddWorkersAsync"/>.
        /// </summary>
        private int CountAlreadyInPool(
            IReadOnlyList<WorkerInfo> allEnabled,
            IReadOnlyList<WorkerInfo> toAdd)
        {
            if (allEnabled == null || _batchOnlineWorkers == null) return 0;
            var toAddNames = new HashSet<string>();
            if (toAdd != null)
                foreach (var w in toAdd) if (w != null) toAddNames.Add(w.displayName);

            int count = 0;
            foreach (var w in allEnabled)
            {
                if (w == null) continue;
                if (toAddNames.Contains(w.displayName)) continue;
                foreach (var pw in _batchOnlineWorkers)
                {
                    if (pw != null && pw.displayName == w.displayName)
                    {
                        count++;
                        break;
                    }
                }
            }
            return count;
        }

        // -----------------------------------------------------------------------
        // Single-job dispatch with hash/version override dialog
        // -----------------------------------------------------------------------

        /// <summary>
        /// Dispatches one job, handling hash/version mismatch dialogs in-line.
        /// Returns true when the job was accepted by the Worker.
        /// </summary>
        private async Task<bool> DispatchOneWithOverrideAsync(
            JobDispatcher dispatcher,
            HmacAuthenticator auth,
            WorkerInfo worker,
            JobRequest request,
            MtrJobViewModel vm)
        {
            string jobIdShort = request.jobId.Length >= 8
                ? request.jobId.Substring(0, 8) : request.jobId;

            DispatchResult result;
            try
            {
                result = await dispatcher.DispatchAsync(worker, request,
                    skipVersionCheck: _sessionSkipVersionCheck,
                    skipHashCheck:    _sessionSkipHashCheck);
            }
            catch (Exception ex)
            {
                vm.State = JobState.Failed;
                Debug.LogError(
                    $"[DistributedRecorder] ジョブ {jobIdShort}… → {worker.displayName}: " +
                    $"例外が発生しました: {ex.Message}");
                return false;
            }

            if (result.Success)
            {
                vm.State = JobState.Running;
                // dispatch-status-labels: ack received → Worker is launching Play Mode.
                // No frames yet → show "Editor 起動中…" until first ProgressEvent.
                vm.Phase = JobPhase.Preparing;
                Debug.Log(
                    $"[DistributedRecorder] ジョブ {jobIdShort}… → {worker.displayName}: 投入完了");
                return true;
            }

            // ── Handle failures ──────────────────────────────────────────────
            switch (result.FailReason)
            {
                case DispatchFailReason.VersionMismatch:
                {
                    bool proceed = _sessionSkipVersionCheck || EditorUtility.DisplayDialog(
                        "バージョン不一致",
                        $"{result.ErrorMessage}\n\n続行しますか？",
                        "はい、送信する（Send anyway）", "キャンセル");

                    if (proceed)
                    {
                        _sessionSkipVersionCheck = true;
                        Debug.LogWarning(
                            $"[DistributedRecorder] バージョン不一致のため上書き送信: ジョブ {jobIdShort}…");

                        DispatchResult retryResult;
                        try
                        {
                            retryResult = await dispatcher.DispatchAsync(worker, request,
                                skipVersionCheck: true,
                                skipHashCheck:    _sessionSkipHashCheck);
                        }
                        catch (Exception ex)
                        {
                            vm.State = JobState.Failed;
                            Debug.LogError(
                                $"[DistributedRecorder] 上書き再送失敗 ジョブ {jobIdShort}…: {ex.Message}");
                            return false;
                        }

                        if (!retryResult.Success)
                        {
                            vm.State = JobState.Failed;
                            Debug.LogError(
                                $"[DistributedRecorder] 上書き再送が拒否されました ジョブ {jobIdShort}…: " +
                                retryResult.ErrorMessage);
                            EditorUtility.DisplayDialog("送信失敗",
                                retryResult.ErrorMessage, "OK");
                            return false;
                        }

                        vm.State = JobState.Running;
                        vm.Phase = JobPhase.Preparing; // dispatch-status-labels
                        Debug.Log(
                            $"[DistributedRecorder] ジョブ {jobIdShort}… → {worker.displayName}: " +
                            "バージョン上書き送信完了");
                        return true;
                    }

                    vm.State = JobState.Failed;
                    return false;
                }

                case DispatchFailReason.HashMismatch:
                {
                    // Extract short hashes for dialog body (same helper as DistributedRecorderWindow)
                    string masterShort = ExtractHashShort(result.ErrorMessage, "master=");
                    string localShort  = ExtractHashShort(result.ErrorMessage, "local=");

                    bool proceed = _sessionSkipHashCheck || EditorUtility.DisplayDialog(
                        "プロジェクトハッシュ不一致",
                        "Master と Worker のプロジェクト内容が異なります（hash mismatch）。\n" +
                        "Worker は自分のローカル版プロジェクトで録画します。続行しますか？\n\n" +
                        $"Master: {masterShort}\nWorker: {localShort}",
                        "上書き送信（Send anyway）", "キャンセル");

                    if (proceed)
                    {
                        _sessionSkipHashCheck = true;
                        Debug.LogWarning(
                            $"[DistributedRecorder] ハッシュ不一致のため上書き送信: ジョブ {jobIdShort}…");

                        DispatchResult retryResult;
                        try
                        {
                            retryResult = await dispatcher.DispatchAsync(worker, request,
                                skipVersionCheck: _sessionSkipVersionCheck,
                                skipHashCheck:    true);
                        }
                        catch (Exception ex)
                        {
                            vm.State = JobState.Failed;
                            Debug.LogError(
                                $"[DistributedRecorder] ハッシュ上書き再送失敗 ジョブ {jobIdShort}…: {ex.Message}");
                            return false;
                        }

                        if (!retryResult.Success)
                        {
                            vm.State = JobState.Failed;
                            Debug.LogError(
                                $"[DistributedRecorder] ハッシュ上書き再送が拒否されました ジョブ {jobIdShort}…: " +
                                retryResult.ErrorMessage);
                            EditorUtility.DisplayDialog("送信失敗",
                                retryResult.ErrorMessage, "OK");
                            return false;
                        }

                        vm.State = JobState.Running;
                        vm.Phase = JobPhase.Preparing; // dispatch-status-labels
                        Debug.Log(
                            $"[DistributedRecorder] ジョブ {jobIdShort}… → {worker.displayName}: " +
                            "ハッシュ上書き送信完了");
                        return true;
                    }

                    vm.State = JobState.Failed;
                    return false;
                }

                case DispatchFailReason.CommitMismatch:
                {
                    // commit-based-project-verification: Worker's git HEAD differs from Master's.
                    // Show a dialog analogous to HashMismatch, with commit SHAs in the body.
                    string masterCommit = ExtractHashShort(result.ErrorMessage, "master=");
                    string workerCommit = ExtractHashShort(result.ErrorMessage, "worker=");

                    bool proceed = _sessionSkipHashCheck || EditorUtility.DisplayDialog(
                        "コミット不一致",
                        "Master と Worker の git コミットが異なります（commit mismatch）。\n" +
                        "Worker は自分のローカル版プロジェクト（別コミット）で録画します。\n" +
                        "続行しますか？\n\n" +
                        $"Master commit: {masterCommit}\nWorker commit: {workerCommit}",
                        "上書き送信（Send anyway）", "キャンセル");

                    if (proceed)
                    {
                        _sessionSkipHashCheck = true;
                        Debug.LogWarning(
                            $"[DistributedRecorder] コミット不一致のため上書き送信: ジョブ {jobIdShort}…");

                        DispatchResult retryResult;
                        try
                        {
                            retryResult = await dispatcher.DispatchAsync(worker, request,
                                skipVersionCheck: _sessionSkipVersionCheck,
                                skipHashCheck:    true);
                        }
                        catch (Exception ex)
                        {
                            vm.State = JobState.Failed;
                            Debug.LogError(
                                $"[DistributedRecorder] コミット上書き再送失敗 ジョブ {jobIdShort}…: {ex.Message}");
                            return false;
                        }

                        if (!retryResult.Success)
                        {
                            vm.State = JobState.Failed;
                            Debug.LogError(
                                $"[DistributedRecorder] コミット上書き再送が拒否されました ジョブ {jobIdShort}…: " +
                                retryResult.ErrorMessage);
                            EditorUtility.DisplayDialog("送信失敗",
                                retryResult.ErrorMessage, "OK");
                            return false;
                        }

                        vm.State = JobState.Running;
                        vm.Phase = JobPhase.Preparing; // dispatch-status-labels
                        Debug.Log(
                            $"[DistributedRecorder] ジョブ {jobIdShort}… → {worker.displayName}: " +
                            "コミット上書き送信完了");
                        return true;
                    }

                    vm.State = JobState.Failed;
                    return false;
                }

                case DispatchFailReason.Unreachable:
                {
                    // dispatch-worker-liveness §B – Unreachable failover:
                    // Add the failed Worker to the per-job exclusion set so it is not
                    // selected again for this job.  If an untried online Worker remains,
                    // set vm.State = Queued and return false — the caller
                    // (DispatchQueuedJobAsync) will re-enqueue, and AfterFailedDispatch
                    // → TryDispatchNextQueuedJob will select a different Worker.
                    // If all Workers have now been tried, set the terminal Unreachable
                    // state here.
                    //
                    // vm.TriedWorkers is initialized in StartDistributedRecordingInternalAsync;
                    // defensive null-guard in case this method is called from another path.
                    if (vm.TriedWorkers == null)
                        vm.TriedWorkers = new HashSet<string>();
                    vm.TriedWorkers.Add(worker.displayName);
                    Debug.LogWarning(
                        $"[DistributedRecorder] ジョブ {jobIdShort}… → {worker.displayName}: " +
                        $"Worker に到達できません (tried: {string.Join(", ", vm.TriedWorkers)}).\n{result.ErrorMessage}");

                    // dispatch-worker-liveness §B2 (Major fix): use the pre-flight online
                    // list as the failover candidate pool to avoid trying pre-flight-offline
                    // Workers (which would each cost 10 s DispatchAsync /health timeout).
                    // Fall back to EnabledWorkers only if the batch is already finalized.
                    var allWorkers = (_batchOnlineWorkers != null && _batchOnlineWorkers.Count > 0)
                        ? (IReadOnlyList<WorkerInfo>)_batchOnlineWorkers
                        : _distWorkerRegistry?.EnabledWorkers;
                    bool hasMore   = HasUntried(allWorkers, vm.TriedWorkers);
                    if (hasMore)
                    {
                        // Failover: requeue for dispatch to a different Worker.
                        vm.State = JobState.Queued;
                        Debug.Log(
                            $"[DistributedRecorder] ジョブ {jobIdShort}…: 別 Worker へ failover します。");
                        // Caller re-enqueues when State == Queued.
                        return false;
                    }

                    // All Workers tried — terminal failure.
                    vm.State = JobState.Unreachable;
                    Debug.LogError(
                        $"[DistributedRecorder] ジョブ {jobIdShort}…: " +
                        "すべての Worker を試みましたが到達できませんでした。Unreachable に設定します。");
                    return false;
                }

                case DispatchFailReason.WorkerBusy:
                {
                    // Transient busy (HTTP 503): hold the job in Queued state for
                    // re-dispatch by the scheduler when the Worker becomes available.
                    // RetryCount is incremented here; if it exceeds MaxJobRetries the
                    // job is failed permanently to avoid infinite looping.
                    vm.RetryCount++;
                    if (vm.RetryCount > MaxJobRetries)
                    {
                        vm.State = JobState.Failed;
                        Debug.LogError(
                            $"[DistributedRecorder] ジョブ {jobIdShort}… → {worker.displayName}: " +
                            $"WorkerBusy リトライ上限 ({MaxJobRetries}) 超過。Failed に設定します。");
                        return false;
                    }

                    vm.State = JobState.Queued;
                    Debug.LogWarning(
                        $"[DistributedRecorder] ジョブ {jobIdShort}… → {worker.displayName}: " +
                        $"Worker がビジーです (retry {vm.RetryCount}/{MaxJobRetries})。キューに戻します。");
                    // Caller (DispatchQueuedJobAsync) re-enqueues when State == Queued.
                    return false;
                }

                default:
                    // Bug fix (commit-based-project-verification F8):
                    // Previously this default case set State=Failed silently (no dialog).
                    // Now we always show the reason in a dialog so the user knows why the
                    // job was rejected (no silent / "無言 Failed" any more).
                    vm.State = JobState.Failed;
                    Debug.LogWarning(
                        $"[DistributedRecorder] ジョブ {jobIdShort}… → {worker.displayName}: " +
                        $"拒否されました ({result.FailReason})。\n{result.ErrorMessage}");
                    if (!string.IsNullOrEmpty(result.ErrorMessage))
                    {
                        EditorUtility.DisplayDialog(
                            $"ジョブ拒否 ({result.FailReason})",
                            $"Worker '{worker.displayName}' からジョブが拒否されました。\n\n{result.ErrorMessage}",
                            "OK");
                    }
                    return false;
            }
        }

        // -----------------------------------------------------------------------
        // Progress monitoring
        // -----------------------------------------------------------------------

        /// <summary>
        /// Starts a <see cref="ProgressMonitor"/> that hooks into the
        /// dispatch-retry-queue scheduler: when the job terminates (Completed or Failed)
        /// <see cref="OnJobTerminated"/> is called to free the Worker slot and dispatch
        /// the next queued job.
        ///
        /// Also wires up the failsafe health-poll on <c>OnError</c> so that a dropped
        /// NDJSON stream does not stall the queue indefinitely.
        /// </summary>
        private void StartProgressMonitorWithScheduler(WorkerInfo worker, MtrJobViewModel vm)
        {
            // Use the batch-scoped authenticator; null guard in case batch was
            // already finalized (should not happen in normal operation).
            var auth = _batchAuth;
            if (auth == null)
            {
                Debug.LogError("[DistributedRecorder] StartProgressMonitorWithScheduler: バッチ認証が null です。");
                return;
            }

            var monitor = new ProgressMonitor(auth);

            monitor.OnProgress += evt =>
            {
                var capturedEvt = evt;
                EditorCallbackOnce(() =>
                {
                    vm.State        = capturedEvt.state;
                    vm.CurrentFrame = capturedEvt.currentFrame;
                    vm.TotalFrames  = capturedEvt.totalFrames;

                    // dispatch-status-labels: advance Phase based on progress event.
                    // Transition Preparing → Recording on first frame event with frames known.
                    // Terminal phases are set by the state check below.
                    if (!IsTerminalState(capturedEvt.state))
                    {
                        if (capturedEvt.totalFrames > 0)
                            vm.Phase = JobPhase.Recording;
                        else if (vm.Phase == JobPhase.Queued || vm.Phase == JobPhase.Sending)
                            vm.Phase = JobPhase.Preparing;
                    }

                    // Keep last-progress timestamp fresh for the failsafe
                    _jobLastProgressTime[vm.JobId] = EditorApplication.timeSinceStartup;

                    if (!string.IsNullOrEmpty(capturedEvt.message))
                        Debug.Log($"[DistributedRecorder] {vm.JobId.Substring(0, 8)}… {capturedEvt.message}");

                    if (capturedEvt.state == JobState.Completed)
                    {
                        _ = DownloadResultsAsync(worker, vm);
                        // Free the Worker slot and dispatch next queued job
                        OnJobTerminated(worker, vm);
                    }
                    else if (capturedEvt.state == JobState.Failed)
                    {
                        Debug.LogError(
                            $"[DistributedRecorder] ジョブ {vm.JobId.Substring(0, 8)}… が失敗しました。");
                        OnJobTerminated(worker, vm);
                    }

                    Repaint();
                });
            };

            monitor.OnError += err =>
            {
                var capturedErr = err;
                // worker-reload-survival 案D: stream errors during in-flight recording
                // may be caused by a transient domain-reload gap.  Instead of immediately
                // failing, attempt to reconnect up to ReconnectMaxAttempts times.
                // Only fall back to HandleProgressStreamErrorAsync when all retries are
                // exhausted or the Worker is confirmed idle via /health.
                EditorCallbackOnce(() =>
                {
                    if (IsTerminalState(vm.State)) return; // job already done

                    _jobReconnectCount.TryGetValue(vm.JobId, out int attempts);
                    string jobIdShort = vm.JobId.Length >= 8 ? vm.JobId.Substring(0, 8) : vm.JobId;

                    if (attempts < ReconnectMaxAttempts)
                    {
                        _jobReconnectCount[vm.JobId] = attempts + 1;
                        Debug.LogWarning(
                            $"[DistributedRecorder] 進捗ストリーム切断 (jobId={jobIdShort}…, " +
                            $"retry {attempts + 1}/{ReconnectMaxAttempts}): {capturedErr}\n" +
                            "Worker のドメインリロード中の可能性があります。再接続を試みます。");

                        // Re-arm the last-progress timestamp so stall watchdog does not
                        // fire immediately during the reconnect delay.
                        _jobLastProgressTime[vm.JobId] = EditorApplication.timeSinceStartup;

                        // Reconnect after a short delay using a fire-and-forget task.
                        _ = ReconnectProgressMonitorAsync(worker, vm, attempts + 1);
                    }
                    else
                    {
                        Debug.LogError(
                            $"[DistributedRecorder] 進捗ストリームエラー (jobId={jobIdShort}…): {capturedErr}\n" +
                            $"再接続上限 ({ReconnectMaxAttempts}) 到達。Worker の health を確認します。");
                        _ = HandleProgressStreamErrorAsync(worker, vm);
                    }

                    Repaint();
                });
            };

            monitor.Start(worker.BaseUrl, vm.JobId);

            // worker-reload-survival 案D: register stall watchdog when first job starts
            EnsureStallWatchdogRegistered();
        }

        /// <summary>
        /// worker-reload-survival 案D: reconnects the ProgressMonitor for an in-flight
        /// job after a delay to absorb the Worker domain-reload gap.
        ///
        /// Called from the OnError handler when <c>attempts &lt; ReconnectMaxAttempts</c>.
        /// </summary>
        private async Task ReconnectProgressMonitorAsync(WorkerInfo worker, MtrJobViewModel vm, int attempt)
        {
            // Delay to give the Worker time to complete the domain reload.
            await Task.Delay(TimeSpan.FromSeconds(ReconnectDelaySeconds)).ConfigureAwait(false);

            EditorCallbackOnce(() =>
            {
                if (IsTerminalState(vm.State)) return; // already resolved while we waited
                if (_batchAuth == null)             return; // batch finalized

                string jobIdShort = vm.JobId.Length >= 8 ? vm.JobId.Substring(0, 8) : vm.JobId;
                Debug.Log($"[DistributedRecorder] 進捗ストリーム再接続 (jobId={jobIdShort}…, attempt={attempt})");

                // Restart the monitor — this wires up the same OnProgress/OnError closures
                // (but creates a fresh ProgressMonitor instance).
                StartProgressMonitorWithScheduler(worker, vm);
            });
        }

        /// <summary>
        /// worker-reload-survival 案D: registers the stall watchdog on
        /// <c>EditorApplication.update</c> if not already registered.
        /// The watchdog auto-unregisters when no jobs are in flight.
        /// </summary>
        private void EnsureStallWatchdogRegistered()
        {
            if (_stallWatchdogRegistered) return;
            _stallWatchdogRegistered = true;
            EditorApplication.update += StallWatchdogTick;
        }

        /// <summary>
        /// worker-reload-survival 案D: periodic stall watchdog.
        ///
        /// Called every Editor frame via <c>EditorApplication.update</c>.
        /// For each in-flight job where no progress has been received for
        /// <see cref="FailsafeStallSeconds"/>, issues a patient /health probe
        /// (timeout <see cref="InFlightHealthTimeoutSeconds"/>).
        ///
        /// Outcomes:
        ///   - Worker idle (job completed/failed there): free slot → OnJobTerminated.
        ///   - Worker still running this job: reset last-progress time (progress is
        ///     expected again soon) and let reconnect handle the stream.
        ///   - Health poll fails (Worker unreachable): increment reconnect counter.
        ///     If exhausted → OnJobTerminated with Failed.
        ///
        /// Exposed <c>internal</c> for pure-logic EditMode tests via
        /// <see cref="IsJobStalled"/>.
        /// </summary>
        private void StallWatchdogTick()
        {
            if (_jobLastProgressTime.Count == 0)
            {
                // No in-flight jobs — unregister to avoid unnecessary ticks
                EditorApplication.update -= StallWatchdogTick;
                _stallWatchdogRegistered  = false;
                return;
            }

            double now = EditorApplication.timeSinceStartup;

            // Snapshot keys to avoid modifying the dict while iterating
            var jobIds = new List<string>(_jobLastProgressTime.Keys);
            foreach (string jobId in jobIds)
            {
                if (_stallWatchdogActive.Contains(jobId)) continue; // already processing

                if (!_jobLastProgressTime.TryGetValue(jobId, out double lastTime)) continue;

                if (!IsJobStalled(now, lastTime, FailsafeStallSeconds)) continue;

                // Find the view model and worker
                MtrJobViewModel vm = null;
                foreach (var j in _dispatchedJobs)
                {
                    if (j.JobId == jobId) { vm = j; break; }
                }

                if (vm == null || IsTerminalState(vm.State)) continue;

                if (!_jobWorkerMap.TryGetValue(jobId, out WorkerInfo stalledWorker)) continue;

                string jobIdShort = jobId.Length >= 8 ? jobId.Substring(0, 8) : jobId;
                Debug.LogWarning(
                    $"[DistributedRecorder] Stall watchdog: ジョブ {jobIdShort}… の進捗が " +
                    $"{FailsafeStallSeconds}s 途絶えています。Worker health を確認します。");

                _stallWatchdogActive.Add(jobId);

                // Fire async health probe with patient timeout (absorbed domain-reload gap)
                _ = HandleStalledJobAsync(stalledWorker, vm);
            }
        }

        /// <summary>
        /// worker-reload-survival 案D: pure-logic stall predicate.
        ///
        /// Returns <c>true</c> when <c>now - lastProgressTime &gt;= stallThreshold</c>.
        /// Public static for hermetic EditMode tests.
        /// </summary>
        public static bool IsJobStalled(double nowTime, double lastProgressTime, double stallThreshold)
            => (nowTime - lastProgressTime) >= stallThreshold;

        /// <summary>
        /// Public accessor for <see cref="FailsafeStallSeconds"/> used by EditMode tests.
        /// </summary>
        public static double FailsafeStallSecondsPublic => FailsafeStallSeconds;

        // -----------------------------------------------------------------------
        // retry-failed-collection (phase 1): automatic re-collection watchdog
        // -----------------------------------------------------------------------

        /// <summary>
        /// Pure predicate: decides whether a job should be auto-retried right now.
        ///
        /// All conditions must hold:
        /// <list type="bullet">
        ///   <item><paramref name="state"/> == <see cref="JobState.Completed"/> and
        ///     <paramref name="downloadState"/> == <see cref="DownloadState.Failed"/>
        ///     (the "回収失敗" case — never touches jobs that are still in flight or
        ///     genuinely Failed/Unreachable on the Worker side).</item>
        ///   <item><paramref name="failureKind"/> == <see cref="DownloadFailureKind.Connection"/>
        ///     — a NotFound (404, Worker forgot the job) is never auto-retried; that is a
        ///     phase-2 concern and retrying blindly would just repeat the same 404.</item>
        ///   <item><paramref name="autoRetryCount"/> &lt; <paramref name="maxAutoRetries"/> —
        ///     the auto-retry budget is not exhausted (manual retries reset this counter).</item>
        ///   <item>The exponential-backoff delay for attempt <c>autoRetryCount + 1</c> has
        ///     elapsed since <paramref name="lastAttemptTime"/>:
        ///     <c>baseDelaySeconds * 2^autoRetryCount</c>.</item>
        /// </list>
        ///
        /// Made <c>public static</c> for hermetic EditMode tests.
        /// </summary>
        public static bool ShouldAutoRetryDownload(
            JobState state,
            DownloadState downloadState,
            DownloadFailureKind failureKind,
            int autoRetryCount,
            double lastAttemptTime,
            double nowTime,
            int maxAutoRetries,
            double baseDelaySeconds)
        {
            if (state != JobState.Completed || downloadState != DownloadState.Failed)
                return false;

            if (failureKind != DownloadFailureKind.Connection)
                return false;

            if (autoRetryCount >= maxAutoRetries)
                return false;

            double backoffDelay = baseDelaySeconds * Math.Pow(2, autoRetryCount);
            return (nowTime - lastAttemptTime) >= backoffDelay;
        }

        /// <summary>
        /// Registers <see cref="AutoRetryDownloadsTick"/> on
        /// <c>EditorApplication.update</c> if not already registered. Mirrors
        /// <see cref="EnsureStallWatchdogRegistered"/>.
        /// </summary>
        private void EnsureAutoRetryWatchdogRegistered()
        {
            if (_autoRetryWatchdogRegistered) return;
            _autoRetryWatchdogRegistered = true;
            EditorApplication.update += AutoRetryDownloadsTick;
        }

        /// <summary>
        /// Called every Editor frame via <c>EditorApplication.update</c> (throttled to
        /// <see cref="AutoRetryTickIntervalSeconds"/>). Scans <see cref="_dispatchedJobs"/>
        /// for jobs eligible per <see cref="ShouldAutoRetryDownload"/> and retries them via
        /// the same idempotent <see cref="DownloadResultsAsync"/> entry point used by the
        /// manual buttons (<c>isManualRetry: false</c> — silent, no DisplayDialog, and does
        /// not reset <see cref="MtrJobViewModel.AutoRetryCount"/>).
        ///
        /// Auto-unregisters when there are no dispatched jobs at all, mirroring
        /// <see cref="StallWatchdogTick"/>'s self-unregistration. Registration itself is
        /// lazy: <see cref="EnsureAutoRetryWatchdogRegistered"/> is called from
        /// <see cref="DownloadResultsAsync"/>'s Connection-failure paths, so the tick only
        /// starts running once at least one job has actually failed to download; it
        /// re-registers itself the next time a job fails after a prior all-clear unregister.
        /// </summary>
        private void AutoRetryDownloadsTick()
        {
            if (_dispatchedJobs.Count == 0)
            {
                EditorApplication.update -= AutoRetryDownloadsTick;
                _autoRetryWatchdogRegistered = false;
                return;
            }

            double now = EditorApplication.timeSinceStartup;
            if (now - _lastAutoRetryTickTime < AutoRetryTickIntervalSeconds)
                return;
            _lastAutoRetryTickTime = now;

            // Snapshot to avoid mutating _dispatchedJobs while iterating (defensive;
            // DownloadResultsAsync mutates vm fields in place, not the list itself).
            var jobs = new List<MtrJobViewModel>(_dispatchedJobs);
            foreach (var vm in jobs)
            {
                if (_autoRetryActive.Contains(vm.JobId)) continue; // already retrying

                bool eligible = ShouldAutoRetryDownload(
                    vm.State,
                    vm.DownloadState,
                    vm.LastDownloadFailureKind,
                    vm.AutoRetryCount,
                    vm.LastDownloadAttemptTime,
                    now,
                    MaxAutoRetries,
                    AutoRetryBaseDelaySeconds);

                if (!eligible) continue;
                if (vm.Worker == null) continue; // defensive: no endpoint to retry against

                string jobIdShort = vm.JobId.Length >= 8 ? vm.JobId.Substring(0, 8) : vm.JobId;
                Debug.Log(
                    $"[DistributedRecorder] 自動再回収: ジョブ {jobIdShort}… " +
                    $"(試行 {vm.AutoRetryCount + 1}/{MaxAutoRetries})");

                vm.AutoRetryCount++;
                _autoRetryActive.Add(vm.JobId);
                _ = AutoRetryOneAsync(vm);
            }
        }

        /// <summary>
        /// Runs one automatic retry attempt for <paramref name="vm"/> and clears the
        /// <see cref="_autoRetryActive"/> re-entrancy guard when done, regardless of outcome.
        /// Split out from <see cref="AutoRetryDownloadsTick"/> so the tick itself stays
        /// synchronous (fire-and-forget per job, like <see cref="HandleStalledJobAsync"/>'s
        /// call site).
        /// </summary>
        private async Task AutoRetryOneAsync(MtrJobViewModel vm)
        {
            try
            {
                // isManualRetry: false — silent (no dialog) and preserves AutoRetryCount,
                // which DownloadResultsAsync itself increments only via the manual-reset
                // path; the tick above already incremented it for this attempt.
                await DownloadResultsAsync(vm.Worker, vm, isManualRetry: false);
            }
            finally
            {
                _autoRetryActive.Remove(vm.JobId);
            }
        }

        /// <summary>
        /// worker-reload-survival 案D: async health probe for a stalled in-flight job.
        ///
        /// Uses <see cref="InFlightHealthTimeoutSeconds"/> (patient) instead of the
        /// pre-flight 3 s so that a Worker recovering from domain-reload can respond.
        ///
        /// Outcomes:
        ///   (a) Worker idle (job done there): if Completed, trigger download + OnJobTerminated.
        ///       If Worker is idle but we never saw Completed, mark Failed.
        ///   (b) Worker still running this job: reset last-progress time; let monitor reconnect.
        ///   (c) Health poll fails: count as a reconnect attempt; when exhausted → Failed.
        /// </summary>
        private async Task HandleStalledJobAsync(WorkerInfo worker, MtrJobViewModel vm)
        {
            if (_batchAuth == null)
            {
                _stallWatchdogActive.Remove(vm.JobId);
                return;
            }

            string jobIdShort = vm.JobId.Length >= 8 ? vm.JobId.Substring(0, 8) : vm.JobId;

            try
            {
                var healthTransport = new HttpTransport(_batchAuth);
                string healthJson;
                try
                {
                    // patient timeout to absorb domain-reload gap (§F-4 tuning target)
                    healthJson = await healthTransport.GetAsync(
                        $"{worker.BaseUrl}/health",
                        TimeSpan.FromSeconds(InFlightHealthTimeoutSeconds)).ConfigureAwait(false);
                }
                finally
                {
                    healthTransport.Dispose();
                }

                var health  = ProtocolSerializer.Deserialize<WorkerHealth>(healthJson);
                var outcome = ClassifyWorkerJobOutcome(health, vm.JobId);

                EditorCallbackOnce(() =>
                {
                    _stallWatchdogActive.Remove(vm.JobId);
                    if (IsTerminalState(vm.State)) return;

                    switch (outcome)
                    {
                        case WorkerJobOutcome.StillRunning:
                            // Worker is actively recording — reset progress time and wait.
                            Debug.Log(
                                $"[DistributedRecorder] Stall watchdog: Worker はジョブ {jobIdShort}… を実行中。" +
                                "進捗タイムスタンプをリセットします。");
                            _jobLastProgressTime[vm.JobId] = EditorApplication.timeSinceStartup;
                            break;

                        case WorkerJobOutcome.CompletedElsewhere:
                            // Worker finished the job; stream missed the terminal event.
                            Debug.Log(
                                $"[DistributedRecorder] Stall watchdog: Worker がジョブ {jobIdShort}… " +
                                "を完了済みと判定。結果を回収します。");
                            vm.State = JobState.Completed;
                            _ = DownloadResultsAsync(worker, vm);
                            OnJobTerminated(worker, vm);
                            break;

                        case WorkerJobOutcome.FailedOnWorker:
                            Debug.LogError(
                                $"[DistributedRecorder] Stall watchdog: Worker がジョブ {jobIdShort}… " +
                                "を Failed と報告しました。");
                            vm.State = JobState.Failed;
                            OnJobTerminated(worker, vm);
                            break;

                        default: // WorkerJobOutcome.Unknown
                            // Idle, state indeterminate — treat as Completed (false-negative avoidance).
                            Debug.LogWarning(
                                $"[DistributedRecorder] Stall watchdog: Worker アイドル、ジョブ " +
                                $"{jobIdShort}… の状態不明 → Completed 扱いで回収を試みます。");
                            vm.State = JobState.Completed;
                            _ = DownloadResultsAsync(worker, vm);
                            OnJobTerminated(worker, vm);
                            break;
                    }

                    Repaint();
                });
            }
            catch (Exception ex)
            {
                EditorCallbackOnce(() =>
                {
                    _stallWatchdogActive.Remove(vm.JobId);
                    if (IsTerminalState(vm.State)) return;

                    _jobReconnectCount.TryGetValue(vm.JobId, out int attempts);
                    if (attempts < ReconnectMaxAttempts)
                    {
                        _jobReconnectCount[vm.JobId] = attempts + 1;
                        // Reset timestamp so we wait another FailsafeStallSeconds before probing again
                        _jobLastProgressTime[vm.JobId] = EditorApplication.timeSinceStartup;
                        Debug.LogWarning(
                            $"[DistributedRecorder] Stall watchdog health チェック失敗 " +
                            $"(job={jobIdShort}…, attempt {attempts + 1}/{ReconnectMaxAttempts}): {ex.Message}");
                    }
                    else
                    {
                        Debug.LogError(
                            $"[DistributedRecorder] Stall watchdog health チェック上限到達 " +
                            $"(job={jobIdShort}…): {ex.Message} → Failed");
                        vm.State = JobState.Failed;
                        OnJobTerminated(worker, vm);
                    }

                    Repaint();
                });
            }
        }

        /// <summary>
        /// Failsafe: called when the progress stream emits an error AND reconnect
        /// attempts are exhausted.
        ///
        /// Polls <c>GET /health</c> with a patient timeout (same as the in-flight stall
        /// watchdog) to determine the Worker's actual state, then routes:
        ///
        ///   • Worker still running this job → log warning only (stream will reconnect
        ///     via the stall watchdog; do NOT mark Failed).
        ///   • Worker is idle (currentJobId moved on or empty) → treat as Completed and
        ///     trigger result download.  The Worker completed the job before the master
        ///     could (re-)connect to the progress stream.  Only mark Failed when the
        ///     Worker explicitly reports the job as Failed via currentJobState.
        ///   • Worker unreachable (health GET throws) → mark Failed with reason in log
        ///     (no silent / "無言 Failed").
        ///
        /// This replaces the previous "idle → unconditional Failed" behaviour that caused
        /// short jobs (or jobs that finished while the master was reconnecting) to be
        /// incorrectly classified as Failed even though jobsProcessed on the Worker
        /// showed the recording had completed successfully.
        /// </summary>
        private async Task HandleProgressStreamErrorAsync(WorkerInfo worker, MtrJobViewModel vm)
        {
            if (_batchAuth == null) return;

            string jobIdShort = vm.JobId.Length >= 8 ? vm.JobId.Substring(0, 8) : vm.JobId;

            try
            {
                var healthTransport = new HttpTransport(_batchAuth);
                string healthJson;
                try
                {
                    // Use the patient in-flight timeout rather than the pre-flight 3 s.
                    // Even though reconnect retries are exhausted, the Worker may still be
                    // alive and finishing the job — we must not fast-fail here.
                    healthJson = await healthTransport.GetAsync(
                        $"{worker.BaseUrl}/health",
                        TimeSpan.FromSeconds(InFlightHealthTimeoutSeconds)).ConfigureAwait(false);
                }
                finally
                {
                    healthTransport.Dispose();
                }

                var health = ProtocolSerializer.Deserialize<WorkerHealth>(healthJson);

                // Determine the Worker's relationship to this job from health fields.
                // ClassifyWorkerJobOutcome is a pure function so it can be unit-tested.
                var outcome = ClassifyWorkerJobOutcome(health, vm.JobId);

                EditorCallbackOnce(() =>
                {
                    if (IsTerminalState(vm.State)) return;

                    switch (outcome)
                    {
                        case WorkerJobOutcome.StillRunning:
                            // Worker is actively recording — do not mark Failed.
                            // The stall watchdog will probe again after FailsafeStallSeconds.
                            Debug.LogWarning(
                                $"[DistributedRecorder] フェイルセーフ health チェック: Worker はまだ " +
                                $"ジョブ {jobIdShort}… を実行中。再接続は StallWatchdog に委ねます。");
                            // Reset last-progress time so watchdog waits another full window.
                            _jobLastProgressTime[vm.JobId] = EditorApplication.timeSinceStartup;
                            break;

                        case WorkerJobOutcome.CompletedElsewhere:
                            // Worker finished this job (moved to next job or idle after success).
                            // Treat as Completed and pull results — the stream just missed the
                            // terminal event.
                            Debug.Log(
                                $"[DistributedRecorder] フェイルセーフ: Worker がジョブ {jobIdShort}… " +
                                "を完了済みと判定。結果を回収します (ストリームは接続できませんでした)。");
                            vm.State = JobState.Completed;
                            _ = DownloadResultsAsync(worker, vm);
                            OnJobTerminated(worker, vm);
                            break;

                        case WorkerJobOutcome.FailedOnWorker:
                            // Worker explicitly reported the job as Failed.
                            Debug.LogError(
                                $"[DistributedRecorder] フェイルセーフ: Worker がジョブ {jobIdShort}… " +
                                "を Failed と報告しました (currentJobState=Failed)。");
                            vm.State = JobState.Failed;
                            OnJobTerminated(worker, vm);
                            break;

                        default: // WorkerJobOutcome.Unknown — idle but state unclear
                            // Worker is idle and we cannot determine outcome from health alone.
                            // Treat as Completed (false-negative avoidance): it is worse to
                            // mark a successfully-recorded job as Failed than to attempt a
                            // result download that may find no files.
                            Debug.LogWarning(
                                $"[DistributedRecorder] フェイルセーフ: Worker がアイドル、ジョブ " +
                                $"{jobIdShort}… の結果状態不明 → Completed 扱いで回収を試みます。");
                            vm.State = JobState.Completed;
                            _ = DownloadResultsAsync(worker, vm);
                            OnJobTerminated(worker, vm);
                            break;
                    }

                    Repaint();
                });
            }
            catch (Exception ex)
            {
                EditorCallbackOnce(() =>
                {
                    if (IsTerminalState(vm.State)) return;

                    // Health check failed → Worker is unreachable.  This IS a genuine failure.
                    Debug.LogError(
                        $"[DistributedRecorder] フェイルセーフ health チェック失敗 " +
                        $"(job={jobIdShort}…): {ex.Message} → Failed");
                    vm.State = JobState.Failed;
                    OnJobTerminated(worker, vm);
                    Repaint();
                });
            }
        }

        // -----------------------------------------------------------------------
        // Progress-loss classification helpers
        // -----------------------------------------------------------------------

        /// <summary>
        /// Describes the outcome of an in-flight job as seen from a fresh
        /// <see cref="WorkerHealth"/> snapshot.
        ///
        /// Used by <see cref="HandleProgressStreamErrorAsync"/> and
        /// <see cref="HandleStalledJobAsync"/> to decide whether to mark the job
        /// Completed (and trigger download), Failed, or keep it Running.
        /// </summary>
        public enum WorkerJobOutcome
        {
            /// <summary>Worker is actively executing this job.</summary>
            StillRunning,
            /// <summary>
            /// Worker is idle or running a different job, and the job state is not
            /// explicitly Failed — treat as Completed and pull results.
            /// </summary>
            CompletedElsewhere,
            /// <summary>Worker explicitly reports this job as Failed.</summary>
            FailedOnWorker,
            /// <summary>
            /// Worker is idle but the last job state cannot be determined (e.g. the
            /// currentJobId already moved on and we have no explicit state).
            /// Caller should treat this as Completed (false-negative avoidance).
            /// </summary>
            Unknown,
        }

        /// <summary>
        /// Pure function: classifies what happened to <paramref name="jobId"/> on a
        /// Worker based on a fresh <see cref="WorkerHealth"/> snapshot.
        ///
        /// This is the single source of truth for the progress-loss decision tree and
        /// is exposed <c>public static</c> so hermetic EditMode tests can exercise every
        /// branch without instantiating the EditorWindow.
        ///
        /// Decision tree:
        /// <list type="bullet">
        ///   <item>alive=false → Unknown (caller treats as Completed to avoid false-fail)</item>
        ///   <item>currentJobId == jobId &amp;&amp; state is non-terminal → StillRunning</item>
        ///   <item>currentJobId == jobId &amp;&amp; state == Failed → FailedOnWorker</item>
        ///   <item>currentJobId == jobId &amp;&amp; state == Completed → CompletedElsewhere</item>
        ///   <item>currentJobId ≠ jobId (idle or next job) → CompletedElsewhere</item>
        /// </list>
        /// </summary>
        public static WorkerJobOutcome ClassifyWorkerJobOutcome(WorkerHealth health, string jobId)
        {
            if (health == null) return WorkerJobOutcome.Unknown;

            // Worker unresponsive / not healthy — cannot determine outcome.
            if (!health.alive) return WorkerJobOutcome.Unknown;

            bool isCurrentJob = !string.IsNullOrEmpty(health.currentJobId)
                             && health.currentJobId == jobId;

            if (!isCurrentJob)
            {
                // Worker has moved on (running a different job, or idle).
                // This means it finished ours — classify as Completed.
                return WorkerJobOutcome.CompletedElsewhere;
            }

            // currentJobId == jobId — check the reported state.
            switch (health.currentJobState)
            {
                case JobState.Completed:
                    return WorkerJobOutcome.CompletedElsewhere;
                case JobState.Failed:
                    return WorkerJobOutcome.FailedOnWorker;
                default:
                    // Pending / Running / Queued — still in progress.
                    return WorkerJobOutcome.StillRunning;
            }
        }

        // -----------------------------------------------------------------------
        // Result download
        // -----------------------------------------------------------------------

        /// <summary>
        /// Downloads the result files for <paramref name="vm"/> from <paramref name="worker"/>.
        /// Re-entrant/idempotent: safe to call again for a job whose previous attempt
        /// failed (writes into the same <see cref="MtrJobViewModel.LocalOutputDir"/>).
        ///
        /// retry-failed-collection (phase 1) additions:
        /// <list type="bullet">
        ///   <item><paramref name="isManualRetry"/> resets <see cref="MtrJobViewModel.AutoRetryCount"/>
        ///     so a user-triggered "再回収" is never blocked by the auto-retry cap, and
        ///     (only for manual attempts) shows a <see cref="EditorUtility.DisplayDialog"/>
        ///     when the failure is not retry-worthy (result vanished on the Worker).</item>
        ///   <item>Records <see cref="MtrJobViewModel.LastDownloadFailureKind"/> /
        ///     <see cref="MtrJobViewModel.LastDownloadAttemptTime"/> on every attempt so the
        ///     periodic auto-retry watchdog (<see cref="AutoRetryDownloadsTick"/>) can decide
        ///     eligibility without re-deriving state from logs.</item>
        ///   <item>A <c>Success</c> result with zero files (Worker responded but has nothing
        ///     for this job — e.g. it forgot the job across a restart cycle) is treated as a
        ///     NotFound-style failure, not as Done, matching the "結果消失" acceptance
        ///     criterion (DownloadState stays Failed).</item>
        /// </list>
        /// </summary>
        /// <param name="worker">Worker endpoint to pull from.</param>
        /// <param name="vm">Job view model; mutated in place.</param>
        /// <param name="isManualRetry">
        /// <c>true</c> when invoked from the "再回収" / "失敗した回収を一括再試行" buttons;
        /// <c>false</c> for the original post-recording pull and for the automatic
        /// watchdog retry (which must stay silent — no dialogs from a background tick).
        /// </param>
        /// <remarks>
        /// retry-failed-collection (phase 1): returns <see cref="Task"/> (was <c>async void</c>)
        /// so <see cref="RetryFailedDownloadsAsync"/> can <c>await</c> each job sequentially.
        /// Existing fire-and-forget call sites (<c>DownloadResultsAsync(worker, vm);</c>) are
        /// source-compatible — the returned Task is simply discarded, matching prior behavior.
        /// </remarks>
        private async Task DownloadResultsAsync(
            WorkerInfo worker, MtrJobViewModel vm, bool isManualRetry = false)
        {
            if (isManualRetry)
                vm.AutoRetryCount = 0; // manual action always gets a fresh auto-retry budget

            vm.DownloadState = DownloadState.InProgress;
            // dispatch-status-labels: show "収集中" while pulling result files
            vm.Phase = JobPhase.Collecting;
            Repaint();

            Debug.Log(
                $"[DistributedRecorder] ジョブ {vm.JobId.Substring(0, 8)}… の結果を回収します。" +
                $" 保存先: {vm.LocalOutputDir}");

            // Re-use shared transport (shared HttpClient; safe to reuse for download)
            if (!SharedKeyLoader.TryLoad(out byte[] keyBytes, out string keyError))
            {
                Debug.LogError($"[DistributedRecorder] 結果回収: 共有キーのロードに失敗: {keyError}");
                vm.DownloadState = DownloadState.Failed;
                vm.LastDownloadFailureKind   = DownloadFailureKind.Connection;
                vm.LastDownloadAttemptTime   = EditorApplication.timeSinceStartup;
                EnsureAutoRetryWatchdogRegistered();
                Repaint();
                return;
            }

            var auth      = new HmacAuthenticator(keyBytes);
            var transport = new HttpTransport(auth);

            try
            {
                var downloader = new ResultDownloader(transport);
                var result = await downloader.DownloadAsync(
                    worker.BaseUrl,
                    vm.JobId,
                    vm.LocalOutputDir,
                    (name, cur, total) =>
                        Debug.Log($"[DistributedRecorder]  [{cur}/{total}] {name}"));

                vm.LastDownloadAttemptTime = EditorApplication.timeSinceStartup;

                // A Success result with zero files means the Worker answered but has
                // nothing recorded for this job (result vanished / Worker forgot it).
                // Treat this the same as a NotFound failure rather than Done.
                bool resultVanished = result.Success && result.Files.Count == 0;

                if (result.Success && !resultVanished)
                {
                    vm.DownloadState = DownloadState.Done;
                    vm.Phase = JobPhase.Terminal; // dispatch-status-labels
                    vm.LastDownloadFailureKind = DownloadFailureKind.None;
                    vm.AutoRetryCount = 0;

                    // movie-flat-collect (v1.4.18): a single-file result (e.g. a movie)
                    // is moved out of its per-job sub-folder and placed directly under
                    // the dispatch-timestamp directory, renamed to the Timeline name.
                    // Multi-file results (e.g. an image sequence) keep the pre-existing
                    // <ts>/<TimelineName>/ folder layout untouched.
                    IReadOnlyList<string> collectedFiles =
                        FlattenLocalOutputIfSingleFile(vm, result.Files);

                    Debug.Log(
                        $"[DistributedRecorder] 回収完了: {collectedFiles.Count} ファイル → {vm.LocalOutputDir}");

                    // collect-to-dir (v1.4.10): auto-copy to collect dir if set at dispatch time.
                    if (!string.IsNullOrEmpty(vm.CollectDir) &&
                        CollectPathValidator.Validate(vm.CollectDir, out _))
                    {
                        // Fire-and-forget; errors are logged inside CopyToCollectDirAsync.
                        _ = CopyToCollectDirAsync(vm, collectedFiles);
                    }
                }
                else
                {
                    vm.DownloadState = DownloadState.Failed;
                    vm.Phase = JobPhase.Terminal; // dispatch-status-labels
                    vm.LastDownloadFailureKind = resultVanished
                        ? DownloadFailureKind.NotFound
                        : result.FailureKind;

                    string reason = resultVanished
                        ? "Worker responded but reported no output files for this job."
                        : result.ErrorMessage;
                    Debug.LogError(
                        $"[DistributedRecorder] 回収失敗 ジョブ {vm.JobId.Substring(0, 8)}…: {reason}");

                    if (isManualRetry && vm.LastDownloadFailureKind == DownloadFailureKind.NotFound)
                    {
                        EditorUtility.DisplayDialog(
                            "再回収できません",
                            "成果物が Worker 上に見つかりません。\n" +
                            "既に削除されているか、Worker が再起動して情報を失った可能性があります。\n" +
                            "（フェーズ 2 で対応予定）",
                            "OK");
                    }

                    // Connection-kind failures are eligible for the automatic retry
                    // watchdog; ensure it is ticking (no-op if already registered).
                    if (vm.LastDownloadFailureKind == DownloadFailureKind.Connection)
                        EnsureAutoRetryWatchdogRegistered();
                }
            }
            finally
            {
                transport.Dispose();
                Repaint();
            }
        }

        // -----------------------------------------------------------------------
        // Flat single-file output (movie-flat-collect, v1.4.18)
        // -----------------------------------------------------------------------

        /// <summary>
        /// When exactly one file was downloaded, moves it out of its per-job
        /// sub-folder (<c>&lt;ts&gt;/&lt;TimelineName&gt;/file.ext</c>) directly under
        /// the dispatch-timestamp directory, renamed to the Timeline name
        /// (<c>&lt;ts&gt;/&lt;TimelineName&gt;.ext</c>), then removes the now-empty
        /// sub-folder. Updates <see cref="MtrJobViewModel.LocalOutputDir"/> to the new
        /// file path so the "開く" button and collect-to-dir copy target the flat file.
        ///
        /// Multi-file (or zero-file) results are left untouched — the pre-existing
        /// folder layout is used as-is, matching image-sequence output.
        ///
        /// Idempotent: re-running collection downloads into a fresh per-job folder
        /// (same path every time) and re-flattens, overwriting the previous flat file.
        /// </summary>
        /// <param name="vm">Job view model; <see cref="MtrJobViewModel.LocalOutputDir"/> is mutated in place.</param>
        /// <param name="downloadedFiles">Files written by <see cref="ResultDownloader.DownloadAsync"/>.</param>
        /// <returns>
        /// The file list to use for downstream steps (collect-to-dir): the single
        /// flattened path when flattening occurred, otherwise <paramref name="downloadedFiles"/> unchanged.
        /// </returns>
        private IReadOnlyList<string> FlattenLocalOutputIfSingleFile(
            MtrJobViewModel vm, IReadOnlyList<string> downloadedFiles)
        {
            if (downloadedFiles == null || !FlatOutputPathResolver.ShouldFlatten(downloadedFiles.Count))
                return downloadedFiles;

            string sourceFile = downloadedFiles[0];
            string perJobFolder = vm.LocalOutputDir;
            string parentDir = Path.GetDirectoryName(perJobFolder);
            string sanitizedName = Path.GetFileName(perJobFolder);

            if (string.IsNullOrEmpty(parentDir) || string.IsNullOrEmpty(sanitizedName))
                return downloadedFiles; // unexpected shape; leave the folder layout as-is

            string flatDest = FlatOutputPathResolver.ResolveLocalDestination(
                parentDir, sanitizedName, downloadedFiles.Count, Path.GetFileName(sourceFile));

            try
            {
                if (!File.Exists(sourceFile))
                    return downloadedFiles;

                // Idempotent re-collection: replace a previous flat file if present.
                if (File.Exists(flatDest))
                    File.Delete(flatDest);

                File.Move(sourceFile, flatDest);

                // Remove the now-empty per-job folder (best-effort).
                if (Directory.Exists(perJobFolder) &&
                    Directory.GetFileSystemEntries(perJobFolder).Length == 0)
                {
                    Directory.Delete(perJobFolder);
                }

                vm.LocalOutputDir = flatDest;
                return new List<string> { flatDest };
            }
            catch (Exception ex)
            {
                Debug.LogError(
                    $"[DistributedRecorder] 単一ファイルの直置き移動に失敗しました " +
                    $"({sourceFile} → {flatDest}): {ex.Message}. フォルダ構成のまま維持します。");
                return downloadedFiles;
            }
        }

        // -----------------------------------------------------------------------
        // Collect-to-dir helpers (v1.4.10)
        // -----------------------------------------------------------------------

        /// <summary>
        /// Copies <paramref name="localFiles"/> to <see cref="MtrJobViewModel.CollectDir"/>.
        ///
        /// Destination naming (movie-flat-collect, v1.4.18):
        /// <list type="bullet">
        ///   <item>Exactly one file (e.g. a movie): copied directly to
        ///     <c>CollectDir / TimelineName.ext</c> (no sub-folder). Collision avoidance
        ///     inserts <c>_disambig</c> before the extension.</item>
        ///   <item>Zero or multiple files (e.g. an image sequence): copied into
        ///     <c>CollectDir / TimelineName_disambig /</c>, unchanged from the
        ///     pre-existing behaviour
        ///     (collision avoidance via <see cref="CollectPathValidator.BuildDestinationPath"/>).</item>
        /// </list>
        ///
        /// I/O runs on a background thread (<see cref="Task.Run"/>); Unity API calls
        /// are routed to the main thread via <see cref="MainThreadDispatcher.Enqueue"/>.
        ///
        /// Security: path validation is done by the caller before this method is
        /// invoked; this method trusts that <paramref name="vm.CollectDir"/> has already
        /// been validated.
        /// </summary>
        private async Task CopyToCollectDirAsync(MtrJobViewModel vm, IReadOnlyList<string> localFiles)
        {
            if (localFiles == null || localFiles.Count == 0) return;

            string targetDir = vm.CollectDir;
            if (string.IsNullOrEmpty(targetDir)) return;

            // Use TimelineName as the label, falling back to jobId prefix.
            string label = !string.IsNullOrEmpty(vm.TimelineName) ? vm.TimelineName : vm.JobId;
            string disambig = vm.CollectDisambig ?? vm.JobId.Substring(0, Math.Min(8, vm.JobId.Length));

            bool flatten = FlatOutputPathResolver.ShouldFlatten(localFiles.Count);
            string singleFileName = flatten ? Path.GetFileName(localFiles[0]) : null;

            string dest = FlatOutputPathResolver.ResolveCollectDestination(
                targetDir,
                label,
                disambig,
                localFiles.Count,
                singleFileName,
                path => File.Exists(path) || Directory.Exists(path));

            try
            {
                await Task.Run(() =>
                {
                    if (flatten)
                    {
                        string src = localFiles[0];
                        if (!File.Exists(src)) return;

                        CollectPathValidator.EnsureDirectory(Path.GetDirectoryName(dest));
                        File.Copy(src, dest, overwrite: true); // idempotent re-collection
                    }
                    else
                    {
                        string destDir = dest;
                        CollectPathValidator.EnsureDirectory(destDir);
                        foreach (string src in localFiles)
                        {
                            if (!File.Exists(src)) continue;
                            string fileName = Path.GetFileName(src);
                            string filePath = Path.Combine(destDir, fileName);

                            // Avoid overwriting an identically-named file (simple name check).
                            if (File.Exists(filePath))
                            {
                                string stem = Path.GetFileNameWithoutExtension(fileName);
                                string ext  = Path.GetExtension(fileName);
                                filePath = Path.Combine(destDir, $"{stem}_{disambig}{ext}");
                            }

                            File.Copy(src, filePath, overwrite: false);
                        }
                    }
                }).ConfigureAwait(false);

                // Back on a thread-pool thread after ConfigureAwait(false):
                // route Unity API calls to the main thread.
                MainThreadDispatcher.Enqueue(() =>
                {
                    Debug.Log($"[DistributedRecorder] 収集完了: {localFiles.Count} ファイル → {dest}");
                    Repaint();
                });
            }
            catch (Exception ex)
            {
                MainThreadDispatcher.Enqueue(() =>
                {
                    Debug.LogError(
                        $"[DistributedRecorder] 収集コピー失敗 ジョブ {vm.JobId.Substring(0, Math.Min(8, vm.JobId.Length))}: {ex.Message}");
                    Repaint();
                });
            }
        }

        /// <summary>
        /// Copies already-downloaded files for all Completed+Done jobs to
        /// <see cref="_collectDir"/> (current UI value, not the per-job snapshot).
        ///
        /// Called by the "まとめて DL（収集先へ）" button.
        /// Guard: <see cref="_isBulkCollecting"/>.
        /// </summary>
        private async void BulkCollectAsync()
        {
            if (_isBulkCollecting) return;
            if (string.IsNullOrEmpty(_collectDir)) return;
            if (!CollectPathValidator.Validate(_collectDir, out string valErr))
            {
                EditorUtility.DisplayDialog("収集先が無効", valErr, "OK");
                return;
            }

            _isBulkCollecting = true;
            Repaint();

            try
            {
                // Snapshot the eligible job list synchronously on the main thread.
                var eligible = new List<MtrJobViewModel>();
                foreach (var job in _dispatchedJobs)
                {
                    if (job.State == JobState.Completed &&
                        job.DownloadState == DownloadState.Done &&
                        !string.IsNullOrEmpty(job.LocalOutputDir))
                    {
                        eligible.Add(job);
                    }
                }

                if (eligible.Count == 0)
                {
                    Debug.Log("[DistributedRecorder] まとめて DL: 対象ジョブ（完了 & DL済み）がありません。");
                    return;
                }

                Debug.Log($"[DistributedRecorder] まとめて DL 開始 – {eligible.Count} 件 → {_collectDir}");

                int done = 0;
                foreach (var job in eligible)
                {
                    Debug.Log(
                        $"[DistributedRecorder]   [{++done}/{eligible.Count}] " +
                        $"{job.JobId.Substring(0, Math.Min(8, job.JobId.Length))}...");

                    // movie-flat-collect (v1.4.18): LocalOutputDir may now point at a
                    // single flattened file (movie) instead of a per-job folder
                    // (image sequence). Handle both shapes.
                    List<string> files;
                    if (File.Exists(job.LocalOutputDir))
                    {
                        files = new List<string> { job.LocalOutputDir };
                    }
                    else if (Directory.Exists(job.LocalOutputDir))
                    {
                        files = new List<string>(
                            Directory.GetFiles(job.LocalOutputDir, "*", SearchOption.AllDirectories));
                    }
                    else
                    {
                        Debug.LogWarning(
                            $"[DistributedRecorder]   [WARN] ローカルキャッシュが見つかりません: {job.LocalOutputDir}");
                        continue;
                    }

                    // Override the per-job snapshot with current _collectDir for bulk op.
                    var bulkVm = new MtrJobViewModel
                    {
                        JobId          = job.JobId,
                        TimelineName   = job.TimelineName,
                        CollectDir     = _collectDir,
                        CollectDisambig = job.CollectDisambig,
                        LocalOutputDir = job.LocalOutputDir,
                    };

                    await CopyToCollectDirAsync(bulkVm, files);
                }

                Debug.Log($"[DistributedRecorder] まとめて DL 完了 → {_collectDir}");
            }
            finally
            {
                _isBulkCollecting = false;
                Repaint();
            }
        }

        // -----------------------------------------------------------------------
        // retry-failed-collection (phase 1): bulk manual retry
        // -----------------------------------------------------------------------

        /// <summary>
        /// Pure function: counts jobs currently in <see cref="DownloadState.Failed"/>.
        /// Drives the "失敗した回収を一括再試行 [N 件]" button label/enabled-state and is
        /// re-derived every OnGUI frame (cheap: linear scan, no allocation beyond the int).
        ///
        /// Made <c>internal static</c> for hermetic EditMode tests.
        /// </summary>
        internal static int CountFailedDownloads(IReadOnlyList<MtrJobViewModel> jobs)
        {
            if (jobs == null) return 0;
            int count = 0;
            foreach (var job in jobs)
            {
                if (job != null && job.DownloadState == DownloadState.Failed)
                    count++;
            }
            return count;
        }

        private int CountFailedDownloads() => CountFailedDownloads(_dispatchedJobs);

        /// <summary>
        /// Retries every job currently in <see cref="DownloadState.Failed"/>, sequentially,
        /// re-using the same idempotent <see cref="DownloadResultsAsync"/> entry point as the
        /// per-job "再回収" button (<paramref name="isManualRetry"/>-equivalent: <c>true</c>).
        ///
        /// Guard: <see cref="_isRetryingDownloads"/> prevents concurrent invocations from
        /// rapid double-clicks, mirroring <see cref="BulkCollectAsync"/>'s
        /// <see cref="_isBulkCollecting"/> pattern.
        ///
        /// A single job's failure (e.g. NotFound / result vanished — dialog shown inside
        /// <see cref="DownloadResultsAsync"/>) does not stop the remaining jobs in the batch
        /// from being retried (acceptance criterion: "一括再試行中に一部が結果消失でも、
        /// 残りのジョブの再回収は継続する").
        /// </summary>
        private async void RetryFailedDownloadsAsync()
        {
            if (_isRetryingDownloads) return;

            // Snapshot the eligible job list synchronously on the main thread so
            // concurrent state changes (e.g. a per-job manual retry) mid-batch do not
            // mutate the collection we are iterating.
            var eligible = new List<MtrJobViewModel>();
            foreach (var job in _dispatchedJobs)
            {
                if (job.DownloadState == DownloadState.Failed)
                    eligible.Add(job);
            }

            if (eligible.Count == 0)
            {
                Debug.Log("[DistributedRecorder] 一括再試行: 対象ジョブ（回収失敗）がありません。");
                return;
            }

            _isRetryingDownloads = true;
            Repaint();

            try
            {
                Debug.Log($"[DistributedRecorder] 失敗した回収を一括再試行 開始 – {eligible.Count} 件");

                int done = 0;
                foreach (var job in eligible)
                {
                    Debug.Log(
                        $"[DistributedRecorder]   [{++done}/{eligible.Count}] " +
                        $"{job.JobId.Substring(0, Math.Min(8, job.JobId.Length))}...");

                    // job.Worker can be null only if dispatch bookkeeping is corrupted;
                    // guard defensively so one bad entry cannot abort the whole batch.
                    if (job.Worker == null)
                    {
                        Debug.LogWarning(
                            $"[DistributedRecorder]   [WARN] Worker 情報がありません。スキップ: " +
                            $"{job.JobId.Substring(0, Math.Min(8, job.JobId.Length))}");
                        continue;
                    }

                    await DownloadResultsAsync(job.Worker, job, isManualRetry: true);
                }

                Debug.Log("[DistributedRecorder] 失敗した回収を一括再試行 完了");
            }
            finally
            {
                _isRetryingDownloads = false;
                Repaint();
            }
        }

        // -----------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------

        /// <summary>
        /// Cheaply counts the number of selected timelines that have at least one
        /// enabled supported recorder item (Image or Movie), without calling
        /// <see cref="CollectRenderTargets"/> (which invokes AssetDatabase, JsonUtility,
        /// and hash computation). Safe to call every OnGUI frame.
        /// Renamed internally from CountImageTimelinesCheap to reflect Movie support.
        /// </summary>
        private int CountSupportedTimelinesCheap()
        {
            if (selectedDirectorIndices == null || selectedDirectorIndices.Count == 0)
                return 0;

            int count = 0;
            foreach (int idx in selectedDirectorIndices)
            {
                var config = GetTimelineRecorderConfig(idx);
                var enabled = config.GetEnabledRecorders();
                foreach (var item in enabled)
                {
                    if (IsSupportedRecorderItem(item))
                    {
                        count++;
                        break; // one supported item is enough to count this timeline
                    }
                }
            }
            return count;
        }

        private bool IsAnyJobActive()
        {
            foreach (var vm in _dispatchedJobs)
            {
                if (!IsTerminalState(vm.State) || vm.DownloadState == DownloadState.InProgress)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Returns <c>true</c> when <paramref name="state"/> is a terminal state.
        ///
        /// Delegates to <see cref="DistributedRecorder.Shared.JobStateExtensions.IsTerminal"/>
        /// (the single shared definition in Protocol.cs) so Master and any future
        /// consumers share identical terminal-state semantics (F8).
        ///
        /// Made <c>public static</c> for hermetic tests
        /// (see <see cref="AreAllJobsTerminal"/>).
        /// </summary>
        public static bool IsTerminalState(JobState state)
            => state.IsTerminal();

        private int CountJobsByState(JobState state)
            => CountJobsInState(_dispatchedJobs, state);

        /// <summary>
        /// Posts <paramref name="action"/> to <see cref="EditorApplication.update"/> so
        /// it executes exactly once on the next Editor main-thread tick, then removes itself.
        ///
        /// Using an <c>EditorApplication.CallbackFunction</c> variable ensures the same delegate
        /// instance is used for both <c>+=</c> and <c>-=</c>, which is required for correct
        /// self-removal.  (C# local functions generate a new delegate instance on each
        /// conversion, so <c>-= localFunctionName</c> would fail to unregister.)
        /// </summary>
        private static void EditorCallbackOnce(Action action)
        {
            EditorApplication.CallbackFunction callback = null;
            callback = () =>
            {
                EditorApplication.update -= callback;
                action();
            };
            EditorApplication.update += callback;
        }

        private static string BuildHierarchyPath(GameObject go)
        {
            if (go == null) return string.Empty;
            var parts   = new System.Text.StringBuilder();
            var current = go.transform;
            while (current != null)
            {
                if (parts.Length > 0)
                    parts.Insert(0, '/');
                parts.Insert(0, current.gameObject.name);
                current = current.parent;
            }
            return parts.ToString();
        }

        /// <summary>
        /// Extracts the first 8 characters of a hash value from a reason string.
        /// Looks for <paramref name="key"/> (e.g. "master=") and returns the 8 chars
        /// immediately following it, or "????????" if not found.
        /// Matches the same helper used in <c>DistributedRecorderWindow</c>.
        /// </summary>
        private static string ExtractHashShort(string reason, string key)
        {
            if (string.IsNullOrEmpty(reason) || string.IsNullOrEmpty(key))
                return "????????";

            int idx = reason.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return "????????";

            int start = idx + key.Length;
            if (start >= reason.Length) return "????????";

            int len = Math.Min(8, reason.Length - start);
            return reason.Substring(start, len) + "…";
        }

        /// <summary>
        /// Checks whether any recording-target scene or timeline asset has uncommitted
        /// edits (dirty working tree) and logs a warning if so.
        ///
        /// Only scene (.unity) and timeline (.playable) assets are checked.
        /// Recorder config assets (sent in-request as recorderConfigJson) are intentionally
        /// excluded – their content is transferred regardless of git state.
        ///
        /// Uses <see cref="GitInfo.TryGetDirtyPaths"/> via the project root.
        /// On non-git repos or when git is unavailable, the check is silently skipped
        /// (warning in Debug.Log only).
        ///
        /// commit-based-project-verification F5.
        /// </summary>
        private static void WarnIfTargetAssetsDirty(List<DistributedTimelineJob> targets)
        {
            if (targets == null || targets.Count == 0)
                return;

            // Collect unique scene + timeline paths (project-relative, as passed to git).
            var scopePaths = new System.Collections.Generic.List<string>();
            var seen       = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var job in targets)
            {
                if (!string.IsNullOrEmpty(job.ScenePath) && seen.Add(job.ScenePath))
                    scopePaths.Add(job.ScenePath);
                if (!string.IsNullOrEmpty(job.TimelineAssetPath) && seen.Add(job.TimelineAssetPath))
                    scopePaths.Add(job.TimelineAssetPath);
            }

            if (scopePaths.Count == 0)
                return;

            string projectRoot = DistributedRecorder.Shared.ProjectPaths.ProjectRoot;
            if (!DistributedRecorder.Shared.GitInfo.TryGetDirtyPaths(
                    projectRoot, scopePaths, out var dirtyPaths, out string gitError))
            {
                // Not a git repo or git not installed – skip silently.
                Debug.Log(
                    $"[DistributedRecorder] dirty 警告チェックをスキップ（git 取得失敗）: {gitError}");
                return;
            }

            if (dirtyPaths.Count == 0)
                return;

            string fileList = string.Join("\n  ", dirtyPaths);
            Debug.LogWarning(
                "[DistributedRecorder] 録画対象 scene / timeline に未コミット編集があります。\n" +
                "Worker には反映されません。先にコミットすることを推奨します。\n" +
                $"ダーティなファイル:\n  {fileList}");

            EditorUtility.DisplayDialog(
                "未コミット編集の警告",
                "録画対象の scene / timeline に未コミット編集があります。\n" +
                "Worker には反映されません。このまま続行しますか？\n\n" +
                $"未コミット:\n{fileList}",
                "続行");
        }

        /// <summary>
        /// Resolves the output relative path for a given recorder config item,
        /// substituting MTR wildcards that are known at dispatch time (<c>&lt;Take&gt;</c>,
        /// <c>&lt;Scene&gt;</c>, <c>&lt;Timeline&gt;</c>, etc.) while preserving
        /// <c>&lt;Frame&gt;</c> for the Recorder to resolve at capture time.
        ///
        /// The result is a relative path fragment that the Worker prepends with
        /// <c>Recordings/{jobId}/</c>.  It is guaranteed to be relative and free of
        /// ".." traversal.
        ///
        /// Made internal for hermetic testing.
        /// </summary>
        internal static string ResolveOutputRelativePath(
            MultiRecorderConfig.RecorderConfigItem item,
            string scenePath,
            PlayableDirector director)
        {
            string sceneName = string.IsNullOrEmpty(scenePath)
                ? "Scene"
                : Path.GetFileNameWithoutExtension(scenePath);

            string timelineName = (director != null && director.playableAsset != null)
                ? director.playableAsset.name
                : "Timeline";

            // Movie recordings produce a single file — do NOT preserve <Frame> wildcard.
            // Image recordings preserve <Frame> so the Recorder substitutes per-frame numbers.
            bool isMovie = item.recorderType == RecorderSettingsType.Movie;

            // Build the wildcard context.
            var ctx = new WildcardContext
            {
                SceneName              = sceneName,
                TimelineName           = timelineName,
                TakeNumber             = item.takeNumber,
                Width                  = item.width,
                Height                 = item.height,
                RecorderType           = item.recorderType,
                PreserveFrameWildcard  = !isMovie,   // false for Movie: resolve <Frame> away
                RecorderDisplayName    = item.name,
                RecorderName           = item.name
            };

            // Determine the raw path from the item's output path settings
            string rawPath;
            if (item.outputPath != null && item.outputPath.pathMode == RecorderPathMode.Custom &&
                !string.IsNullOrEmpty(item.outputPath.path))
            {
                // Custom path: use it directly (it is already relative or will be made relative below)
                rawPath = item.outputPath.path + "/" + item.fileName;
            }
            else
            {
                // UseGlobal / default: use only the fileName (the global output root is handled
                // on the Worker side by prepending Recordings/{jobId}/)
                rawPath = item.fileName;
            }

            // For Movie: strip any <Frame> wildcard that may be in the template
            // (e.g. if the user typed "frame_<Frame>" for a Movie item).
            if (isMovie)
                rawPath = rawPath.Replace("<Frame>", string.Empty).TrimEnd('_', '-', ' ');

            // Process wildcards (PreserveFrameWildcard controls <Frame> handling)
            string processed = WildcardProcessor.ProcessWildcards(rawPath, ctx);

            // Sanitize: strip leading slashes, normalize separators
            processed = processed.Replace('\\', '/').TrimStart('/');

            // Safety: reject absolute paths or ".." (should not happen after wildcard
            // processing, but defend-in-depth).
            // NOTE: for Image, `processed` may still contain the <Frame> wildcard (and
            // '<','>' are illegal path chars on Windows), so we MUST NOT call
            // System.IO.Path APIs here — Path.IsPathRooted throws "Illegal characters in
            // path". Detect an absolute/rooted path manually instead.
            bool isRooted = processed.Length > 0
                && (processed[0] == '/' || processed[0] == '\\'
                    || (processed.Length >= 2 && processed[1] == ':')
                    || processed.StartsWith("\\\\", StringComparison.Ordinal));
            if (isRooted)
            {
                Debug.LogWarning(
                    $"[DistributedRecorder] resolvedOutputRelativePath was absolute after processing; " +
                    $"falling back to safe default. Original: {rawPath}");
                string fallback = isMovie
                    ? $"{sceneName}_{timelineName}/{item.name}"
                    : $"{sceneName}_{timelineName}/{item.fileName}";
                processed = WildcardProcessor.ProcessWildcards(fallback, ctx);
                processed = processed.Replace('\\', '/').TrimStart('/');
            }

            // Check for ".." traversal components
            bool hasDotDot = false;
            foreach (string part in processed.Split('/'))
            {
                if (part == "..") { hasDotDot = true; break; }
            }
            if (hasDotDot)
            {
                Debug.LogWarning(
                    $"[DistributedRecorder] resolvedOutputRelativePath contained '..'; " +
                    $"falling back to safe default. Original: {rawPath}");
                processed = isMovie
                    ? $"{sceneName}_{item.name}_{item.takeNumber}"
                    : $"{sceneName}_{item.name}_{item.takeNumber}/<Frame>";
            }

            return processed;
        }
    }

    // -----------------------------------------------------------------------
    // Data transfer objects (internal to this file / partial)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Intermediate DTO produced by <see cref="MultiTimelineRecorder.CollectRenderTargets"/>.
    /// Carries all data needed to build a <see cref="JobRequest"/> without holding
    /// <see cref="UnityEngine.Object"/> references beyond the collection phase.
    /// </summary>
    public class DistributedTimelineJob
    {
        /// <summary>Source PlayableDirector (used only during collection; not sent over wire).</summary>
        public PlayableDirector Director;

        /// <summary>Project-relative path of the TimelineAsset.</summary>
        public string TimelineAssetPath;

        /// <summary>Name of the PlayableDirector's GameObject.</summary>
        public string DirectorObjectName;

        /// <summary>Full hierarchy path of the PlayableDirector's GameObject (e.g. "Root/Director").</summary>
        public string DirectorHierarchyPath;

        /// <summary>Normalized recorder configuration to send inside JobRequest (legacy DTO).</summary>
        public RecorderJobConfig JobConfig;

        /// <summary>Recording start time in seconds (signal-resolved or 0).</summary>
        public double StartTime;

        /// <summary>Recording end time in seconds (signal-resolved or Timeline.duration).</summary>
        public double EndTime;

        /// <summary>Active scene path at the time of collection.</summary>
        public string ScenePath;

        // --- MTR fidelity fields (mtr-distributed-integration M3) ---

        /// <summary>
        /// Full RecorderConfigItem serialized by JsonUtility.ToJson (without Camera/RT Object refs).
        /// </summary>
        public string RecorderConfigJson;

        /// <summary>Hierarchy path of the target Camera (TargetCamera source type).</summary>
        public string TargetCameraHierarchyPath;

        /// <summary>Name of the target Camera GameObject (fallback when hierarchy path is empty).</summary>
        public string TargetCameraName;

        /// <summary>AssetDatabase GUID of the RenderTexture (RenderTexture source type).</summary>
        public string RenderTextureGuid;

        /// <summary>Resolved output width after applying global/per-item resolution rules.</summary>
        public int EffectiveWidth;

        /// <summary>Resolved output height.</summary>
        public int EffectiveHeight;

        /// <summary>Resolved frame rate from MTR global settings.</summary>
        public double EffectiveFrameRate;

        /// <summary>
        /// Output relative path fragment with Take/Scene wildcards resolved; Frame preserved.
        /// </summary>
        public string ResolvedOutputRelativePath;

        /// <summary>Job-scoped hash (timeline + deps + scene).</summary>
        public string JobScopeHash;
    }

    /// <summary>
    /// State of the result download phase for a dispatched job.
    /// </summary>
    public enum DownloadState
    {
        NotStarted,
        InProgress,
        Done,
        Failed
    }

    // ── dispatch-status-labels: JobPhase enum ─────────────────────────────────
    //
    // Sub-phase within the Running lifecycle, used to provide finer-grained UI
    // labels without changing the wire-protocol JobState values.
    // Master-local only; never sent over the wire.
    // Values are ordered chronologically so comparisons against ordinal are valid.

    /// <summary>
    /// Fine-grained dispatch sub-phase for a job that is in or approaching the
    /// Running state.  Provides UI status labels without altering JobState wire values.
    ///
    /// Master-local only: never sent over the wire.
    /// Added in dispatch-status-labels.
    /// </summary>
    public enum JobPhase
    {
        /// <summary>Job is waiting in the Master-side queue (State == Queued).</summary>
        Queued,
        /// <summary>
        /// Master is sending the HTTP POST to the Worker; waiting for the ack.
        /// Transitions to <see cref="Preparing"/> on successful ack (accepted == true).
        /// </summary>
        Sending,
        /// <summary>
        /// Ack received. Worker is starting Unity / Play Mode; no frames yet.
        /// Transitions to <see cref="Recording"/> on first ProgressEvent with TotalFrames &gt; 0.
        /// </summary>
        Preparing,
        /// <summary>
        /// First frame event received; Worker is actively recording.
        /// </summary>
        Recording,
        /// <summary>
        /// Recording completed; Master is downloading result files from Worker.
        /// </summary>
        Collecting,
        /// <summary>Job has reached a terminal state (Completed / Failed / Unreachable).</summary>
        Terminal,
    }

    /// <summary>
    /// View model for a dispatched distributed job.
    /// Replaces the M5 <c>DistributedJobRecord</c> with richer progress/download state.
    /// </summary>
    public class MtrJobViewModel
    {
        /// <summary>Unique job ID (GUID, no hyphens).</summary>
        public string JobId;

        /// <summary>Human-readable Timeline (director GameObject) name for UI display.</summary>
        public string TimelineName;

        /// <summary>Human-readable Worker name for UI display.</summary>
        public string WorkerName;

        /// <summary>Worker endpoint (used by ProgressMonitor and ResultDownloader).</summary>
        public WorkerInfo Worker;

        /// <summary>Current lifecycle state reported by the Worker.</summary>
        public JobState State;

        /// <summary>Most recent frame number from the Worker's progress stream.</summary>
        public int CurrentFrame;

        /// <summary>Total expected frames from the Worker's progress stream.</summary>
        public int TotalFrames;

        /// <summary>State of the result download phase.</summary>
        public DownloadState DownloadState;

        /// <summary>Absolute local directory where downloaded files are written.</summary>
        public string LocalOutputDir;

        // ── dispatch-retry-queue fields ─────────────────────────────────────────

        /// <summary>
        /// The <see cref="JobRequest"/> to (re-)send when this job is dequeued.
        /// Set at batch-build time; retained for requeue scenarios.
        /// Only the Master uses this field; never sent over the wire.
        /// </summary>
        public JobRequest PendingRequest;

        /// <summary>
        /// Number of times this job has been handed back to the pending queue after
        /// a transient failure (WorkerBusy).  When this exceeds
        /// <see cref="MultiTimelineRecorder.MaxJobRetries"/> the job is Failed.
        /// </summary>
        public int RetryCount;

        // ── dispatch-worker-liveness fields ─────────────────────────────────────

        /// <summary>
        /// Set of Worker display names that have already been tried for this job and
        /// should not be selected again (per-job blacklist for Unreachable failover).
        ///
        /// Populated by the failover path in
        /// <see cref="MultiTimelineRecorder.DispatchOneWithOverrideAsync"/> when a
        /// dispatch attempt returns <see cref="DispatchFailReason.Unreachable"/>.
        ///
        /// Master-local only; never sent over the wire.
        /// </summary>
        public HashSet<string> TriedWorkers;

        // ── dispatch-status-labels field ────────────────────────────────────────

        /// <summary>
        /// Fine-grained dispatch sub-phase used to display human-readable status labels
        /// in the job list UI.  Updated by the Master as the job progresses through the
        /// dispatch → ack → recording → download lifecycle.
        ///
        /// Master-local only; never sent over the wire.
        /// Added in dispatch-status-labels.
        /// </summary>
        public JobPhase Phase;

        // ── collect-to-dir fields (v1.4.10) ─────────────────────────────────────

        /// <summary>
        /// Collection destination directory snapshotted at dispatch time so that
        /// mid-session UI edits to <see cref="MultiTimelineRecorder._collectDir"/>
        /// do not affect already-dispatched jobs.
        ///
        /// Empty string = collect feature disabled for this job.
        /// Master-local only; never sent over the wire.
        /// </summary>
        public string CollectDir;

        /// <summary>
        /// Short unique string (first 8 chars of <see cref="JobId"/>) used by
        /// <see cref="CollectPathValidator.BuildDestinationPath"/> to disambiguate
        /// destination sub-directory names on collision.
        /// </summary>
        public string CollectDisambig;

        // ── retry-failed-collection fields (phase 1) ────────────────────────────

        /// <summary>
        /// Classification of the most recent <see cref="DownloadResult"/> failure for
        /// this job (<see cref="DistributedRecorder.Master.DownloadFailureKind.None"/> when
        /// the last attempt succeeded or none has run yet). Drives whether the
        /// auto-retry watchdog considers this job eligible (Connection only —
        /// NotFound is a phase-2 concern) and which DisplayDialog text a manual
        /// retry shows on failure.
        /// Master-local only; never sent over the wire.
        /// </summary>
        public DistributedRecorder.Master.DownloadFailureKind LastDownloadFailureKind;

        /// <summary>
        /// Number of automatic re-collection attempts made by the periodic auto-retry
        /// watchdog for this job since it last entered <see cref="DownloadState.Failed"/>.
        /// Reset to 0 whenever a manual retry ("再回収" / bulk) is triggered, so manual
        /// retries are never blocked by the auto-retry cap.
        /// Master-local only; never sent over the wire.
        /// </summary>
        public int AutoRetryCount;

        /// <summary>
        /// <see cref="EditorApplication.timeSinceStartup"/> at the moment this job's
        /// download last transitioned to <see cref="DownloadState.Failed"/> (or the most
        /// recent auto-retry attempt). Used together with <see cref="AutoRetryCount"/> to
        /// compute the exponential-backoff due time in
        /// <see cref="MultiTimelineRecorder.ShouldAutoRetryDownload"/>.
        /// Master-local only; never sent over the wire.
        /// </summary>
        public double LastDownloadAttemptTime;
    }
}
