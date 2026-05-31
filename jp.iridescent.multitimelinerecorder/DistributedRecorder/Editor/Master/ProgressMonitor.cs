using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DistributedRecorder.Shared;
using UnityEngine;

namespace DistributedRecorder.Master
{
    /// <summary>
    /// Subscribes to the Worker's progress stream for a specific job and raises
    /// events on each <see cref="ProgressEvent"/> received.
    ///
    /// The Worker exposes newline-delimited JSON (NDJSON) over
    /// <c>GET /jobs/{id}/progress</c>.  This class reads that stream on a
    /// background thread and fires <see cref="OnProgress"/> callbacks.
    /// </summary>
    public class ProgressMonitor : IDisposable
    {
        /// <summary>Raised on the calling thread context for each progress event.</summary>
        public event Action<ProgressEvent> OnProgress;

        /// <summary>Raised when the stream ends or an error occurs.</summary>
        public event Action<string> OnError;

        private readonly HttpClient         _client;
        private readonly HmacAuthenticator  _auth;
        private CancellationTokenSource     _cts;
        private bool                        _disposed;

        public ProgressMonitor(HmacAuthenticator auth)
        {
            _auth   = auth ?? throw new ArgumentNullException(nameof(auth));
            _client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        }

        /// <summary>
        /// Starts streaming progress from <paramref name="workerBaseUrl"/> for
        /// <paramref name="jobId"/>.
        ///
        /// This is fire-and-forget; events are delivered via <see cref="OnProgress"/>.
        /// </summary>
        public void Start(string workerBaseUrl, string jobId)
        {
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => StreamLoopAsync(workerBaseUrl, jobId, _cts.Token), _cts.Token);
        }

        /// <summary>Stops the progress stream and releases resources.</summary>
        public void Stop()
        {
            _cts?.Cancel();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
            _client.Dispose();
        }

        // --- private ------------------------------------------------------------

        private async Task StreamLoopAsync(string baseUrl, string jobId, CancellationToken ct)
        {
            string url     = $"{baseUrl}/jobs/{jobId}/progress";
            var    headers = _auth.GenerateHeaders("GET", $"/jobs/{jobId}/progress", Array.Empty<byte>());

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-Timestamp", headers.timestamp);
            request.Headers.Add("X-Nonce",     headers.nonce);
            request.Headers.Add("X-Signature", headers.signature);

            HttpResponseMessage response;
            try
            {
                response = await _client.SendAsync(request,
                    HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                OnError?.Invoke($"Progress stream connect failed for job '{jobId}': {ex.Message}");
                return;
            }

            if (!response.IsSuccessStatusCode)
            {
                OnError?.Invoke($"Progress stream returned HTTP {(int)response.StatusCode} for job '{jobId}'.");
                return;
            }

            using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    string line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (line == null) break; // stream closed by Worker

                    line = line.Trim();
                    if (string.IsNullOrEmpty(line)) continue;

                    ProgressEvent evt;
                    try
                    {
                        evt = ProtocolSerializer.Deserialize<ProgressEvent>(line);
                    }
                    catch
                    {
                        Debug.LogWarning($"[ProgressMonitor] Could not parse progress event: {line}");
                        continue;
                    }

                    OnProgress?.Invoke(evt);

                    // Stop streaming once job reaches a terminal state.
                    if (evt.state == JobState.Completed || evt.state == JobState.Failed)
                        break;
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                OnError?.Invoke($"Progress stream error for job '{jobId}': {ex.Message}");
            }
        }
    }
}
