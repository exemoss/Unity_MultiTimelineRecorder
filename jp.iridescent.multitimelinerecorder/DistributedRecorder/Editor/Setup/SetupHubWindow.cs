using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DistributedRecorder.Shared;
using DistributedRecorder.Worker;
using UnityEditor;
using UnityEngine;

namespace DistributedRecorder.Setup
{
    /// <summary>
    /// Dashboard-style <see cref="EditorWindow"/> that consolidates all
    /// Distributed Recorder setup operations.
    ///
    /// Opened via: <c>DistributedRecorder &gt; Setup Wizard</c>
    ///
    /// Pivot v2 (iter4) — C4D Team Render style:
    /// <list type="bullet">
    ///   <item>Shared password input at the top (stored in EditorPrefs, per-machine).</item>
    ///   <item>"Worker を探す" UDP broadcast discovery (port 11081, HMAC-signed).</item>
    ///   <item>QR / encrypted-file / LAN-scan UI removed.</item>
    ///   <item>All discovery and key features disabled when password is not set.</item>
    /// </list>
    ///
    /// Security notes:
    /// EditorPrefs stores the password in the Windows HKCU registry in plain text.
    /// It is readable by any process running as the same OS user.  This is acceptable
    /// for an intranet-only deployment.  The HMAC key is derived in memory only and
    /// is never written to disk or transmitted.
    ///
    /// Domain Reload resilience:
    /// Transient UI state that matters across recompile is stored in
    /// <c>[SerializeField]</c> fields.
    /// </summary>
    public sealed class SetupHubWindow : EditorWindow
    {
        // ------------------------------------------------------------------
        // Constants
        // ------------------------------------------------------------------

        private const float HealthRefreshIntervalSeconds = 5f;

        // ------------------------------------------------------------------
        // Serialised state (Domain Reload resilient)
        // ------------------------------------------------------------------

        [SerializeField] private string _syncDestinationPath = string.Empty;
        [SerializeField] private string _statusMessage       = string.Empty;
        [SerializeField] private bool   _showSyncSection     = false;
        [SerializeField] private string _lastSyncStatus      = string.Empty;

        // ------------------------------------------------------------------
        // Transient state (re-initialised after domain reload / window open)
        // ------------------------------------------------------------------

        private IReadOnlyList<HealthCheckItem> _healthItems;
        private double                         _lastHealthRefresh;
        private Vector2                        _scrollPos;
        private bool                           _syncInProgress;
        private CancellationTokenSource        _syncCts;
        private int                            _syncProgress;
        private int                            _syncTotal;
        private bool                           _discoveryRunning;
        private CancellationTokenSource        _discoveryCts;
        private List<DiscoveredWorker>         _discoveredWorkers = new List<DiscoveredWorker>();

        // Password UI — intentionally transient (not persisted in serialised state)
        // to avoid Unity leaking the password into serialised window state on disk.
        private string _passwordInput = string.Empty;

        // ------------------------------------------------------------------
        // Menu item
        // ------------------------------------------------------------------

        [MenuItem("DistributedRecorder/Setup Wizard", false, 100)]
        public static void Open()
        {
            var window = GetWindow<SetupHubWindow>("DR Setup Hub");
            window.minSize = new Vector2(480, 520);
            window.Show();
        }

        // ------------------------------------------------------------------
        // Lifecycle
        // ------------------------------------------------------------------

        private void OnEnable()
        {
            EditorApplication.update += OnUpdate;
            // Pre-populate password field from EditorPrefs on window open.
            _passwordInput = EditorPrefs.GetString(SharedKeyLoader.PasswordPrefsKey, string.Empty);
            RefreshHealthSnapshot();
            _lastHealthRefresh = EditorApplication.timeSinceStartup;
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnUpdate;
            _syncCts?.Cancel();
            _discoveryCts?.Cancel();
        }

        private void OnUpdate()
        {
            double elapsed = EditorApplication.timeSinceStartup - _lastHealthRefresh;
            if (elapsed >= HealthRefreshIntervalSeconds)
            {
                RefreshHealthSnapshot();
                _lastHealthRefresh = EditorApplication.timeSinceStartup;
                Repaint();
            }

            if (_syncInProgress || _discoveryRunning)
                Repaint();
        }

