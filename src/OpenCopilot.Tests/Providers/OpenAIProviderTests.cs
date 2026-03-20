using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RichardSzalay.MockHttp;
using Xunit;
using OpenCopilot.Providers;

namespace OpenCopilot.Tests.Providers
{
    public class OpenAIProviderTests
    {
        private static OpenAIProvider CreateProviderWithMockHttp(MockHttpMessageHandler handler, string apiKey = "test-key")
        {
            // Use reflection to inject the mock HttpClient because the provider
            // creates its own client. A factory pattern would be cleaner but for
            // testing purposes we reach into the private field.
            var provider = new TestableOpenAIProvider(apiKey, "gpt-4o-mini", "https://api.openai.com/v1", handler.ToHttpClient());
            return provider;
        }

        [Fact]
        public async Task CompleteAsync_ReturnsSuccess_WhenApiReturnsOk()
        {
            var mock = new MockHttpMessageHandler();
            var responseBody = JsonConvert.SerializeObject(new
            {
                choices = new[]
                {
                    new { message = new { content = "Hello, world!" }, finish_reason = "stop" }
                },
                usage = new { prompt_tokens = 10, completion_tokens = 5 }
            });

            mock.When("https://api.openai.com/v1/chat/completions")
                .Respond("application/json", responseBody);

            var provider = new TestableOpenAIProvider("test-key", "gpt-4o-mini", "https://api.openai.com/v1", mock.ToHttpClient());

            var request = new LlmRequest
            {
                Messages = new List<LlmMessage> { LlmMessage.User("Hi") },
                MaxTokens = 10
            };

            var response = await provider.CompleteAsync(request, CancellationToken.None);

            Assert.True(response.IsSuccess);
            Assert.Equal("Hello, world!", response.Content);
            Assert.Equal("stop", response.FinishReason);
            Assert.Equal(10, response.PromptTokens);
            Assert.Equal(5, response.CompletionTokens);
        }

        [Fact]
        public async Task CompleteAsync_ReturnsFailure_WhenApiReturnsError()
        {
            var mock = new MockHttpMessageHandler();
            mock.When("https://api.openai.com/v1/chat/completions")
                .Respond(HttpStatusCode.Unauthorized, "application/json",
                    JsonConvert.SerializeObject(new { error = new { message = "Invalid API key" } }));

            var provider = new TestableOpenAIProvider("bad-key", "gpt-4o-mini", "https://api.openai.com/v1", mock.ToHttpClient());

            var request = new LlmRequest
            {
                Messages = new List<LlmMessage> { LlmMessage.User("Hi") }
            };

            var response = await provider.CompleteAsync(request, CancellationToken.None);

            Assert.False(response.IsSuccess);
            Assert.Contains("401", response.ErrorMessage);
        }

        [Fact]
        public async Task CompleteAsync_ReturnsFailure_WhenNetworkFails()
        {
            var mock = new MockHttpMessageHandler();
            mock.When("https://api.openai.com/v1/chat/completions")
                .Throw(new HttpRequestException("Network error"));

            var provider = new TestableOpenAIProvider("test-key", "gpt-4o-mini", "https://api.openai.com/v1", mock.ToHttpClient());

            var request = new LlmRequest
            {
                Messages = new List<LlmMessage> { LlmMessage.User("Hi") }
            };

            var response = await provider.CompleteAsync(request, CancellationToken.None);

            Assert.False(response.IsSuccess);
            Assert.Contains("Network error", response.ErrorMessage);
        }

        [Fact]
        public void Name_ReturnsOpenAI()
        {
            var provider = new OpenAIProvider("key");
            Assert.Equal("OpenAI", provider.Name);
        }

        [Fact]
        public void AvailableModels_ContainsGpt4o()
        {
            var provider = new OpenAIProvider("key");
            Assert.Contains("gpt-4o", provider.AvailableModels);
        }

        [Fact]
        public void DeepSeekProvider_Name_IsDeepSeek()
        {
            var p = new DeepSeekProvider("key");
            Assert.Equal("DeepSeek", p.Name);
        }

        [Fact]
        public void DoubaoProvider_Name_IsDoubao()
        {
            var p = new DoubaoProvider("key");
            Assert.Equal("Doubao", p.Name);
        }

        [Fact]
        public void DockerDesktopAIProvider_Name_IsDockerDesktopAI()
        {
            var p = new DockerDesktopAIProvider();
            Assert.Equal("Docker Desktop AI", p.Name);
        }
    }

    /// <summary>Allows injecting a custom HttpClient for testing.</summary>
    internal class TestableOpenAIProvider : OpenAIProvider
    {
        private readonly HttpClient _injectedClient;

        public TestableOpenAIProvider(string apiKey, string model, string baseUrl, HttpClient httpClient)
            : base(apiKey, model, baseUrl)
        {
            _injectedClient = httpClient;
        }

        // Override CompleteAsync to use the injected client
        public override async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            try
            {
                var payload = BuildPayload(request, stream: false);
                var content = new System.Net.Http.StringContent(
                    JsonConvert.SerializeObject(payload),
                    System.Text.Encoding.UTF8, "application/json");

                using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
                httpRequest.Content = content;
                SetAuthHeader(httpRequest);

                var response = await _injectedClient.SendAsync(httpRequest, cancellationToken);
                var json = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                    return LlmResponse.Failure($"HTTP {(int)response.StatusCode}: {json}");

                var obj = Newtonsoft.Json.Linq.JObject.Parse(json);
                var text = obj["choices"]?[0]?["message"]?["content"]?.ToString() ?? string.Empty;
                var finish = obj["choices"]?[0]?["finish_reason"]?.ToString() ?? "stop";
                var promptTokens = obj["usage"]?["prompt_tokens"]?.ToObject<int>() ?? 0;
                var completionTokens = obj["usage"]?["completion_tokens"]?.ToObject<int>() ?? 0;

                return LlmResponse.Success(text, finish, promptTokens, completionTokens);
            }
            catch (System.OperationCanceledException)
            {
                return LlmResponse.Failure("Request timed out or was cancelled.");
            }
            catch (System.Exception ex)
            {
                return LlmResponse.Failure($"Error: {ex.Message}");
            }
        }
    }
}
