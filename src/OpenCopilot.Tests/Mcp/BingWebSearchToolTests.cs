using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RichardSzalay.MockHttp;
using Xunit;
using OpenCopilot.Mcp;

namespace OpenCopilot.Tests.Mcp
{
    public class BingWebSearchToolTests
    {
        // ── helpers ──────────────────────────────────────────────────────────

        /// <summary>Creates a BingWebSearchTool backed by a mock HTTP handler via reflection.</summary>
        private static BingWebSearchTool CreateWithMock(MockHttpMessageHandler handler, string apiKey = "test-key")
        {
            // We construct with a real key; then swap the private _httpClient via reflection
            // so we can intercept HTTP calls without a real network.
            var tool = new BingWebSearchTool(apiKey);
            var field = typeof(BingWebSearchTool)
                .GetField("_httpClient", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            field!.SetValue(tool, handler.ToHttpClient());
            return tool;
        }

        private static string MakeBingResponse(params (string title, string url, string snippet)[] results)
        {
            var items = new System.Text.StringBuilder();
            for (int i = 0; i < results.Length; i++)
            {
                if (i > 0) items.Append(",");
                items.Append(JsonConvert.SerializeObject(new
                {
                    name = results[i].title,
                    url = results[i].url,
                    snippet = results[i].snippet
                }));
            }
            return $"{{\"webPages\":{{\"value\":[{items}]}}}}";
        }

        // ── tests ─────────────────────────────────────────────────────────────

        [Fact]
        public async Task SearchAsync_ReturnsResults_WhenApiSucceeds()
        {
            var mock = new MockHttpMessageHandler();
            mock.When("https://api.bing.microsoft.com/v7.0/search*")
                .Respond("application/json",
                    MakeBingResponse(
                        ("Result One", "https://example.com/1", "First snippet"),
                        ("Result Two", "https://example.com/2", "Second snippet")));

            var tool = CreateWithMock(mock);
            var service = new McpService();
            tool.RegisterInto(service);

            var result = await service.CallToolAsync("web_search",
                new Dictionary<string, object?> { ["query"] = "C# async programming" });

            Assert.False(result.IsError);
            var text = result.GetTextContent();
            Assert.Contains("Result One", text);
            Assert.Contains("https://example.com/1", text);
            Assert.Contains("First snippet", text);
            Assert.Contains("Result Two", text);
        }

        [Fact]
        public async Task SearchAsync_ReturnsFailure_WhenApiKeyMissing()
        {
            var mock = new MockHttpMessageHandler();
            var tool = CreateWithMock(mock, apiKey: "");   // empty key
            var service = new McpService();
            tool.RegisterInto(service);

            var result = await service.CallToolAsync("web_search",
                new Dictionary<string, object?> { ["query"] = "test" });

            Assert.True(result.IsError);
            Assert.Contains("Bing Search API key", result.ErrorMessage);
        }

        [Fact]
        public async Task SearchAsync_ReturnsFailure_WhenQueryMissing()
        {
            var mock = new MockHttpMessageHandler();
            var tool = CreateWithMock(mock);
            var service = new McpService();
            tool.RegisterInto(service);

            var result = await service.CallToolAsync("web_search",
                new Dictionary<string, object?>());   // no "query" key

            Assert.True(result.IsError);
            Assert.Contains("query", result.ErrorMessage);
        }

        [Fact]
        public async Task SearchAsync_ReturnsFailure_WhenApiReturnsError()
        {
            var mock = new MockHttpMessageHandler();
            mock.When("https://api.bing.microsoft.com/v7.0/search*")
                .Respond(HttpStatusCode.Unauthorized, "application/json",
                    "{\"error\":{\"statusCode\":401,\"message\":\"Access denied\"}}");

            var tool = CreateWithMock(mock);
            var service = new McpService();
            tool.RegisterInto(service);

            var result = await service.CallToolAsync("web_search",
                new Dictionary<string, object?> { ["query"] = "test" });

            Assert.True(result.IsError);
            Assert.Contains("401", result.ErrorMessage);
        }

        [Fact]
        public async Task SearchAsync_ReturnsNoResults_WhenWebPagesEmpty()
        {
            var mock = new MockHttpMessageHandler();
            mock.When("https://api.bing.microsoft.com/v7.0/search*")
                .Respond("application/json", "{\"webPages\":{\"value\":[]}}");

            var tool = CreateWithMock(mock);
            var service = new McpService();
            tool.RegisterInto(service);

            var result = await service.CallToolAsync("web_search",
                new Dictionary<string, object?> { ["query"] = "xyzzy no results" });

            Assert.False(result.IsError);
            Assert.Contains("No web results", result.GetTextContent());
        }

        [Fact]
        public async Task SearchAsync_PassesCountParameter()
        {
            var capturedUrl = string.Empty;
            var mock = new MockHttpMessageHandler();
            mock.When("https://api.bing.microsoft.com/v7.0/search*")
                .Respond(req =>
                {
                    capturedUrl = req.RequestUri!.ToString();
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            MakeBingResponse(("T", "https://x.com", "S")),
                            System.Text.Encoding.UTF8,
                            "application/json")
                    };
                });

            var tool = CreateWithMock(mock);
            var service = new McpService();
            tool.RegisterInto(service);

            await service.CallToolAsync("web_search",
                new Dictionary<string, object?> { ["query"] = "test", ["count"] = "3" });

            Assert.Contains("count=3", capturedUrl);
        }

        [Fact]
        public async Task RegisterInto_RegistersTool_WithCorrectName()
        {
            var tool = new BingWebSearchTool("key");
            var service = new McpService();
            tool.RegisterInto(service);

            var tools = await service.SearchToolsAsync("web_search");
            Assert.Single(tools);
            Assert.Equal("web_search", tools[0].Name);
        }

        [Fact]
        public void UpdateSettings_ChangesApiKey_WithoutThrowing()
        {
            var tool = new BingWebSearchTool("old-key");
            tool.UpdateSettings("new-key", null);
            // No exception = pass
        }

        [Fact]
        public void UpdateSettings_ChangesProxy_RebuildsHttpClient()
        {
            var tool = new BingWebSearchTool("key", null);
            tool.UpdateSettings("key", "http://127.0.0.1:7890");
            // No exception = pass (proxy rebuild path exercised)
        }
    }
}