        // ------------------------------------------------------------------
        // GUI
        // ------------------------------------------------------------------

        private void OnGUI()
        {
            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);

            DrawHeader();
            DrawPasswordSection();
            EditorGUILayout.Space(8);
            DrawHealthSection();
            EditorGUILayout.Space(8);
            DrawDiscoverySection();
            EditorGUILayout.Space(8);
            DrawSyncSection();
            EditorGUILayout.Space(8);
            DrawWorkerSection();

            if (!string.IsNullOrEmpty(_statusMessage))
            {
                EditorGUILayout.Space(8);
                EditorGUILayout.HelpBox(_statusMessage, MessageType.Info);
            }

            EditorGUILayout.EndScrollView();
        }

        // ------------------------------------------------------------------
        // Section: Header
        // ------------------------------------------------------------------

        private void DrawHeader()
        {
            EditorGUILayout.LabelField("Distributed Recorder – Setup Hub",
                EditorStyles.boldLabel);
            EditorGUILayout.LabelField("各項目の状態を確認し、必要な修正を行ってください。",
                EditorStyles.helpBox);
        }

        // ------------------------------------------------------------------
        // Section: Password
        // ------------------------------------------------------------------

        private void DrawPasswordSection()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("分散レンダリングパスワード", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.HelpBox(
                "全 PC で同じパスワードを設定してください（8〜32 文字）。\n" +
                "このパスワードは Windows レジストリ (HKCU) に平文保存されます。\n" +
                "パスワードから HMAC 鍵が導出されます（PBKDF2-SHA256 / 100,000 iter）。",
                MessageType.Info);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("パスワード:", GUILayout.Width(90));
            _passwordInput = EditorGUILayout.PasswordField(_passwordInput, GUILayout.ExpandWidth(true));
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();

            // Validate and colour the feedback
            bool hasPassword = !string.IsNullOrEmpty(_passwordInput);
            bool isValid     = PasswordKeyDeriver.IsValidPassword(_passwordInput);

            if (hasPassword && !isValid)
            {
                EditorGUILayout.HelpBox("8〜32 文字、制御文字なし", MessageType.Warning);
            }

            GUILayout.FlexibleSpace();

            if (GUILayout.Button("ランダム生成", GUILayout.Width(100)))
            {
                _passwordInput = KeyGenerator.GenerateRandomPassword();
            }

            using (new EditorGUI.DisabledGroupScope(!isValid))
            {
                if (GUILayout.Button("パスワードを保存", GUILayout.Width(120)))
                {
                    EditorPrefs.SetString(SharedKeyLoader.PasswordPrefsKey, _passwordInput);
                    _statusMessage = "パスワードを保存しました。Worker PC でも同じパスワードを設定してください。";
                    RefreshHealthSnapshot();
                }
            }

            EditorGUILayout.EndHorizontal();

            // Show saved state
            string saved = EditorPrefs.GetString(SharedKeyLoader.PasswordPrefsKey, string.Empty);
            string savedState = string.IsNullOrEmpty(saved) ? "未保存" : "保存済み（●●●●）";
            EditorGUILayout.LabelField($"保存状態: {savedState}", EditorStyles.miniLabel);

            EditorGUILayout.EndVertical();
        }

        // ------------------------------------------------------------------
        // Section: Health check
        // ------------------------------------------------------------------

