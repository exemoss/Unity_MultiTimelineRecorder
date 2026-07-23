using System;
using System.Collections.Generic;
using DistributedRecorder.Master;
using DistributedRecorder.Shared;
using UnityEditor;
using UnityEngine;

namespace Unity.MultiTimelineRecorder
{
    /// <summary>
    /// lightweight-master-manifest: export/import of job manifests so a low-spec "lightweight
    /// master" (MTR package only, no content project) can dispatch jobs that were prepared on
    /// a full (heavyweight) project (plan.md 案A).
    ///
    /// Appended as a partial class — mirrors the existing convention in
    /// <c>MultiTimelineRecorder_Distributed.cs</c> — so the MTR core file is not touched by
    /// this feature beyond the two call-sites edited there (manifest section hook in
    /// <c>DrawDistributedSection</c>, and the <c>manifestCtx</c> parameter threaded through
    /// <c>StartDistributedRecordingInternalAsync</c>).
    /// </summary>
    public partial class MultiTimelineRecorder
    {
        /// <summary>
        /// Signals that <see cref="StartDistributedRecordingInternalAsync"/> should run in
        /// "lightweight master" mode for this batch: skip scene-open (F10), dirty-warn (F5),
        /// and the sync-before-dispatch git gate — all three assume a full content project
        /// that a lightweight master does not have — and inject <see cref="SourceGitCommit"/>
        /// as a <c>JobDispatcher</c> commit override instead of computing this project's own
        /// local HEAD (plan.md 論点4 機構A).
        /// </summary>
        internal sealed class ManifestDispatchContext
        {
            /// <summary>
            /// HEAD commit SHA of the content repository at export time (may be empty for
            /// non-git exports — plan.md Q3/E6). Injected verbatim into every
            /// <see cref="JobRequest.gitCommit"/> in this batch via the <c>JobDispatcher</c>
            /// commit override.
            /// </summary>
            public string SourceGitCommit = string.Empty;
        }

        // -----------------------------------------------------------------------
        // UI
        // -----------------------------------------------------------------------

        /// <summary>
        /// Draws the "job manifest" sub-section: export the current Timeline selection to a
        /// <c>.mtrjob.json</c> file, or import one to dispatch on a lightweight master.
        /// Called from <see cref="DrawDistributedSection"/>.
        /// </summary>
        private void DrawManifestSection()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("ジョブマニフェスト（低スペックマスター用）", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "ハイスペック機でジョブ定義を .mtrjob.json にエクスポートし、" +
                "MTR パッケージのみの軽量プロジェクトで読み込んでディスパッチできます。\n" +
                "読み込んだジョブは Worker が sourceGitCommit と自機 HEAD を照合します。",
                MessageType.None);

            EditorGUILayout.BeginHorizontal();

            int exportableCount = CountSupportedTimelinesCheap();
            using (new EditorGUI.DisabledScope(exportableCount == 0))
            {
                if (GUILayout.Button("マニフェストへエクスポート", GUILayout.Height(22)))
                    OnExportManifestClicked();
            }

            if (GUILayout.Button("マニフェストから読み込み", GUILayout.Height(22)))
                OnImportManifestClicked();

            EditorGUILayout.EndHorizontal();
        }

        // -----------------------------------------------------------------------
        // Export
        // -----------------------------------------------------------------------

        /// <summary>
        /// Export button handler: collects the current Timeline selection exactly as the
        /// normal "分散実行" path does (<see cref="CollectRenderTargets"/>), maps each job to
        /// a <see cref="JobManifestEntry"/>, and writes a <see cref="JobManifest"/> to a
        /// user-chosen file (plan.md Q4: file dialog, arbitrary path, ".mtrjob.json").
        /// </summary>
        private void OnExportManifestClicked()
        {
            List<DistributedTimelineJob> targets;
            try
            {
                targets = CollectRenderTargets();
            }
            catch (Exception ex)
            {
                MultiTimelineRecorderLogger.LogError(
                    $"[DistributedRecorder] マニフェストエクスポートの準備に失敗しました: {ex}");
                EditorUtility.DisplayDialog(
                    "エクスポートエラー",
                    $"対象ジョブの収集中にエラーが発生しました:\n{ex.Message}",
                    "OK");
                return;
            }

            if (targets.Count == 0)
            {
                EditorUtility.DisplayDialog(
                    "マニフェストエクスポート",
                    "エクスポート対象のジョブがありません。\n" +
                    "Timeline を選択し、対応する Recorder（Image Sequence / Movie）を有効にしてください。",
                    "OK");
                return;
            }

            string defaultName = "Manifest_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".mtrjob.json";
            string path = EditorUtility.SaveFilePanel(
                "ジョブマニフェストをエクスポート", string.Empty, defaultName, "json");
            if (string.IsNullOrEmpty(path))
                return; // user cancelled

            JobManifest manifest = BuildManifest(targets);

            if (!JobManifestIO.TrySave(path, manifest, out string saveError))
            {
                Debug.LogError($"[DistributedRecorder] マニフェストの書き出しに失敗しました: {saveError}");
                EditorUtility.DisplayDialog("エクスポートエラー", saveError, "OK");
                return;
            }

            Debug.Log($"[DistributedRecorder] マニフェストを書き出しました: {path} ({targets.Count} ジョブ)");
            EditorUtility.DisplayDialog(
                "エクスポート完了", $"{targets.Count} 件のジョブをエクスポートしました:\n{path}", "OK");
        }

