using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using OpenCopilot.Http;

namespace OpenCopilot.Mcp
{
    /// <summary>
    /// Built-in MCP tool that performs web searches via the Bing Web Search v7 API,
    /// matching the search capability exposed by VS Code's MCP integration.
    /// Requires a Bing Search API key (Azure Cognitive Services / Bing Search resource).
    /// </summary>
    public class BingWebSearchTool : IDisposable
    {
        private const string ServerName = "BuiltIn";
        private const string ToolName = "web_search";
        private const string BingEndpoint = "https://api.bing.microsoft.com/v7.0/search";

        private HttpClient _httpClient;
        private string _apiKey;
        private string? _proxyUrl;
        private bool _disposed;

        public BingWebSearchTool(string apiKey, string? proxyUrl = null)
        {
            _apiKey = apiKey;
            _proxyUrl = proxyUrl;
            _httpClient = HttpClientFactory.Create(proxyUrl, TimeSpan.FromSeconds(30));
        }

        /// <summary>Updates the API key and proxy without re-registration.</summary>
        public void UpdateSettings(string apiKey, string? proxyUrl)
        {
            _apiKey = apiKey;

            if (proxyUrl != _proxyUrl)
            {
                var old = _httpClient;
                _proxyUrl = proxyUrl;
                _httpClient = HttpClientFactory.Create(proxyUrl, TimeSpan.FromSeconds(30));
                old.Dispose();
            }
        }

        /// <summary>
        /// Registers this tool into <paramref name="service"/> as a built-in MCP tool.
        /// Call once during package initialisation.
        /// </summary>
        public void RegisterInto(McpService service)
        {
            var definition = new McpTool
            {
                Name = ToolName,
                Description = "Search the web using Bing (Microsoft). " +
                              "Returns titles, URLs, and snippets for the top results. " +
                              "Use this when you need current information not available in your knowledge base.",
                ServerName = ServerName,
                IsBuiltIn = true,
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""query"": {
                            ""type"": ""string"",
                            ""description"": ""The search query string""
                        },
                        ""count"": {
                            ""type"": ""integer"",
                            ""description"": ""Number of results to return (1-10, default 5)"",
                            ""minimum"": 1,
                            ""maximum"": 10,
                            ""default"": 5
                        },
                        ""market"": {
                            ""type"": ""string"",
                            ""description"": ""Market/locale code, e.g. en-US, zh-CN (optional)""
                        }
                    },
                    ""required"": [""query""]
                }")
            };

            service.RegisterBuiltInTool(definition, args => SearchAsync(args, CancellationToken.None));
        }

        private async Task<McpToolCallResult> SearchAsync(
            Dictionary<string, object?> args,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(_apiKey))
                return McpToolCallResult.Failure(
                    "Bing Search API key is not configured. " +
                    "Set it in Tools > Options > OpenCopilot > MCP > Bing Search API Key.");

            if (!args.TryGetValue("query", out var queryObj) || queryObj == null)
                return McpToolCallResult.Failure("Required parameter 'query' is missing.");

            var query = queryObj.ToString()!;
            var count = 5;
            if (args.TryGetValue("count", out var countObj) && countObj != null)
                int.TryParse(countObj.ToString(), out count);
            count = Math.Max(1, Math.Min(10, count));

            var market = string.Empty;
            if (args.TryGetValue("market", out var marketObj) && marketObj != null)
                market = marketObj.ToString() ?? string.Empty;

            try
            {
                var urlBuilder = new StringBuilder(BingEndpoint);
                urlBuilder.Append("?q=").Append(Uri.EscapeDataString(query));
                urlBuilder.Append("&count=").Append(count);
                urlBuilder.Append("&textDecorations=false");
                if (!string.IsNullOrWhiteSpace(market))
                    urlBuilder.Append("&mkt=").Append(Uri.EscapeDataString(market));

                using var request = new HttpRequestMessage(HttpMethod.Get, urlBuilder.ToString());
                request.Headers.Add("Ocp-Apim-Subscription-Key", _apiKey);

                var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                    return McpToolCallResult.Failure($"Bing API error {(int)response.StatusCode}: {json}");

                return ParseResults(json, query);
            }
            catch (OperationCanceledException)
            {
                return McpToolCallResult.Failure("Web search timed out or was cancelled.");
            }
            catch (Exception ex)
            {
                return McpToolCallResult.Failure($"Web search failed: {ex.Message}");
            }
        }

        private static McpToolCallResult ParseResults(string json, string query)
        {
            var obj = JObject.Parse(json);
            var items = obj["webPages"]?["value"] as JArray;

            if (items == null || items.Count == 0)
                return McpToolCallResult.Success($"No web results found for: {query}");

            var sb = new StringBuilder();
            sb.AppendLine($"Web search results for: **{query}**");
            sb.AppendLine();

            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                var title = item["name"]?.ToString() ?? "(no title)";
                var url = item["url"]?.ToString() ?? string.Empty;
                var snippet = item["snippet"]?.ToString() ?? string.Empty;

                sb.AppendLine($"{i + 1}. **{title}**");
                if (!string.IsNullOrWhiteSpace(url))
                    sb.AppendLine($"   URL: {url}");
                if (!string.IsNullOrWhiteSpace(snippet))
                    sb.AppendLine($"   {snippet}");
                sb.AppendLine();
            }

            return McpToolCallResult.Success(sb.ToString().TrimEnd());
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