        private void DrawHealthSection()
        {
            EditorGUILayout.LabelField("セットアップ健康チェック", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            if (_healthItems == null || _healthItems.Count == 0)
            {
                EditorGUILayout.LabelField("チェック中...");
            }
            else
            {
                foreach (var item in _healthItems)
                    DrawHealthRow(item);
            }

            EditorGUILayout.EndVertical();

            if (GUILayout.Button("ヘルスチェックを今すぐ更新", GUILayout.Height(22)))
            {
                RefreshHealthSnapshot();
                _ = HealthCheck.RefreshWorkerHealthAsync();
            }
        }

        private static void DrawHealthRow(HealthCheckItem item)
        {
            EditorGUILayout.BeginHorizontal();

            string icon = item.Status switch
            {
                HealthStatus.Ok            => "○",
                HealthStatus.Warning       => "△",
                HealthStatus.Error         => "×",
                HealthStatus.Checking      => "…",
                HealthStatus.NotApplicable => "-",
                _                          => "?",
            };
            Color prevColor = GUI.color;
            GUI.color = item.Status switch
            {
                HealthStatus.Ok      => new Color(0.2f, 0.8f, 0.2f),
                HealthStatus.Warning => new Color(1f, 0.8f, 0.2f),
                HealthStatus.Error   => new Color(1f, 0.3f, 0.3f),
                _                    => Color.gray,
            };
            GUILayout.Label(icon, GUILayout.Width(20));
            GUI.color = prevColor;

            EditorGUILayout.LabelField(item.Label, GUILayout.Width(180));
            EditorGUILayout.LabelField(item.Detail, EditorStyles.miniLabel);

            if (!string.IsNullOrEmpty(item.FixLabel) && item.FixAction != null)
            {
                if (GUILayout.Button(item.FixLabel, GUILayout.Width(90), GUILayout.Height(18)))
                    item.FixAction.Invoke();
            }
            else
            {
                GUILayout.Space(94);
            }

            EditorGUILayout.EndHorizontal();
        }

        // ------------------------------------------------------------------
        // Section: UDP Discovery
        // ------------------------------------------------------------------

        private void DrawDiscoverySection()
        {
            EditorGUILayout.LabelField("Worker を探す（UDP Discovery）", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            bool passwordSaved = !string.IsNullOrEmpty(
                EditorPrefs.GetString(SharedKeyLoader.PasswordPrefsKey, string.Empty));

            if (!passwordSaved)
            {
                EditorGUILayout.HelpBox(
                    "パスワードを保存してから Discovery を実行してください。",
                    MessageType.Warning);
            }

            EditorGUILayout.HelpBox(
                "UDP ブロードキャスト (port 11081) で同じパスワードを持つ Worker を検索します。\n" +
                "Worker PC では Setup Hub でパスワードを保存し、「Worker を起動」してください。\n" +
                "Windows Firewall の UDP 11081 inbound を Worker PC で許可してください。",
                MessageType.None);

            using (new EditorGUI.DisabledGroupScope(!passwordSaved))
            {
                EditorGUILayout.BeginHorizontal();

                if (_discoveryRunning)
                {
                    GUILayout.Label("検索中... (5 秒)");
                    if (GUILayout.Button("停止", GUILayout.Width(60)))
                    {
                        _discoveryCts?.Cancel();
                        _discoveryRunning = false;
                    }
                }
                else
                {
                    if (GUILayout.Button("Worker を探す", GUILayout.Height(28)))
                        StartDiscovery();
                }

                EditorGUILayout.EndHorizontal();
            }

            // Results
            if (_discoveredWorkers.Count > 0)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("発見された Worker:", EditorStyles.boldLabel);
                EditorGUILayout.HelpBox(
                    $"登録先: {WorkerRegistryAutoFactory.DefaultAssetPath}",
                    MessageType.None);
                foreach (var worker in _discoveredWorkers)
                {
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(
                        $"{worker.DisplayName}  {worker.Host}:{worker.Port}",
                        GUILayout.ExpandWidth(true));

                    // Show "登録済み" label if the worker is already registered.
                    bool alreadyRegistered = IsWorkerAlreadyRegistered(worker);
                    if (alreadyRegistered)
                    {
                        GUI.color = new Color(0.2f, 0.8f, 0.2f);
                        GUILayout.Label("登録済み", GUILayout.Width(120));
                        GUI.color = Color.white;
                    }
                    else
                    {
                        if (GUILayout.Button("登録済みワーカーに追加", GUILayout.Width(140)))
                            AddWorkerToRegistry(worker);
                    }

                    EditorGUILayout.EndHorizontal();
                }
            }
            else if (!_discoveryRunning)
            {
                EditorGUILayout.LabelField(
                    "発見された Worker はありません（「Worker を探す」を押してください）。",
                    EditorStyles.miniLabel);
            }

            EditorGUILayout.EndVertical();
        }

        // ------------------------------------------------------------------
        // Section: Project Sync
        // ------------------------------------------------------------------

        private void DrawSyncSection()
        {
            _showSyncSection = EditorGUILayout.Foldout(_showSyncSection, "プロジェクトを Worker に同期");
            if (!_showSyncSection) return;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal();
            _syncDestinationPath = EditorGUILayout.TextField("同期先 UNC パス:", _syncDestinationPath);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.HelpBox(
                "例: \\\\WORKER-PC\\Projects\\Unity_Recorder_DistRendering\n" +
                "Worker PC でフォルダ共有を有効にしてください。",
                MessageType.None);

            if (_syncInProgress)
            {
                float pct = _syncTotal > 0 ? (float)_syncProgress / _syncTotal : 0f;
                EditorGUI.ProgressBar(
                    EditorGUILayout.GetControlRect(GUILayout.Height(20)),
                    pct, $"{_syncProgress}/{_syncTotal} ファイル");

                if (GUILayout.Button("キャンセル", GUILayout.Height(24)))
                {
                    _syncCts?.Cancel();
                    _statusMessage  = "同期をキャンセルしました。";
                    _syncInProgress = false;
                }
            }
            else
            {
                if (!string.IsNullOrEmpty(_lastSyncStatus))
                    EditorGUILayout.HelpBox(_lastSyncStatus, MessageType.Info);

                if (GUILayout.Button("同期を開始", GUILayout.Height(28)))
                    StartSync();
            }

            EditorGUILayout.EndVertical();
        }

        // ------------------------------------------------------------------
        // Section: Worker Start/Stop
        // ------------------------------------------------------------------

        private void DrawWorkerSection()
        {
            EditorGUILayout.LabelField("Worker の起動 / 停止", EditorStyles.boldLabel);

            bool passwordSaved = !string.IsNullOrEmpty(
                EditorPrefs.GetString(SharedKeyLoader.PasswordPrefsKey, string.Empty));

            using (new EditorGUI.DisabledGroupScope(!passwordSaved))
            {
                EditorGUILayout.BeginHorizontal();

                if (GUILayout.Button("Worker を起動 (Debug)", GUILayout.Height(28)))
                    Bootstrap.Run();

                if (GUILayout.Button("Worker を停止 (Debug)", GUILayout.Height(28)))
                    Bootstrap.StopWorker();

                EditorGUILayout.EndHorizontal();
            }

            if (!passwordSaved)
            {
                EditorGUILayout.HelpBox(
                    "パスワードを保存してから Worker を起動してください。",
                    MessageType.Warning);
            }

            EditorGUILayout.HelpBox(
                "DistributedRecorder > Start/Stop Worker (Debug) と同等の操作です。",
                MessageType.None);

            if (GUILayout.Button("ジョブ投入 Window を開く", GUILayout.Height(22)))
                EditorApplication.ExecuteMenuItem("Window/Distributed Recorder");

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Recorder ジョブ", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "E2E 検証用のサンプルを生成します。\n" +
                "Step 1: サンプルシーンを作成（カメラが回る 30 フレームの軽量シーン）\n" +
                "Step 2: サンプルジョブを作成（PNG 連番 / 30 フレーム / 1280×720 / GameView）\n" +
                "Step 3: ジョブ投入 Window でシーンパスを設定してジョブを送信",
                MessageType.None);
            if (GUILayout.Button("Step 1: サンプルシーンを作成", GUILayout.Height(26)))
                SampleSceneFactory.CreateSampleSceneFromMenu();
            if (GUILayout.Button("Step 2: サンプル Recorder ジョブを作成", GUILayout.Height(26)))
                SampleRecorderJobFactory.CreateSampleRecorderJobFromMenu();
        }