        /// <summary>
        /// Builds the manifest header (schema version, tool/version metadata, git provenance)
        /// and per-job entries for <paramref name="targets"/>.
        /// </summary>
        private static JobManifest BuildManifest(List<DistributedTimelineJob> targets)
        {
            string projectRoot = ProjectPaths.ProjectRoot;

            string sourceGitCommit       = string.Empty;
            string sourceGitBranch       = string.Empty;
            bool   sourceGitCommitPushed = false;

            if (GitInfo.TryGetHeadCommit(projectRoot, out string headCommit, out _))
            {
                sourceGitCommit = headCommit;

                if (GitInfo.TryGetCurrentBranch(projectRoot, out string branch, out _))
                {
                    sourceGitBranch = branch;

                    // Best-effort "already pushed" detection (plan.md E4): ahead-count == 0
                    // means HEAD is reachable from origin/<branch> (a Worker can fetch it).
                    // TryGetAheadCount fails safe (returns false) when there is no upstream
                    // ref configured — sourceGitCommitPushed then stays false (unknown), which
                    // is fine: this flag is informational only and never blocks export.
                    if (GitInfo.TryGetAheadCount(projectRoot, branch, out int aheadCount, out _))
                        sourceGitCommitPushed = aheadCount == 0;
                }
            }
            else
            {
                // plan.md Q3: non-git projects may still export. sourceGitCommit stays empty,
                // so the lightweight master's dispatch will carry an empty gitCommit override —
                // the Worker then has nothing to compare against (E6: "sourceGitCommit 空＝照合なし").
                Debug.LogWarning(
                    "[DistributedRecorder] マニフェストのエクスポート元は git リポジトリではありません。" +
                    "sourceGitCommit は空になり、Worker 側でのコミット照合は行われません。");
            }

            var manifest = new JobManifest
            {
                schemaVersion         = JobManifestIO.CurrentSchemaVersion,
                generatorToolVersion  = ResolveMtrPackageVersion(),
                generatedAtUtc        = DateTime.UtcNow.ToString("o"),
                sourceGitCommit       = sourceGitCommit,
                sourceGitBranch       = sourceGitBranch,
                sourceGitCommitPushed = sourceGitCommitPushed,
                sourceUnityVersion    = VersionChecker.UnityVersion,
                sourceRecorderVersion = VersionChecker.RecorderVersion,
            };

            foreach (var job in targets)
                manifest.jobs.Add(ToManifestEntry(job));

            return manifest;
        }

        /// <summary>
        /// Resolves this MTR package's own version (package.json "version") via the Package
        /// Manager's synchronous assembly lookup — no async <c>Client.List</c> polling needed.
        /// Returns empty string if the assembly is not resolved to a UPM package (e.g. loose
        /// Assets-folder install, which MTR does not use, but defend defensively anyway).
        /// </summary>
        private static string ResolveMtrPackageVersion()
        {
            var pkgInfo = UnityEditor.PackageManager.PackageInfo.FindForAssembly(
                typeof(MultiTimelineRecorder).Assembly);
            return pkgInfo != null ? pkgInfo.version : string.Empty;
        }

        // -----------------------------------------------------------------------
        // Import
        // -----------------------------------------------------------------------

