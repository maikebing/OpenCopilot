using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenCopilot.Http;

namespace OpenCopilot.Providers
{
    /// <summary>
    /// Provider for local Ollama instance (http://localhost:11434).
    /// Uses the native Ollama /api/chat and /api/tags endpoints.
    /// </summary>
    public class OllamaProvider : ILlmProvider, IDisposable
    {
        private HttpClient _httpClient;
        private string _baseUrl;
        private string _model;
        private string? _proxyUrl;
        private bool _disposed;

        // Ollama runs locally and may take longer than cloud APIs for large models.
        private const int RequestTimeoutSeconds = 60;
        private const int ModelDiscoveryTimeoutSeconds = 10;

        public string Name => "Ollama";

        private string[] _availableModels;
        public string[] AvailableModels => _availableModels;

        /// <param name="baseUrl">Ollama server URL (default <c>http://localhost:11434</c>).</param>
        /// <param name="model">Model tag to use.</param>
        /// <param name="proxyUrl">
        /// Optional HTTP proxy. Because Ollama is typically localhost the
        /// <see cref="HttpClientFactory"/> already sets <c>bypassOnLocal=true</c>, so
        /// passing a proxy here has no practical effect unless Ollama is on a remote host.
        /// </param>
        public OllamaProvider(string baseUrl = "http://localhost:11434", string model = "codellama",
            string? proxyUrl = null)
        {
            _baseUrl = baseUrl.TrimEnd('/');
            _model = model;
            _proxyUrl = proxyUrl;
            _availableModels = CreateFallbackModels(model);
            _httpClient = HttpClientFactory.Create(proxyUrl, TimeSpan.FromSeconds(120));
        }

        public virtual async Task<IReadOnlyList<string>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(ModelDiscoveryTimeoutSeconds));

                var response = await _httpClient.GetAsync($"{_baseUrl}/api/tags", cts.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return AvailableModels;

                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var payload = JObject.Parse(json);
                var discoveredModels = payload["models"]
                    ?.Children<JObject>()
                    .Select(item => item["name"]?.ToString())
                    .Where(modelName => !string.IsNullOrWhiteSpace(modelName))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                if (discoveredModels != null && discoveredModels.Length > 0)
                    _availableModels = discoveredModels;
            }
            catch (HttpRequestException)
            {
            }
            catch (JsonException)
            {
            }
            catch (OperationCanceledException)
            {
            }

            return AvailableModels;
        }

        public virtual async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            try
            {
                var payload = BuildPayload(request, stream: false);
                var content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(RequestTimeoutSeconds));

                var response = await _httpClient.PostAsync($"{_baseUrl}/api/chat", content, cts.Token).ConfigureAwait(false);
                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                    return LlmResponse.Failure($"HTTP {(int)response.StatusCode}: {json}");

                var obj = JObject.Parse(json);
                var text = obj["message"]?["content"]?.ToString() ?? string.Empty;
                var done = obj["done"]?.ToObject<bool>() ?? false;

                return LlmResponse.Success(text, done ? "stop" : "length");
            }
            catch (OperationCanceledException)
            {
                return LlmResponse.Failure("Request timed out or was cancelled.");
            }
            catch (Exception ex)
            {
                return LlmResponse.Failure($"Ollama error: {ex.Message}");
            }
        }

        public async Task<IAsyncEnumerable<string>> StreamCompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            return StreamInternalAsync(request, cancellationToken);
        }

        private async IAsyncEnumerable<string> StreamInternalAsync(LlmRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            HttpResponseMessage response = null;
            try
            {
                var payload = BuildPayload(request, stream: true);
                var content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

                response = await _httpClient.PostAsync($"{_baseUrl}/api/chat", content, cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    yield return $"[ERROR] HTTP {(int)response.StatusCode}: {error}";
                    yield break;
                }

                using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var reader = new System.IO.StreamReader(stream);

                while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    JObject chunk;
                    try { chunk = JObject.Parse(line); }
                    catch { continue; }

                    var delta = chunk["message"]?["content"]?.ToString();
                    if (!string.IsNullOrEmpty(delta))
                        yield return delta;

                    if (chunk["done"]?.ToObject<bool>() == true)
                        yield break;
                }
            }
            finally
            {
                response?.Dispose();
            }
        }

        public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var models = await GetAvailableModelsAsync(cancellationToken).ConfigureAwait(false);
                return models.Count > 0;
            }
            catch
            {
                return false;
            }
        }

        private object BuildPayload(LlmRequest request, bool stream)
        {
            var messages = new List<object>();

            if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
                messages.Add(new { role = "system", content = request.SystemPrompt });

            foreach (var msg in request.Messages)
                messages.Add(new { role = msg.Role, content = msg.Content });

            return new
            {
                model = request.Model ?? _model,
                messages,
                stream,
                options = new
                {
                    temperature = request.Temperature,
                    num_predict = request.MaxTokens
                }
            };
        }

        public void UpdateSettings(string baseUrl, string model, string? proxyUrl = null)
        {
            _baseUrl = baseUrl.TrimEnd('/');
            _model = model;
            _availableModels = CreateFallbackModels(model);

            if (proxyUrl != _proxyUrl)
            {
                var old = _httpClient;
                _proxyUrl = proxyUrl;
                _httpClient = HttpClientFactory.Create(proxyUrl, TimeSpan.FromSeconds(120));
                old.Dispose();
            }
        }

        private static string[] CreateFallbackModels(string model)
        {
            return string.IsNullOrWhiteSpace(model)
                ? Array.Empty<string>()
                : new[] { model };
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _httpClient.Dispose();
                _disposed = true;
            }
        }
    }
}
