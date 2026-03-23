using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenCopilot.Services
{
    using OpenCopilot.Providers;

    /// <summary>
    /// Central service that manages all LLM providers and delegates calls to the active one.
    /// </summary>
    public class LlmService
    {
        private readonly List<ILlmProvider> _providers = new List<ILlmProvider>();
        private ILlmProvider _activeProvider;

        public IReadOnlyList<ILlmProvider> Providers => _providers.AsReadOnly();

        public ILlmProvider ActiveProvider
        {
            get => _activeProvider;
            set
            {
                if (value != null && !_providers.Contains(value))
                    throw new ArgumentException($"Provider '{value.Name}' is not registered.");
                _activeProvider = value;
            }
        }

        public void RegisterProvider(ILlmProvider provider)
        {
            if (provider == null) throw new ArgumentNullException(nameof(provider));
            if (_providers.Any(p => p.Name == provider.Name))
                throw new ArgumentException($"A provider named '{provider.Name}' is already registered.");

            _providers.Add(provider);
            if (_activeProvider == null)
                _activeProvider = provider;
        }

        public bool TrySetActiveProvider(string name)
        {
            var provider = _providers.FirstOrDefault(p =>
                string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

            if (provider == null) return false;
            _activeProvider = provider;
            return true;
        }

        public ILlmProvider GetProvider(string name)
            => _providers.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

        public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            if (_activeProvider == null)
                return LlmResponse.Failure("No LLM provider is active. Configure one in Tools > Options > OpenCopilot.");

            return await _activeProvider.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
        }

        public async Task<IAsyncEnumerable<string>> StreamCompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            if (_activeProvider == null)
                throw new InvalidOperationException("No LLM provider is active.");

            return await _activeProvider.StreamCompleteAsync(request, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Builds a code-completion request from the given context.</summary>
        public LlmRequest BuildCompletionRequest(
            string codeBeforeCursor,
            string codeAfterCursor,
            string language,
            string model = null,
            double temperature = 0.2,
            int maxTokens = 256)
        {
            var systemPrompt =
                $"You are an expert {language} programmer. " +
                "Complete the code at the cursor position. " +
                "Output ONLY the code to insert — no explanation, no markdown fences.";

            var userContent =
                $"Language: {language}\n\n" +
                $"Code before cursor:\n```\n{codeBeforeCursor}\n```\n\n" +
                $"Code after cursor:\n```\n{codeAfterCursor}\n```\n\n" +
                "Insert the completion here:";

            return new LlmRequest
            {
                Model = model,
                SystemPrompt = systemPrompt,
                Messages = new List<LlmMessage> { LlmMessage.User(userContent) },
                Temperature = temperature,
                MaxTokens = maxTokens
            };
        }

        /// <summary>Builds a chat request for the chat window.</summary>
        public LlmRequest BuildChatRequest(
            IEnumerable<LlmMessage> history,
            string userMessage,
            string model = null,
            double temperature = 0.7,
            int maxTokens = 2048)
        {
            var messages = new List<LlmMessage>(history) { LlmMessage.User(userMessage) };

            return new LlmRequest
            {
                Model = model,
                SystemPrompt = "You are OpenCopilot, an AI coding assistant. Help the user with code questions, explanations, and generation.",
                Messages = messages,
                Temperature = temperature,
                MaxTokens = maxTokens
            };
        }
    }
}
