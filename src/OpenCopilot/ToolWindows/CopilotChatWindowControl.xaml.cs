using System;
using System.Collections.ObjectModel;
using System.Linq;
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
        private readonly ObservableCollection<ChatSessionInfo> _sessions = new ObservableCollection<ChatSessionInfo>();
        private LlmService? _llmService;
        private CancellationTokenSource? _cts;
        private ChatSessionStore? _sessionStore;
        private ChatSessionInfo? _currentSession;
        private bool _isSwitchingSession;

        // Number of recent messages sent as conversation history to the LLM.
        // Keeping this bounded avoids exceeding context-window limits.
        private const int MaxHistoryMessages = 20;

        public CopilotChatWindowControl()
        {
            InitializeComponent();
            MessageList.ItemsSource = _messages;
            SessionSelector.ItemsSource = _sessions;
        }

        /// <summary>Called by the package after services are ready.</summary>
        public async Task InitializeAsync(LlmService llmService, string? activeProviderName, string? activeModelName, string? solutionDirectory)
        {
            if (llmService == null)
                throw new ArgumentNullException(nameof(llmService));

            _llmService = llmService;
            UpdateStatus(activeProviderName, activeModelName);
            await InitializeSessionsAsync(solutionDirectory).ConfigureAwait(true);
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
                await PersistCurrentSessionAsync(CancellationToken.None).ConfigureAwait(true);
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

        private async void NewChatButton_Click(object sender, RoutedEventArgs e)
        {
            await StartNewChatAsync(CancellationToken.None);
        }

        private async void SessionSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isSwitchingSession)
                return;

            if (SessionSelector.SelectedItem is ChatSessionInfo session)
                await LoadSessionAsync(session, CancellationToken.None);
        }

        // ── Core send logic ──────────────────────────────────────────────────

        private async Task SendMessageAsync()
        {
            var userText = InputBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(userText) || _llmService == null) return;

            var history = BuildHistory();

            InputBox.Clear();
            InputBox.IsEnabled = false;
            SendButton.IsEnabled = false;
            TypingIndicator.Visibility = Visibility.Visible;

            _messages.Add(new ChatMessage("You", userText));
            ChatScrollViewer.ScrollToBottom();

            await PersistCurrentSessionAsync(CancellationToken.None).ConfigureAwait(true);
            await UpdateFallbackSessionTitleAsync(CancellationToken.None).ConfigureAwait(true);

            _cts?.Cancel();
            _cts = new CancellationTokenSource();

            try
            {
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

                await PersistCurrentSessionAsync(CancellationToken.None).ConfigureAwait(true);
                await TryGenerateSessionTitleAsync(CancellationToken.None).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                await ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    TypingIndicator.Visibility = Visibility.Collapsed;
                    _messages.Add(new ChatMessage("OpenCopilot", "Request cancelled."));
                });

                await PersistCurrentSessionAsync(CancellationToken.None).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                await ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    TypingIndicator.Visibility = Visibility.Collapsed;
                    _messages.Add(new ChatMessage("OpenCopilot", $"⚠ Unexpected error: {ex.Message}"));
                });

                await PersistCurrentSessionAsync(CancellationToken.None).ConfigureAwait(true);
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

        private async Task InitializeSessionsAsync(string? solutionDirectory)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            _sessionStore = null;
            _sessions.Clear();
            _messages.Clear();
            _currentSession = null;
            SessionSelector.SelectedItem = null;
            SessionSelector.IsEnabled = false;

            if (string.IsNullOrWhiteSpace(solutionDirectory))
                return;

            _sessionStore = new ChatSessionStore(solutionDirectory);

            var sessions = await _sessionStore.LoadSessionsAsync(CancellationToken.None).ConfigureAwait(true);
            foreach (var session in sessions)
                _sessions.Add(session);

            SessionSelector.IsEnabled = true;

            if (_sessions.Count > 0)
                await LoadSessionAsync(_sessions[0], CancellationToken.None).ConfigureAwait(true);
        }

        private async Task StartNewChatAsync(CancellationToken cancellationToken)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            _messages.Clear();

            if (_sessionStore == null)
            {
                _currentSession = null;
                SessionSelector.SelectedItem = null;
                InputBox.Focus();
                return;
            }

            var session = await _sessionStore.CreateSessionAsync(CreateGeneratedSessionTitle(), cancellationToken).ConfigureAwait(true);
            InsertSession(session);
            await LoadSessionAsync(session, cancellationToken).ConfigureAwait(true);
            InputBox.Focus();
        }

        private async Task LoadSessionAsync(ChatSessionInfo session, CancellationToken cancellationToken)
        {
            if (session == null)
                throw new ArgumentNullException(nameof(session));
            if (_sessionStore == null)
                return;

            var messages = await _sessionStore.LoadMessagesAsync(session, cancellationToken).ConfigureAwait(true);

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            _isSwitchingSession = true;
            try
            {
                _currentSession = session;
                _messages.Clear();
                foreach (var message in messages)
                    _messages.Add(message);

                SessionSelector.SelectedItem = session;
                ChatScrollViewer.ScrollToBottom();
            }
            finally
            {
                _isSwitchingSession = false;
            }
        }

        private async Task PersistCurrentSessionAsync(CancellationToken cancellationToken)
        {
            if (_sessionStore == null)
                return;

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (_currentSession == null)
            {
                _currentSession = await _sessionStore.CreateSessionAsync(CreateGeneratedSessionTitle(), cancellationToken).ConfigureAwait(true);
                InsertSession(_currentSession);
                _isSwitchingSession = true;
                try
                {
                    SessionSelector.SelectedItem = _currentSession;
                }
                finally
                {
                    _isSwitchingSession = false;
                }
            }

            var snapshot = _messages.Select(message => new ChatMessage(message.Sender, message.Content)).ToList();
            await _sessionStore.SaveSessionAsync(_currentSession, snapshot, cancellationToken).ConfigureAwait(true);
            MoveSessionToTop(_currentSession);
        }

        private async Task UpdateFallbackSessionTitleAsync(CancellationToken cancellationToken)
        {
            if (_currentSession == null || _currentSession.TitleSource != ChatSessionTitleSource.Generated)
                return;

            var firstUserMessage = _messages.FirstOrDefault(message => message.IsUser);
            if (firstUserMessage == null || string.IsNullOrWhiteSpace(firstUserMessage.Content))
                return;

            var fallbackTitle = BuildFallbackTitle(firstUserMessage.Content);
            if (string.IsNullOrWhiteSpace(fallbackTitle))
                return;

            _currentSession.Title = fallbackTitle;
            _currentSession.TitleSource = ChatSessionTitleSource.Fallback;
            await PersistCurrentSessionAsync(cancellationToken).ConfigureAwait(true);
        }

        private async Task TryGenerateSessionTitleAsync(CancellationToken cancellationToken)
        {
            if (_llmService == null || _currentSession == null || _messages.Count < 2)
                return;
            if (_currentSession.TitleSource == ChatSessionTitleSource.Ai)
                return;

            var transcript = string.Join("\n\n", _messages.Take(6).Select(message => $"{message.Sender}:\n{message.Content}"));
            if (string.IsNullOrWhiteSpace(transcript))
                return;

            var request = new OpenCopilot.Providers.LlmRequest
            {
                SystemPrompt = "Generate a concise chat title for a coding conversation. Return a single plain-text title with no markdown, quotes, or punctuation at the end.",
                Messages = new System.Collections.Generic.List<OpenCopilot.Providers.LlmMessage>
                {
                    OpenCopilot.Providers.LlmMessage.User($"Create a short title (max 12 words) for this conversation:\n\n{transcript}")
                },
                Temperature = 0.2,
                MaxTokens = 32
            };

            var response = await _llmService.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccess || string.IsNullOrWhiteSpace(response.Content))
                return;

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            _currentSession.Title = SanitizeSessionTitle(response.Content);
            _currentSession.TitleSource = ChatSessionTitleSource.Ai;
            await PersistCurrentSessionAsync(cancellationToken).ConfigureAwait(true);
        }

        private void InsertSession(ChatSessionInfo session)
        {
            if (_sessions.Any(existing => string.Equals(existing.FilePath, session.FilePath, StringComparison.OrdinalIgnoreCase)))
                return;

            _sessions.Insert(0, session);
        }

        private void MoveSessionToTop(ChatSessionInfo session)
        {
            var index = _sessions.IndexOf(session);
            if (index <= 0)
                return;

            _sessions.Move(index, 0);
        }

        private static string CreateGeneratedSessionTitle()
            => $"New chat {DateTime.Now:yyyy-MM-dd HH:mm}";

        private static string BuildFallbackTitle(string content)
        {
            var normalized = (content ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            if (normalized.Length == 0)
                return CreateGeneratedSessionTitle();

            return normalized.Length > 60 ? normalized.Substring(0, 60).TrimEnd() : normalized;
        }

        private static string SanitizeSessionTitle(string title)
        {
            var sanitized = (title ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            sanitized = sanitized.Trim('"', '\'', '#', '-', ' ');
            return sanitized.Length == 0 ? CreateGeneratedSessionTitle() : BuildFallbackTitle(sanitized);
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
