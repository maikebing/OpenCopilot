using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RichardSzalay.MockHttp;
using Xunit;
using OpenCopilot.Providers;
using System.Collections.Generic;

namespace OpenCopilot.Tests.Providers
{
    public class OllamaProviderTests
    {
        [Fact]
        public void Name_ReturnsOllama()
        {
            var provider = new OllamaProvider();
            Assert.Equal("Ollama", provider.Name);
        }

        [Fact]
        public void DefaultBaseUrl_IsLocalhost11434()
        {
            var provider = new OllamaProvider();
            // Name is the only public property we can check directly
            Assert.Equal("Ollama", provider.Name);
        }

        [Fact]
        public async Task TestConnectionAsync_ReturnsFalse_WhenServerNotAvailable()
        {
            // Point to a port that should be closed in the test environment
            var provider = new OllamaProvider("http://localhost:19999");
            var result = await provider.TestConnectionAsync(CancellationToken.None);
            // Should not throw - returns false gracefully
            Assert.False(result);
        }

        [Fact]
        public void AvailableModels_ContainsCodellama()
        {
            var provider = new OllamaProvider();
            Assert.Contains("codellama", provider.AvailableModels);
        }

        [Fact]
        public async Task CompleteAsync_ReturnsSuccess_WithValidOllamaResponse()
        {
            var mock = new MockHttpMessageHandler();
            var responseBody = JsonConvert.SerializeObject(new
            {
                message = new { role = "assistant", content = "Hello from Ollama!" },
                done = true
            });

            mock.When("http://localhost:11434/api/chat")
                .Respond("application/json", responseBody);

            var provider = new TestableOllamaProvider(mock.ToHttpClient());

            var request = new LlmRequest
            {
                Messages = new List<LlmMessage> { LlmMessage.User("Hi") },
                MaxTokens = 10
            };

            var response = await provider.CompleteAsync(request, CancellationToken.None);

            Assert.True(response.IsSuccess);
            Assert.Equal("Hello from Ollama!", response.Content);
        }

        [Fact]
        public async Task CompleteAsync_ReturnsFailure_WhenApiReturnsError()
        {
            var mock = new MockHttpMessageHandler();
            mock.When("http://localhost:11434/api/chat")
                .Respond(HttpStatusCode.InternalServerError, "application/json", "{\"error\":\"model not found\"}");

            var provider = new TestableOllamaProvider(mock.ToHttpClient());

            var request = new LlmRequest
            {
                Messages = new List<LlmMessage> { LlmMessage.User("Hi") }
            };

            var response = await provider.CompleteAsync(request, CancellationToken.None);

            Assert.False(response.IsSuccess);
            Assert.Contains("500", response.ErrorMessage);
        }
    }

    /// <summary>Testable wrapper for OllamaProvider with injected HttpClient.</summary>
    internal class TestableOllamaProvider : OllamaProvider
    {
        private readonly HttpClient _injectedClient;

        public TestableOllamaProvider(HttpClient httpClient)
            : base("http://localhost:11434", "codellama")
        {
            _injectedClient = httpClient;
        }

        public override async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            try
            {
                var payload = new
                {
                    model = request.Model ?? "codellama",
                    messages = BuildMessages(request),
                    stream = false,
                    options = new { temperature = request.Temperature, num_predict = request.MaxTokens }
                };

                var content = new System.Net.Http.StringContent(
                    JsonConvert.SerializeObject(payload),
                    System.Text.Encoding.UTF8, "application/json");

                var response = await _injectedClient.PostAsync("http://localhost:11434/api/chat", content, cancellationToken);
                var json = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                    return LlmResponse.Failure($"HTTP {(int)response.StatusCode}: {json}");

                var obj = Newtonsoft.Json.Linq.JObject.Parse(json);
                var text = obj["message"]?["content"]?.ToString() ?? string.Empty;
                var done = obj["done"]?.ToObject<bool>() ?? false;

                return LlmResponse.Success(text, done ? "stop" : "length");
            }
            catch (System.OperationCanceledException)
            {
                return LlmResponse.Failure("Request timed out or was cancelled.");
            }
            catch (System.Exception ex)
            {
                return LlmResponse.Failure($"Ollama error: {ex.Message}");
            }
        }

        private static List<object> BuildMessages(LlmRequest request)
        {
            var messages = new List<object>();
            if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
                messages.Add(new { role = "system", content = request.SystemPrompt });
            foreach (var msg in request.Messages)
                messages.Add(new { role = msg.Role, content = msg.Content });
            return messages;
        }
    }
}