        // ------------------------------------------------------------------
        // Operations
        // ------------------------------------------------------------------

        private void StartDiscovery()
        {
            string saved = EditorPrefs.GetString(SharedKeyLoader.PasswordPrefsKey, string.Empty);
            if (string.IsNullOrEmpty(saved))
            {
                _statusMessage = "パスワードが未設定です。";
                return;
            }

            _discoveryCts     = new CancellationTokenSource();
            _discoveryRunning = true;
            _discoveredWorkers.Clear();

            byte[] hmacKey;
            try
            {
                hmacKey = PasswordKeyDeriver.DeriveKey(saved);
            }
            catch (Exception ex)
            {
                _statusMessage    = $"パスワードエラー: {ex.Message}";
                _discoveryRunning = false;
                return;
            }

            var token = _discoveryCts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    var results = await UdpDiscovery.BroadcastAsync(hmacKey, token);

                    EditorApplication.delayCall += () =>
                    {
                        _discoveryRunning  = false;
                        _discoveredWorkers = new List<DiscoveredWorker>(results);
                        _statusMessage     = results.Count == 0
                            ? "Worker が見つかりませんでした。Worker PC でパスワードを設定して Worker を起動してください。"
                            : $"{results.Count} 件の Worker が見つかりました。";
                        Repaint();
                    };
                }
                catch (OperationCanceledException)
                {
                    EditorApplication.delayCall += () =>
                    {
                        _discoveryRunning = false;
                        _statusMessage    = "Discovery をキャンセルしました。";
                        Repaint();
                    };
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                    EditorApplication.delayCall += () =>
                    {
                        _discoveryRunning = false;
                        _statusMessage    = $"Discovery エラー: {ex.Message}";
                        Repaint();
                    };
                }
            }, token);
        }

        private void StartSync()
        {
            if (string.IsNullOrWhiteSpace(_syncDestinationPath))
            {
                _statusMessage = "同期先パスを入力してください。";
                return;
            }

            _syncCts        = new CancellationTokenSource();
            _syncInProgress = true;
            _syncProgress   = 0;
            _syncTotal      = 1;
            _lastSyncStatus = string.Empty;

            string srcRoot = ProjectPaths.ProjectRoot;
            string dstRoot = _syncDestinationPath;
            var token      = _syncCts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await ProjectSyncer.SyncAsync(
                        srcRoot, dstRoot,
                        (processed, total, file) =>
                        {
                            _syncProgress = processed;
                            _syncTotal    = total;
                        },
                        token);

                    EditorApplication.delayCall += () =>
                    {
                        _syncInProgress = false;
                        _lastSyncStatus = result.Success
                            ? $"同期完了。コピー: {result.CopiedFiles} / スキップ: {result.SkippedFiles}"
                            : $"同期失敗: {result.ErrorMessage}";
                        _statusMessage  = _lastSyncStatus;
                        Repaint();
                    };
                }
                catch (OperationCanceledException)
                {
                    EditorApplication.delayCall += () =>
                    {
                        _syncInProgress = false;
                        _statusMessage  = "同期がキャンセルされました。";
                        Repaint();
                    };
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                    EditorApplication.delayCall += () =>
                    {
                        _syncInProgress = false;
                        _statusMessage  = $"同期エラー: {ex.Message}";
                        Repaint();
                    };
                }
            }, token);
        }

        private static bool IsWorkerAlreadyRegistered(DiscoveredWorker worker)
        {
            var registry = AssetDatabase.LoadAssetAtPath<WorkerRegistryAsset>(
                WorkerRegistryAutoFactory.DefaultAssetPath);
            if (registry == null) return false;
            return registry.workers.Exists(w =>
                w != null &&
                string.Equals(w.host, worker.Host, StringComparison.OrdinalIgnoreCase) &&
                w.port == worker.Port);
        }

        private void AddWorkerToRegistry(DiscoveredWorker worker)
        {
            var registry = WorkerRegistryAutoFactory.EnsureExists();
            // Avoid duplicates
            bool exists = registry.workers.Exists(w =>
                w != null &&
                string.Equals(w.host, worker.Host, StringComparison.OrdinalIgnoreCase) &&
                w.port == worker.Port);

            if (!exists)
            {
                registry.workers.Add(new WorkerInfo
                {
                    displayName = worker.DisplayName,
                    host        = worker.Host,
                    port        = worker.Port,
                    enabled     = true,
                });
                EditorUtility.SetDirty(registry);
                AssetDatabase.SaveAssets();

                _statusMessage =
                    $"{worker.DisplayName} ({worker.Host}:{worker.Port}) を" +
                    $"登録済みワーカーに追加しました。\n" +
                    $"保存先: {WorkerRegistryAutoFactory.DefaultAssetPath}";
                Debug.Log($"[SetupHub] ワーカーを登録しました: {worker.DisplayName} " +
                          $"({worker.Host}:{worker.Port}) → {WorkerRegistryAutoFactory.DefaultAssetPath}");
            }
            else
            {
                _statusMessage =
                    $"{worker.DisplayName} ({worker.Host}:{worker.Port}) は" +
                    "既に登録済みです。";
            }
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private void RefreshHealthSnapshot()
        {
            _healthItems = HealthCheck.GetSnapshot();
        }
    }
}
