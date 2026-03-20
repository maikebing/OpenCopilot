using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenCopilot.Providers
{
    /// <summary>
    /// Abstraction over an LLM backend (OpenAI, DeepSeek, Ollama, etc.).
    /// </summary>
    public interface ILlmProvider
    {
        string Name { get; }
        string[] AvailableModels { get; }

        Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default);
        Task<IAsyncEnumerable<string>> StreamCompleteAsync(LlmRequest request, CancellationToken cancellationToken = default);
        Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default);
    }
}
