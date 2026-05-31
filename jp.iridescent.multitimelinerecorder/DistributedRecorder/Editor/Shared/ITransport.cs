using System;
using System.Threading.Tasks;

namespace DistributedRecorder.Shared
{
    /// <summary>
    /// Abstraction over the network transport layer.
    ///
    /// MVP implementation: <see cref="HttpTransport"/> (HTTP/REST).
    /// Future implementations could add WebSocket-over-TLS, mTLS, etc.
    /// </summary>
    public interface ITransport : IDisposable
    {
        /// <summary>
        /// Sends an HTTP POST to <paramref name="url"/> with <paramref name="jsonBody"/>
        /// and returns the response body as a string.
        /// Throws <see cref="TransportException"/> on network or HTTP errors.
        /// </summary>
        Task<string> PostJsonAsync(string url, string jsonBody, TimeSpan timeout);

        /// <summary>
        /// Sends an HTTP GET to <paramref name="url"/> and returns the response body.
        /// </summary>
        Task<string> GetAsync(string url, TimeSpan timeout);

        /// <summary>
        /// Downloads the binary contents at <paramref name="url"/> into
        /// <paramref name="destinationPath"/>.
        /// </summary>
        Task DownloadFileAsync(string url, string destinationPath, TimeSpan timeout);
    }

    /// <summary>
    /// Thrown when a transport-level error occurs (network failure, HTTP 4xx/5xx, timeout).
    /// </summary>
    public class TransportException : Exception
    {
        public int    HttpStatusCode { get; }

        /// <summary>
        /// The raw response body returned by the server, or <c>null</c> if no body
        /// was available (e.g. network-level errors or timeouts).
        ///
        /// For HTTP 4xx/5xx responses the body typically contains a JSON payload
        /// (e.g. a <see cref="JobAck"/> with <c>accepted=false</c>) that callers
        /// can deserialise to obtain a structured rejection reason.
        /// </summary>
        public string Body          { get; }

        public TransportException(string message, int httpStatusCode = 0, string body = null)
            : base(message)
        {
            HttpStatusCode = httpStatusCode;
            Body           = body;
        }

        public TransportException(string message, Exception inner, int httpStatusCode = 0, string body = null)
            : base(message, inner)
        {
            HttpStatusCode = httpStatusCode;
            Body           = body;
        }
    }
}
