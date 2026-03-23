using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenCopilot.Http;

namespace OpenCopilot.Providers
{
    /// <summary>
    /// Provider for OpenAI and any OpenAI-compatible API (base URL is configurable).
    /// </summary>
    public class OpenAIProvider : ILlmProvider, IDisposable
    {
        private HttpClient _httpClient;
        private string _baseUrl;
        private string _apiKey;
        private string _model;
        private string? _proxyUrl;
        private string[] _availableModels;
        private bool _disposed;

        // Per-request cancellation timeout for non-streaming calls.
        private const int RequestTimeoutSeconds = 30;

        public virtual string Name => "OpenAI";

        public virtual string[] AvailableModels => _availableModels;

        public OpenAIProvider(string apiKey, string model = "gpt-4o-mini",
            string baseUrl = "https://api.openai.com/v1", string? proxyUrl = null)
        {
            _apiKey = apiKey;
            _model = model;
            _baseUrl = baseUrl.TrimEnd('/');
            _proxyUrl = proxyUrl;
            _availableModels = CreateFallbackModels(model);
            _httpClient = HttpClientFactory.Create(proxyUrl, TimeSpan.FromSeconds(120));
        }

        public virtual async Task<IReadOnlyList<string>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(10));

                using var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/models");
                SetAuthHeader(request);

                using var response = await _httpClient.SendAsync(request, cts.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return AvailableModels;

                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var payload = JObject.Parse(json);
                var discoveredModels = NormalizeModelCatalog(
                    payload["data"]
                        ?.Children<JObject>()
                        .Select(item => item["id"]?.ToString())
                    ?? Enumerable.Empty<string?>());

                if (discoveredModels.Length > 0)
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

                using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/chat/completions");
                httpRequest.Content = content;
                SetAuthHeader(httpRequest);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(RequestTimeoutSeconds));

                var response = await _httpClient.SendAsync(httpRequest, cts.Token).ConfigureAwait(false);
                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                    return LlmResponse.Failure($"HTTP {(int)response.StatusCode}: {json}");

                var obj = JObject.Parse(json);
                var text = obj["choices"]?[0]?["message"]?["content"]?.ToString() ?? string.Empty;
                var finish = obj["choices"]?[0]?["finish_reason"]?.ToString() ?? "stop";
                var promptTokens = obj["usage"]?["prompt_tokens"]?.ToObject<int>() ?? 0;
                var completionTokens = obj["usage"]?["completion_tokens"]?.ToObject<int>() ?? 0;

                return LlmResponse.Success(text, finish, promptTokens, completionTokens);
            }
            catch (OperationCanceledException)
            {
                return LlmResponse.Failure("Request timed out or was cancelled.");
            }
            catch (Exception ex)
            {
                return LlmResponse.Failure($"Error: {ex.Message}");
            }
        }

        public async Task<IAsyncEnumerable<string>> StreamCompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            var streamRequest = new LlmRequest
            {
                Model = request.Model ?? _model,
                Messages = request.Messages,
                Temperature = request.Temperature,
                MaxTokens = request.MaxTokens,
                Stream = true,
                SystemPrompt = request.SystemPrompt
            };

            return StreamInternalAsync(streamRequest, cancellationToken);
        }

        private async IAsyncEnumerable<string> StreamInternalAsync(LlmRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            HttpResponseMessage response = null;
            try
            {
                var payload = BuildPayload(request, stream: true);
                var content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

                using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/chat/completions");
                httpRequest.Content = content;
                SetAuthHeader(httpRequest);

                response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

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
                    if (!line.StartsWith("data: ")) continue;

                    var data = line.Substring(6);
                    if (data == "[DONE]") yield break;

                    JObject chunk;
                    try { chunk = JObject.Parse(data); }
                    catch { continue; }

                    var delta = chunk["choices"]?[0]?["delta"]?["content"]?.ToString();
                    if (delta != null)
                        yield return delta;
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
                var request = new LlmRequest
                {
                    Model = _model,
                    Messages = new List<LlmMessage> { LlmMessage.User("Hi") },
                    MaxTokens = 5
                };
                var result = await CompleteAsync(request, cancellationToken).ConfigureAwait(false);
                return result.IsSuccess;
            }
            catch
            {
                return false;
            }
        }

        protected virtual object BuildPayload(LlmRequest request, bool stream)
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
                temperature = request.Temperature,
                max_tokens = request.MaxTokens,
                stream
            };
        }

        protected void SetAuthHeader(HttpRequestMessage request)
        {
            if (!string.IsNullOrWhiteSpace(_apiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        }

        protected string[] NormalizeModelCatalog(IEnumerable<string?> modelNames)
        {
            if (modelNames == null)
                throw new ArgumentNullException(nameof(modelNames));

            return modelNames
                .Where(modelName => !string.IsNullOrWhiteSpace(modelName))
                .Select(modelName => modelName!.Trim())
                .Where(IsChatCompatibleModel)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        /// <summary>
        /// Updates provider settings at runtime.
        /// When <paramref name="proxyUrl"/> changes the internal <see cref="HttpClient"/> is
        /// replaced so the new proxy takes effect immediately without restarting Visual Studio.
        /// </summary>
        public void UpdateSettings(string apiKey, string model, string baseUrl, string? proxyUrl = null)
        {
            _apiKey = apiKey;
            _model = model;
            _baseUrl = baseUrl.TrimEnd('/');
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

        private static bool IsChatCompatibleModel(string modelName)
        {
            var normalized = modelName.Trim();
            if (normalized.Length == 0)
                return false;

            var lowered = normalized.ToLowerInvariant();
            return !lowered.StartsWith("text-embedding", StringComparison.Ordinal)
                && !lowered.StartsWith("embedding-", StringComparison.Ordinal)
                && !lowered.StartsWith("omni-moderation", StringComparison.Ordinal)
                && !lowered.StartsWith("text-moderation", StringComparison.Ordinal)
                && !lowered.StartsWith("whisper", StringComparison.Ordinal)
                && !lowered.StartsWith("tts-", StringComparison.Ordinal)
                && !lowered.StartsWith("speech-", StringComparison.Ordinal)
                && !lowered.StartsWith("rerank", StringComparison.Ordinal)
                && !lowered.StartsWith("text-search", StringComparison.Ordinal)
                && !lowered.StartsWith("text-similarity", StringComparison.Ordinal)
                && !lowered.StartsWith("code-search", StringComparison.Ordinal)
                && !lowered.StartsWith("davinci-002", StringComparison.Ordinal)
                && !lowered.StartsWith("babbage-002", StringComparison.Ordinal)
                && !lowered.StartsWith("gpt-image", StringComparison.Ordinal)
                && !lowered.StartsWith("dall-e", StringComparison.Ordinal)
                && !lowered.Contains("embedding", StringComparison.Ordinal)
                && !lowered.Contains("moderation", StringComparison.Ordinal)
                && !lowered.Contains("rerank", StringComparison.Ordinal);
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
