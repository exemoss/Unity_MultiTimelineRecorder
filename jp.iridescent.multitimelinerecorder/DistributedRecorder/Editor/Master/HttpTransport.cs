using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DistributedRecorder.Shared;

namespace DistributedRecorder.Master
{
    /// <summary>
    /// HTTP transport implementation backed by <see cref="System.Net.Http.HttpClient"/>.
    /// Implements <see cref="ITransport"/> for use by <see cref="JobDispatcher"/>
    /// and <see cref="ResultDownloader"/>.
    ///
    /// This class manages a single shared <c>HttpClient</c> instance; callers
    /// should reuse one <see cref="HttpTransport"/> rather than creating many.
    /// </summary>
    public class HttpTransport : ITransport
    {
        private readonly HttpClient           _client;
        private readonly HmacAuthenticator    _auth;
        private bool                          _disposed;

        public HttpTransport(HmacAuthenticator auth)
        {
            _auth   = auth ?? throw new ArgumentNullException(nameof(auth));
            _client = new HttpClient();
        }

        // --- ITransport ---------------------------------------------------------

        public async Task<string> PostJsonAsync(string url, string jsonBody, TimeSpan timeout)
        {
            ThrowIfDisposed();

            byte[] bodyBytes = Encoding.UTF8.GetBytes(jsonBody);
            var    headers   = _auth.GenerateHeaders("POST", ExtractPath(url), bodyBytes);

            using var cts     = new CancellationTokenSource(timeout);
            using var content = new ByteArrayContent(bodyBytes);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            request.Headers.Add("X-Timestamp", headers.timestamp);
            request.Headers.Add("X-Nonce",     headers.nonce);
            request.Headers.Add("X-Signature", headers.signature);

            HttpResponseMessage response;
            try
            {
                response = await _client.SendAsync(request, cts.Token).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                throw new TransportException($"POST {url} timed out after {timeout.TotalSeconds}s.");
            }
            catch (HttpRequestException ex)
            {
                throw new TransportException($"POST {url} failed: {ex.Message}", ex);
            }

            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new TransportException(
                    $"POST {url} returned HTTP {(int)response.StatusCode}: {body}",
                    httpStatusCode: (int)response.StatusCode,
                    body: body);
            }

            return body;
        }

        public async Task<string> GetAsync(string url, TimeSpan timeout)
        {
            ThrowIfDisposed();

            byte[] emptyBody = Array.Empty<byte>();
            var    headers   = _auth.GenerateHeaders("GET", ExtractPath(url), emptyBody);

            using var cts     = new CancellationTokenSource(timeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-Timestamp", headers.timestamp);
            request.Headers.Add("X-Nonce",     headers.nonce);
            request.Headers.Add("X-Signature", headers.signature);

            HttpResponseMessage response;
            try
            {
                response = await _client.SendAsync(request, cts.Token).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                throw new TransportException($"GET {url} timed out after {timeout.TotalSeconds}s.");
            }
            catch (HttpRequestException ex)
            {
                throw new TransportException($"GET {url} failed: {ex.Message}", ex);
            }

            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new TransportException(
                    $"GET {url} returned HTTP {(int)response.StatusCode}: {body}",
                    httpStatusCode: (int)response.StatusCode,
                    body: body);
            }

            return body;
        }

        public async Task DownloadFileAsync(string url, string destinationPath, TimeSpan timeout)
        {
            ThrowIfDisposed();

            byte[] emptyBody = Array.Empty<byte>();
            var    headers   = _auth.GenerateHeaders("GET", ExtractPath(url), emptyBody);

            using var cts     = new CancellationTokenSource(timeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-Timestamp", headers.timestamp);
            request.Headers.Add("X-Nonce",     headers.nonce);
            request.Headers.Add("X-Signature", headers.signature);

            HttpResponseMessage response;
            try
            {
                response = await _client.SendAsync(request,
                    HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                throw new TransportException($"GET {url} timed out after {timeout.TotalSeconds}s.");
            }
            catch (HttpRequestException ex)
            {
                throw new TransportException($"GET {url} failed: {ex.Message}", ex);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new TransportException(
                    $"GET {url} returned HTTP {(int)response.StatusCode}",
                    (int)response.StatusCode);
            }

            string dir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            using var fileStream   = File.Create(destinationPath);
            using var remoteStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            await remoteStream.CopyToAsync(fileStream, 81920, cts.Token).ConfigureAwait(false);
        }

        // --- dispose ------------------------------------------------------------

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _client.Dispose();
        }

        // --- helpers ------------------------------------------------------------

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(HttpTransport));
        }

        /// <summary>Extracts the path+query portion of a URL for HMAC signing.</summary>
        private static string ExtractPath(string url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return uri.PathAndQuery;
            return url; // fallback: treat entire string as path
        }
    }
}
