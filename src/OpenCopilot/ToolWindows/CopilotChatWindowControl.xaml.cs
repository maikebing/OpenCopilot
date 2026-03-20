using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.Shell;
using OpenCopilot.Services;

namespace OpenCopilot.ToolWindows
{
    /// <summary>
    /// Code-behind for the OpenCopilot Chat tool window.
    /// </summary>
    public partial class CopilotChatWindowControl : UserControl
    {
        private readonly ObservableCollection<ChatMessage> _messages = new ObservableCollection<ChatMessage>();
        private LlmService? _llmService;
        private CancellationTokenSource? _cts;

        // Number of recent messages sent as conversation history to the LLM.
        // Keeping this bounded avoids exceeding context-window limits.
        private const int MaxHistoryMessages = 20;

        public CopilotChatWindowControl()
        {
            InitializeComponent();
            MessageList.ItemsSource = _messages;
        }

        /// <summary>Called by the package after services are ready.</summary>
        public void Initialize(LlmService llmService, string? activeProviderName, string? activeModelName)
        {
            _llmService = llmService;
            UpdateStatus(activeProviderName, activeModelName);
        }

        public void UpdateStatus(string? providerName, string? modelName)
        {
            ThreadHelper.JoinableTaskFactory.Run(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                StatusLabel.Text = string.IsNullOrWhiteSpace(providerName)
                    ? "OpenCopilot · No provider configured"
                    : $"OpenCopilot · {providerName} / {modelName}";
            });
        }

        /// <summary>Programmatically add a message (used by commands).</summary>
        public void AddMessage(string sender, string content)
        {
            ThreadHelper.JoinableTaskFactory.Run(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                _messages.Add(new ChatMessage(sender, content));
                ChatScrollViewer.ScrollToBottom();
            });
        }

        // ── Event handlers ───────────────────────────────────────────────────

        private void SendButton_Click(object sender, RoutedEventArgs e)
            => _ = SendMessageAsync();

        private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && !Keyboard.IsKeyDown(Key.LeftShift) && !Keyboard.IsKeyDown(Key.RightShift))
            {
                e.Handled = true;
                _ = SendMessageAsync();
            }
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            _messages.Clear();
        }

        // ── Core send logic ──────────────────────────────────────────────────

        private async Task SendMessageAsync()
        {
            var userText = InputBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(userText) || _llmService == null) return;

            InputBox.Clear();
            InputBox.IsEnabled = false;
            SendButton.IsEnabled = false;
            TypingIndicator.Visibility = Visibility.Visible;

            _messages.Add(new ChatMessage("You", userText));
            ChatScrollViewer.ScrollToBottom();

            _cts?.Cancel();
            _cts = new CancellationTokenSource();

            try
            {
                var history = BuildHistory();
                var request = _llmService.BuildChatRequest(history, userText);

                var response = await _llmService.CompleteAsync(request, _cts.Token).ConfigureAwait(false);

                await ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    TypingIndicator.Visibility = Visibility.Collapsed;
                    if (response.IsSuccess)
                        _messages.Add(new ChatMessage("OpenCopilot", response.Content));
                    else
                        _messages.Add(new ChatMessage("OpenCopilot", $"⚠ {response.ErrorMessage}"));

                    ChatScrollViewer.ScrollToBottom();
                });
            }
            catch (OperationCanceledException)
            {
                await ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    TypingIndicator.Visibility = Visibility.Collapsed;
                    _messages.Add(new ChatMessage("OpenCopilot", "Request cancelled."));
                });
            }
            catch (Exception ex)
            {
                await ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    TypingIndicator.Visibility = Visibility.Collapsed;
                    _messages.Add(new ChatMessage("OpenCopilot", $"⚠ Unexpected error: {ex.Message}"));
                });
            }
            finally
            {
                await ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    InputBox.IsEnabled = true;
                    SendButton.IsEnabled = true;
                    InputBox.Focus();
                });
            }
        }

        private System.Collections.Generic.List<OpenCopilot.Providers.LlmMessage> BuildHistory()
        {
            var history = new System.Collections.Generic.List<OpenCopilot.Providers.LlmMessage>();
            int start = Math.Max(0, _messages.Count - MaxHistoryMessages);
            for (int i = start; i < _messages.Count; i++)
            {
                var m = _messages[i];
                if (m.Sender == "You")
                    history.Add(OpenCopilot.Providers.LlmMessage.User(m.Content));
                else if (m.Sender == "OpenCopilot")
                    history.Add(OpenCopilot.Providers.LlmMessage.Assistant(m.Content));
            }
            return history;
        }
    }

    /// <summary>View model for a single chat bubble.</summary>
    public class ChatMessage
    {
        public string Sender { get; }
        public string Content { get; }
        public bool IsUser => Sender == "You";

        public HorizontalAlignment SenderAlignment => IsUser ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        public HorizontalAlignment BubbleAlignment => IsUser ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        public Brush BubbleBackground => IsUser ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0078D4")) : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2D2D30"));
        public CornerRadius BubbleCornerRadius => IsUser ? new CornerRadius(8, 8, 2, 8) : new CornerRadius(8, 8, 8, 2);
        public Thickness BubbleMargin => IsUser ? new Thickness(40, 4, 8, 4) : new Thickness(8, 4, 40, 4);
        public Brush TextForeground => IsUser ? Brushes.White : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#D4D4D4"));
        public FontFamily TextFont => IsUser ? SystemFonts.MessageFontFamily : new FontFamily("Consolas, Courier New, monospace");

        public ChatMessage(string sender, string content)
        {
            Sender = sender;
            Content = content;
        }
    }
}
