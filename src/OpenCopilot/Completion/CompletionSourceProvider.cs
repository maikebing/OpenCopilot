using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using OpenCopilot.Options;
using OpenCopilot.Services;

namespace OpenCopilot.Completion
{
    /// <summary>
    /// MEF export that creates a <see cref="CompletionSource"/> for every code editor.
    /// </summary>
    [Export(typeof(IAsyncCompletionSourceProvider))]
    [ContentType("code")]
    [Name("OpenCopilotCompletion")]
    [Order(Before = "default")]
    internal class CompletionSourceProvider : IAsyncCompletionSourceProvider
    {
        // These are set by the package after it initialises its services.
        // volatile ensures the write is visible to all threads immediately.
        private static volatile LlmService _llmServiceInstance;
        private static volatile OpenCopilotOptionsPage _optionsPageInstance;

        internal static LlmService LlmServiceInstance
        {
            get => _llmServiceInstance;
            set => _llmServiceInstance = value;
        }

        internal static OpenCopilotOptionsPage OptionsPageInstance
        {
            get => _optionsPageInstance;
            set => _optionsPageInstance = value;
        }

        public IAsyncCompletionSource GetOrCreate(ITextView textView)
        {
            if (LlmServiceInstance == null || OptionsPageInstance == null)
                return textView.Properties.GetOrCreateSingletonProperty(() => new NoOpCompletionSource());

            return textView.Properties.GetOrCreateSingletonProperty<IAsyncCompletionSource>(
                () => new CompletionSource(LlmServiceInstance, OptionsPageInstance));
        }
    }

    /// <summary>Returned when the extension services are not yet initialised.</summary>
    internal sealed class NoOpCompletionSource : IAsyncCompletionSource
    {
        public Task<CompletionContext> GetCompletionContextAsync(
            IAsyncCompletionSession session,
            CompletionTrigger trigger,
            SnapshotPoint triggerLocation,
            SnapshotSpan applicableToSpan,
            CancellationToken token)
            => Task.FromResult(CompletionContext.Empty);

        public Task<object> GetDescriptionAsync(
            IAsyncCompletionSession session,
            CompletionItem item,
            CancellationToken token)
            => Task.FromResult<object>(string.Empty);

        public CompletionStartData InitializeCompletion(
            CompletionTrigger trigger,
            SnapshotPoint triggerLocation,
            CancellationToken token)
            => CompletionStartData.DoesNotParticipateInCompletion;
    }
}
