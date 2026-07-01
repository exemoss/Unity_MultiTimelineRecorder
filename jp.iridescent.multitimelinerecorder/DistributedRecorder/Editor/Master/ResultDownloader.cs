using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using DistributedRecorder.Shared;
using UnityEngine;

namespace DistributedRecorder.Master
{
    /// <summary>
    /// After a job completes, fetches the output files from the Worker to a
    /// local destination directory on the Master.
    ///
    /// Pull flow:
    ///   1. GET /jobs/{id}/files   → <see cref="FileListResponse"/>
    ///   2. For each entry:
    ///        GET /jobs/{id}/files/{name} → save to {destinationDir}/{name}
    ///
    /// Large files (MVP-B1: up to 8 GB) are streamed via
    /// <see cref="ITransport.DownloadFileAsync"/> which uses
    /// <c>HttpCompletionOption.ResponseHeadersRead</c> + stream copy.
    /// </summary>
    public class ResultDownloader
    {
        private readonly ITransport  _transport;
        private static readonly TimeSpan ListTimeout     = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan PerFileTimeout  = TimeSpan.FromHours(2); // 8 GB @ ~1 GB/s LAN

        public ResultDownloader(ITransport transport)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        }

        /// <summary>
        /// Downloads all output files for <paramref name="jobId"/> from
        /// <paramref name="workerBaseUrl"/> into <paramref name="destinationDir"/>.
        /// </summary>
        /// <param name="workerBaseUrl">e.g. "http://192.168.1.10:11080"</param>
        /// <param name="jobId">Job identifier.</param>
        /// <param name="destinationDir">Local directory; created if absent.</param>
        /// <param name="progress">Optional callback: (fileName, currentIndex, totalCount).</param>
        /// <returns>List of local file paths that were written.</returns>
        public async Task<DownloadResult> DownloadAsync(
            string workerBaseUrl, string jobId, string destinationDir,
            Action<string, int, int> progress = null)
        {
            Directory.CreateDirectory(destinationDir);

            // 1. Get file list
            string listUrl = $"{workerBaseUrl}/jobs/{jobId}/files";
            string listJson;
            try
            {
                listJson = await _transport.GetAsync(listUrl, ListTimeout).ConfigureAwait(false);
            }
            catch (TransportException ex)
            {
                return DownloadResult.Fail(
                    $"Failed to retrieve file list: {ex.Message}",
                    ClassifyFailure(ex));
            }

            FileListResponse fileList;
            try
            {
                fileList = ProtocolSerializer.Deserialize<FileListResponse>(listJson);
            }
            catch (Exception ex)
            {
                return DownloadResult.Fail($"Could not parse file list response: {ex.Message}");
            }

            if (fileList.files == null || fileList.files.Count == 0)
            {
                Debug.LogWarning($"[ResultDownloader] No output files found for job '{jobId}'.");
                return DownloadResult.Ok(new List<string>());
            }

            // 2. Download each file
            var downloaded = new List<string>();
            int total      = fileList.files.Count;

            for (int i = 0; i < total; i++)
            {
                var entry    = fileList.files[i];
                string name  = Path.GetFileName(entry.name); // strip any directory component
                string dest  = Path.Combine(destinationDir, name);
                string url   = $"{workerBaseUrl}/jobs/{jobId}/files/{Uri.EscapeDataString(name)}";

                progress?.Invoke(name, i + 1, total);

                try
                {
                    await _transport.DownloadFileAsync(url, dest, PerFileTimeout).ConfigureAwait(false);
                    downloaded.Add(dest);
                    Debug.Log($"[ResultDownloader] Downloaded {name} ({entry.sizeBytes:N0} bytes) → {dest}");
                }
                catch (TransportException ex)
                {
                    Debug.LogError($"[ResultDownloader] Failed to download '{name}': {ex.Message}");
                    // Continue trying remaining files (partial success is still useful).
                }
            }

            return DownloadResult.Ok(downloaded);
        }

        /// <summary>
        /// Classifies a <see cref="TransportException"/> raised while fetching the file
        /// list into a <see cref="DownloadFailureKind"/> so callers (retry-failed-collection
        /// phase 1) can decide whether a retry is worthwhile.
        ///
        /// <list type="bullet">
        ///   <item><see cref="DownloadFailureKind.NotFound"/>: HTTP 404 — the Worker no
        ///     longer knows about this job (e.g. it lost its in-memory JobStore across a
        ///     10-job restart cycle). Retrying without Worker-side recovery (phase 2)
        ///     will keep failing, so callers should not auto-retry this.</item>
        ///   <item><see cref="DownloadFailureKind.Connection"/>: everything else
        ///     (timeouts, refused connections, other non-2xx statuses). These are
        ///     transient — the Worker may simply be down for a moment (e.g. mid
        ///     restart-cycle) and a later retry is likely to succeed.</item>
        /// </list>
        ///
        /// Public (rather than internal) so hermetic EditMode tests in the
        /// DistributedRecorder.Tests.EditMode assembly can call it directly without
        /// requiring an InternalsVisibleTo declaration.
        /// </summary>
        public static DownloadFailureKind ClassifyFailure(TransportException ex)
            => ex != null && ex.HttpStatusCode == 404
                ? DownloadFailureKind.NotFound
                : DownloadFailureKind.Connection;
    }

    // ---------------------------------------------------------------------------

    /// <summary>
    /// Wire-independent classification of why a <see cref="ResultDownloader.DownloadAsync"/>
    /// call failed. Used only on the Master side (never serialized) to decide whether an
    /// automatic/manual retry can plausibly succeed.
    /// </summary>
    public enum DownloadFailureKind
    {
        /// <summary>No failure (or not yet classified) — default for successful results.</summary>
        None,

        /// <summary>
        /// Transient connection failure (timeout, refused connection, non-404 HTTP error).
        /// The Worker may recover on its own; retrying later is worthwhile.
        /// </summary>
        Connection,

        /// <summary>
        /// The Worker responded but does not know this job (HTTP 404) — its JobStore
        /// entry was lost, most likely across a restart cycle. Retrying will keep
        /// failing until Worker-side jobindex persistence (phase 2) lands.
        /// </summary>
        NotFound,
    }

    public class DownloadResult
    {
        public bool              Success;
        public string            ErrorMessage;
        public IReadOnlyList<string> Files;

        /// <summary>
        /// Classification of the failure, or <see cref="DownloadFailureKind.None"/> when
        /// <see cref="Success"/> is <c>true</c>. Populated for the file-list fetch failure
        /// path (§1); per-file download failures inside a successful list fetch are logged
        /// but do not change this classification (partial success is still <c>Success=true</c>).
        /// </summary>
        public DownloadFailureKind FailureKind;

        public static DownloadResult Ok(List<string> files)
            => new DownloadResult { Success = true, Files = files, FailureKind = DownloadFailureKind.None };

        public static DownloadResult Fail(string error, DownloadFailureKind kind = DownloadFailureKind.Connection)
            => new DownloadResult { Success = false, ErrorMessage = error, Files = new List<string>(), FailureKind = kind };
    }
}
