using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Editor;
using OpenCopilot.Options;
using OpenCopilot.Services;

namespace OpenCopilot.Completion
{
    /// <summary>
    /// Provides AI-powered inline completion items when the user types in the editor.
    /// </summary>
    internal class CompletionSource : IAsyncCompletionSource
    {
        private readonly LlmService _llmService;
        private readonly OpenCopilotOptionsPage _optionsPage;

        // 40 lines before the cursor gives the model enough context to infer intent
        // without exceeding typical prompt size limits for completion use cases.
        private const int ContextLines = 40;

        // Maximum length of the completion display text shown in the IntelliSense popup.
        private const int MaxDisplayLength = 80;

        public CompletionSource(LlmService llmService, OpenCopilotOptionsPage optionsPage)
        {
            _llmService = llmService;
            _optionsPage = optionsPage;
        }

        public async Task<CompletionContext> GetCompletionContextAsync(
            IAsyncCompletionSession session,
            CompletionTrigger trigger,
            SnapshotPoint triggerLocation,
            SnapshotSpan applicableToSpan,
            CancellationToken token)
        {
            var options = _optionsPage.GetOptions();
            if (!options.EnableInlineCompletion)
                return CompletionContext.Empty;

            // Only trigger on character insertion or explicit invoke
            if (trigger.Reason != CompletionTriggerReason.Insertion &&
                trigger.Reason != CompletionTriggerReason.InvokeAndCommitIfUnique &&
                trigger.Reason != CompletionTriggerReason.Invoke)
                return CompletionContext.Empty;

            var snapshot = triggerLocation.Snapshot;
            var (codeBefore, codeAfter) = ExtractContext(snapshot, triggerLocation.Position);
            var language = DetectLanguage(session.TextView);

            try
            {
                var request = _llmService.BuildCompletionRequest(
                    codeBefore, codeAfter, language,
                    temperature: options.Temperature,
                    maxTokens: options.MaxTokens);

                var response = await _llmService.CompleteAsync(request, token).ConfigureAwait(false);

                if (!response.IsSuccess || string.IsNullOrWhiteSpace(response.Content))
                    return CompletionContext.Empty;

                var suggestion = response.Content.Trim();
                var item = new CompletionItem(
                    displayText: TruncateForDisplay(suggestion),
                    source: this,
                    icon: default,
                    filters: ImmutableArray<CompletionFilter>.Empty,
                    suffix: string.Empty,
                    insertText: suggestion,
                    sortText: "\x0001" + suggestion, // sort to top
                    filterText: suggestion,
                    automationText: suggestion,
                    attributeIcons: ImmutableArray<ImageElement>.Empty);

                return new CompletionContext(ImmutableArray.Create(item));
            }
            catch (OperationCanceledException)
            {
                return CompletionContext.Empty;
            }
            catch
            {
                return CompletionContext.Empty;
            }
        }

        public async Task<object> GetDescriptionAsync(
            IAsyncCompletionSession session,
            CompletionItem item,
            CancellationToken token)
        {
            return await Task.FromResult<object>($"OpenCopilot AI suggestion\n\n{item.InsertText}");
        }

        public CompletionStartData InitializeCompletion(
            CompletionTrigger trigger,
            SnapshotPoint triggerLocation,
            CancellationToken token)
        {
            var options = _optionsPage.GetOptions();
            if (!options.EnableInlineCompletion)
                return CompletionStartData.DoesNotParticipateInCompletion;

            // Participate on character insertion for word characters
            if (trigger.Reason == CompletionTriggerReason.Insertion)
            {
                var c = trigger.Character;
                if (char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '(')
                    return new CompletionStartData(CompletionParticipation.ProvidesItems, GetApplicableSpan(triggerLocation));
            }

            return CompletionStartData.DoesNotParticipateInCompletion;
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private static (string before, string after) ExtractContext(ITextSnapshot snapshot, int position)
        {
            var lines = snapshot.Lines;
            int currentLine = snapshot.GetLineNumberFromPosition(position);

            int startLine = Math.Max(0, currentLine - ContextLines);
            int endLine = Math.Min(snapshot.LineCount - 1, currentLine + ContextLines / 4);

            var beforeStart = snapshot.GetLineFromLineNumber(startLine).Start.Position;
            var afterEnd = snapshot.GetLineFromLineNumber(endLine).End.Position;

            var before = snapshot.GetText(beforeStart, position - beforeStart);
            var after = snapshot.GetText(position, afterEnd - position);

            return (before, after);
        }

        private static string DetectLanguage(ITextView view)
        {
            var buffer = view.TextBuffer;
            var contentType = buffer.ContentType.TypeName;

            return contentType switch
            {
                var t when t.IndexOf("csharp", StringComparison.OrdinalIgnoreCase) >= 0 => "C#",
                var t when t.IndexOf("python", StringComparison.OrdinalIgnoreCase) >= 0 => "Python",
                var t when t.IndexOf("typescript", StringComparison.OrdinalIgnoreCase) >= 0 => "TypeScript",
                var t when t.IndexOf("javascript", StringComparison.OrdinalIgnoreCase) >= 0 => "JavaScript",
                var t when t.IndexOf("cpp", StringComparison.OrdinalIgnoreCase) >= 0 => "C++",
                var t when t.IndexOf("java", StringComparison.OrdinalIgnoreCase) >= 0 => "Java",
                var t when t.IndexOf("go", StringComparison.OrdinalIgnoreCase) >= 0 => "Go",
                var t when t.IndexOf("rust", StringComparison.OrdinalIgnoreCase) >= 0 => "Rust",
                _ => contentType
            };
        }

        private static SnapshotSpan GetApplicableSpan(SnapshotPoint triggerLocation)
        {
            var snapshot = triggerLocation.Snapshot;
            var position = triggerLocation.Position;
            var line = snapshot.GetLineFromPosition(position);
            int start = position;

            while (start > line.Start.Position)
            {
                var ch = snapshot[start - 1];
                if (!char.IsLetterOrDigit(ch) && ch != '_') break;
                start--;
            }

            return new SnapshotSpan(snapshot, start, position - start);
        }

        private static string TruncateForDisplay(string text, int maxLength = MaxDisplayLength)
        {
            if (text.Length <= maxLength) return text;
            var firstLine = text.IndexOf('\n');
            return firstLine > 0 && firstLine < maxLength
                ? text.Substring(0, firstLine) + " ..."
                : text.Substring(0, maxLength) + " ...";
        }
    }
}