        /// <summary>
        /// Import button handler: loads + validates a manifest file, maps its entries back to
        /// <see cref="DistributedTimelineJob"/> (with <c>Director = null</c> — see
        /// <see cref="FromManifestEntry"/>), and — after user confirmation — dispatches them
        /// via the existing pipeline in lightweight-master mode.
        /// </summary>
        private void OnImportManifestClicked()
        {
            string path = EditorUtility.OpenFilePanel("ジョブマニフェストを読み込み", string.Empty, "json");
            if (string.IsNullOrEmpty(path))
                return; // user cancelled

            if (!JobManifestIO.TryLoad(
                    path, out JobManifest manifest, out List<string> jobWarnings, out string loadError))
            {
                // plan.md E1/E2: never fail silently — always surface a dialog.
                Debug.LogError($"[DistributedRecorder] マニフェストの読み込みに失敗しました: {loadError}");
                EditorUtility.DisplayDialog("インポートエラー", loadError, "OK");
                return;
            }

            if (jobWarnings.Count > 0)
            {
                Debug.LogWarning(
                    "[DistributedRecorder] マニフェストの一部ジョブが無効なため除外されました:\n" +
                    string.Join("\n", jobWarnings));
            }

            if (string.IsNullOrEmpty(manifest.sourceGitCommit))
            {
                // plan.md Q3/E6: allowed, but the user must know commit verification is off.
                Debug.LogWarning(
                    "[DistributedRecorder] このマニフェストには sourceGitCommit が含まれていません" +
                    "（非 git プロジェクトからのエクスポート）。Worker 側でのコミット照合は行われません。");
            }
            else if (!manifest.sourceGitCommitPushed)
            {
                // plan.md E4: informational only — does not block import/dispatch.
                Debug.LogWarning(
                    $"[DistributedRecorder] マニフェストの sourceGitCommit ({manifest.sourceGitCommit}) は " +
                    "エクスポート時に未 push だった可能性があります。" +
                    "Worker が fetch できない場合はコミット不一致になります。");
            }

            if (manifest.jobs.Count == 0)
            {
                string emptyMsg = jobWarnings.Count > 0
                    ? $"読み込み可能なジョブが 0 件でした（{jobWarnings.Count} 件は検証エラーで除外されました）。"
                    : "マニフェストにジョブが含まれていません（0 件）。";
                // plan.md B1: load succeeds, dispatch is simply a no-op — surfaced here so the
                // user is not left wondering why nothing happened.
                EditorUtility.DisplayDialog("マニフェスト読み込み", emptyMsg, "OK");
                return;
            }

            var targets = new List<DistributedTimelineJob>(manifest.jobs.Count);
            foreach (var entry in manifest.jobs)
                targets.Add(FromManifestEntry(entry));

            string summary = jobWarnings.Count > 0
                ? $"{targets.Count} 件のジョブを読み込みました（{jobWarnings.Count} 件は検証エラーで除外）。\n分散処理を開始しますか？"
                : $"{targets.Count} 件のジョブを読み込みました。\n分散処理を開始しますか？";

            bool proceed = EditorUtility.DisplayDialog(
                "マニフェスト読み込み完了", summary, "分散処理を開始", "後で");
            if (!proceed)
                return;

            var manifestCtx = new ManifestDispatchContext { SourceGitCommit = manifest.sourceGitCommit };
            StartDistributedRecordingAsync(targets, manifestCtx);
        }

        // -----------------------------------------------------------------------
        // Mapper (pure functions — internal for hermetic EditMode tests)
        // -----------------------------------------------------------------------

        /// <summary>
        /// Maps a <see cref="DistributedTimelineJob"/> (in-memory, produced by
        /// <see cref="CollectRenderTargets"/>) to its on-disk <see cref="JobManifestEntry"/>
        /// equivalent. The non-serializable <see cref="DistributedTimelineJob.Director"/>
        /// reference is intentionally not carried across — see research.md 調査事実1: the
        /// dispatch pipeline never reads it once collection has finished.
        /// </summary>
        internal static JobManifestEntry ToManifestEntry(DistributedTimelineJob job)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));

            return new JobManifestEntry
            {
                TimelineAssetPath          = job.TimelineAssetPath,
                DirectorObjectName         = job.DirectorObjectName,
                DirectorHierarchyPath      = job.DirectorHierarchyPath,
                JobConfig                  = job.JobConfig,
                StartTime                  = job.StartTime,
                EndTime                    = job.EndTime,
                ScenePath                  = job.ScenePath,
                RecorderConfigJson         = job.RecorderConfigJson,
                TargetCameraHierarchyPath  = job.TargetCameraHierarchyPath,
                TargetCameraName           = job.TargetCameraName,
                RenderTextureGuid          = job.RenderTextureGuid,
                EffectiveWidth             = job.EffectiveWidth,
                EffectiveHeight            = job.EffectiveHeight,
                EffectiveFrameRate         = job.EffectiveFrameRate,
                ResolvedOutputRelativePath = job.ResolvedOutputRelativePath,
                JobScopeHash               = job.JobScopeHash,
            };
        }

        /// <summary>
        /// Reconstructs a <see cref="DistributedTimelineJob"/> from an imported
        /// <see cref="JobManifestEntry"/>. <see cref="DistributedTimelineJob.Director"/> is
        /// always null — the existing dispatch pipeline
        /// (<see cref="StartDistributedRecordingInternalAsync"/>) never dereferences it once
        /// jobs have been collected (research.md 調査事実1), so this is safe.
        /// </summary>
        internal static DistributedTimelineJob FromManifestEntry(JobManifestEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));

            return new DistributedTimelineJob
            {
                Director                   = null,
                TimelineAssetPath          = entry.TimelineAssetPath,
                DirectorObjectName         = entry.DirectorObjectName,
                DirectorHierarchyPath      = entry.DirectorHierarchyPath,
                JobConfig                  = entry.JobConfig,
                StartTime                  = entry.StartTime,
                EndTime                    = entry.EndTime,
                ScenePath                  = entry.ScenePath,
                RecorderConfigJson         = entry.RecorderConfigJson,
                TargetCameraHierarchyPath  = entry.TargetCameraHierarchyPath,
                TargetCameraName           = entry.TargetCameraName,
                RenderTextureGuid          = entry.RenderTextureGuid,
                EffectiveWidth             = entry.EffectiveWidth,
                EffectiveHeight            = entry.EffectiveHeight,
                EffectiveFrameRate         = entry.EffectiveFrameRate,
                ResolvedOutputRelativePath = entry.ResolvedOutputRelativePath,
                JobScopeHash               = entry.JobScopeHash,
            };
        }
    }
}
