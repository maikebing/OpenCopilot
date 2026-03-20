using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using OpenCopilot.Providers;
using OpenCopilot.Services;

namespace OpenCopilot.Tests.Services
{
    public class LlmServiceTests
    {
        private static LlmService CreateServiceWithProvider(ILlmProvider provider)
        {
            var service = new LlmService();
            service.RegisterProvider(provider);
            return service;
        }

        [Fact]
        public void RegisterProvider_AddsToProviderList()
        {
            var service = new LlmService();
            var provider = new StubProvider("Test", true);
            service.RegisterProvider(provider);

            Assert.Single(service.Providers);
            Assert.Equal("Test", service.Providers[0].Name);
        }

        [Fact]
        public void RegisterProvider_SetsFirstProviderAsActive()
        {
            var service = new LlmService();
            service.RegisterProvider(new StubProvider("First", true));
            Assert.Equal("First", service.ActiveProvider.Name);
        }

        [Fact]
        public void RegisterProvider_ThrowsOnDuplicateName()
        {
            var service = new LlmService();
            service.RegisterProvider(new StubProvider("Dup", true));
            Assert.Throws<System.ArgumentException>(() => service.RegisterProvider(new StubProvider("Dup", true)));
        }

        [Fact]
        public void TrySetActiveProvider_ReturnsTrueForRegisteredProvider()
        {
            var service = new LlmService();
            service.RegisterProvider(new StubProvider("A", true));
            service.RegisterProvider(new StubProvider("B", true));

            var result = service.TrySetActiveProvider("B");
            Assert.True(result);
            Assert.Equal("B", service.ActiveProvider.Name);
        }

        [Fact]
        public void TrySetActiveProvider_ReturnsFalseForUnknownProvider()
        {
            var service = new LlmService();
            service.RegisterProvider(new StubProvider("A", true));

            var result = service.TrySetActiveProvider("NotRegistered");
            Assert.False(result);
        }

        [Fact]
        public async Task CompleteAsync_ReturnsFailure_WhenNoActiveProvider()
        {
            var service = new LlmService();
            var response = await service.CompleteAsync(new LlmRequest());
            Assert.False(response.IsSuccess);
            Assert.Contains("No LLM provider", response.ErrorMessage);
        }

        [Fact]
        public async Task CompleteAsync_DelegatesToActiveProvider()
        {
            var stub = new StubProvider("Test", true, "AI says hi");
            var service = CreateServiceWithProvider(stub);

            var response = await service.CompleteAsync(new LlmRequest
            {
                Messages = new List<LlmMessage> { LlmMessage.User("hello") }
            });

            Assert.True(response.IsSuccess);
            Assert.Equal("AI says hi", response.Content);
        }

        [Fact]
        public async Task CompleteAsync_ReturnsFailure_WhenProviderFails()
        {
            var stub = new StubProvider("Test", false, errorMessage: "API error");
            var service = CreateServiceWithProvider(stub);

            var response = await service.CompleteAsync(new LlmRequest());
            Assert.False(response.IsSuccess);
            Assert.Equal("API error", response.ErrorMessage);
        }

        [Fact]
        public void GetProvider_ReturnsCorrectProvider()
        {
            var service = new LlmService();
            service.RegisterProvider(new StubProvider("Alpha", true));
            service.RegisterProvider(new StubProvider("Beta", true));

            var p = service.GetProvider("beta"); // case-insensitive
            Assert.NotNull(p);
            Assert.Equal("Beta", p.Name);
        }

        [Fact]
        public void BuildCompletionRequest_IncludesContextInMessages()
        {
            var service = new LlmService();
            var req = service.BuildCompletionRequest("before", "after", "C#");

            Assert.NotEmpty(req.Messages);
            Assert.Contains("before", req.Messages[0].Content);
            Assert.Contains("after", req.Messages[0].Content);
            Assert.Contains("C#", req.Messages[0].Content);
        }

        [Fact]
        public void BuildChatRequest_IncludesHistoryAndNewMessage()
        {
            var service = new LlmService();
            var history = new List<LlmMessage>
            {
                LlmMessage.User("What is 2+2?"),
                LlmMessage.Assistant("4")
            };

            var req = service.BuildChatRequest(history, "explain more");

            Assert.Equal(3, req.Messages.Count);
            Assert.Equal("explain more", req.Messages[2].Content);
            Assert.Equal("user", req.Messages[2].Role);
        }
    }

    /// <summary>Test stub for ILlmProvider.</summary>
    internal class StubProvider : ILlmProvider
    {
        private readonly bool _success;
        private readonly string _content;
        private readonly string _errorMessage;

        public string Name { get; }
        public string[] AvailableModels => new[] { "stub-model" };

        public StubProvider(string name, bool success, string content = "stub response", string errorMessage = "stub error")
        {
            Name = name;
            _success = success;
            _content = content;
            _errorMessage = errorMessage;
        }

        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            var result = _success
                ? LlmResponse.Success(_content)
                : LlmResponse.Failure(_errorMessage);
            return Task.FromResult(result);
        }

        public Task<IAsyncEnumerable<string>> StreamCompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(EmptyAsyncEnumerable());

        public Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_success);

        private static async IAsyncEnumerable<string> EmptyAsyncEnumerable()
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
