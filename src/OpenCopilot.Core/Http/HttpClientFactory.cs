using System;
using System.Net;
using System.Net.Http;

namespace OpenCopilot.Http
{
    /// <summary>
    /// Creates <see cref="HttpClient"/> instances, optionally routing traffic through an HTTP proxy.
    /// Localhost addresses are always bypassed even when a proxy is configured, so Ollama and
    /// Docker Desktop AI providers are not affected by proxy settings.
    /// </summary>
    public static class HttpClientFactory
    {
        /// <summary>
        /// Creates an <see cref="HttpClient"/> with the specified timeout.
        /// When <paramref name="proxyUrl"/> is non-empty the client routes all non-local
        /// requests through the proxy (e.g. <c>http://127.0.0.1:7890</c>).
        /// </summary>
        /// <param name="proxyUrl">
        /// Proxy URL such as <c>http://127.0.0.1:7890</c>.
        /// Pass <see langword="null"/> or an empty string to disable proxying.
        /// </param>
        /// <param name="timeout">Per-request timeout applied to the client.</param>
        public static HttpClient Create(string? proxyUrl, TimeSpan timeout)
        {
            if (string.IsNullOrWhiteSpace(proxyUrl))
            {
                return new HttpClient { Timeout = timeout };
            }

            var proxy = new WebProxy(proxyUrl, true);  // true = bypassOnLocal

            var handler = new HttpClientHandler
            {
                UseProxy = true,
                Proxy = proxy
            };

            return new HttpClient(handler) { Timeout = timeout };
        }

        /// <summary>
        /// Convenience overload — timeout defaults to 120 seconds.
        /// </summary>
        public static HttpClient Create(string? proxyUrl) =>
            Create(proxyUrl, TimeSpan.FromSeconds(120));
    }
}
