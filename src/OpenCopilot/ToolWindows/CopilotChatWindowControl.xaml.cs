using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.VisualStudio.Shell;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using AgentToolCallModel = OpenCopilot.Chat.AgentToolCall;
using OpenCopilot.Chat;
using OpenCopilot.Mcp;
using OpenCopilot.Options;
using OpenCopilot.Providers;
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
        private readonly ObservableCollection<ChatAttachment> _attachments = new ObservableCollection<ChatAttachment>();
        private readonly ObservableCollection<ChatPlanItem> _planItems = new ObservableCollection<ChatPlanItem>();
        private readonly ObservableCollection<WorkspaceChangedFile> _changedFiles = new ObservableCollection<WorkspaceChangedFile>();
        private readonly ObservableCollection<ChatInteractionModeOption> _interactionModes = new ObservableCollection<ChatInteractionModeOption>
        {
            new ChatInteractionModeOption(ChatInteractionMode.Ask, "咨询"),
            new ChatInteractionModeOption(ChatInteractionMode.Agent, "智能体")
        };
        private readonly ObservableCollection<string> _providers = new ObservableCollection<string>();
        private readonly ObservableCollection<string> _models = new ObservableCollection<string>();
        private readonly HashSet<string> _retainedChangedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _preferredModelsByProvider = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _activeToolSummarySteps = new List<string>();

        private OpenCopilotPackage? _package;
        private LlmService? _llmService;
        private CancellationTokenSource? _cts;
        private ChatSessionStore? _sessionStore;
        private ChatWorkspaceSettingsStore? _workspaceSettingsStore;
        private ChatSessionInfo? _currentSession;
        private GitWorkspaceChangeTracker? _gitTracker;
        private ChatWorkspaceSettings _workspaceSettings = new ChatWorkspaceSettings();
        private string? _solutionDirectory;
        private string? _selectedProviderName;
        private string? _selectedModelName;
        private ChatInteractionMode _selectedInteractionMode = ChatInteractionMode.Agent;
        private bool _isSwitchingSession;
        private bool _isUpdatingSelectors;

        private const int MaxHistoryMessages = 20;
        private const int MaxAttachmentCharacters = 12000;
        private const int MaxAgentIterations = 6;

        internal bool IsInitialized => _package != null && _llmService != null;

        public CopilotChatWindowControl()
        {
            InitializeComponent();
            MessageList.ItemsSource = _messages;
            SessionSelector.ItemsSource = _sessions;
            AttachmentList.ItemsSource = _attachments;
            PlanList.ItemsSource = _planItems;
            ChangedFilesList.ItemsSource = _changedFiles;
            ModeSelector.ItemsSource = _interactionModes;
            ProviderSelector.ItemsSource = _providers;
            ModelSelector.ItemsSource = _models;
            ModeSelector.SelectedItem = _interactionModes.First(option => option.Mode == _selectedInteractionMode);
            SetIdlePlan();
        }

        /// <summary>Called by the package after services are ready.</summary>
        internal async Task InitializeAsync(OpenCopilotPackage package, LlmService llmService, IReadOnlyList<ConfiguredProviderDefinition> configuredProviders, string? activeProviderName, string? activeModelName, string? solutionDirectory)
        {
            if (package == null)
                throw new ArgumentNullException(nameof(package));
            if (llmService == null)
                throw new ArgumentNullException(nameof(llmService));

            _package = package;
            _llmService = llmService;
            _solutionDirectory = solutionDirectory;
            _workspaceSettingsStore = string.IsNullOrWhiteSpace(solutionDirectory)
                ? null
                : new ChatWorkspaceSettingsStore(solutionDirectory);
            _gitTracker = string.IsNullOrWhiteSpace(solutionDirectory)
                ? null
                : new GitWorkspaceChangeTracker(solutionDirectory);

            await LoadWorkspaceSettingsAsync(CancellationToken.None).ConfigureAwait(true);
            await ApplyProviderConfigurationAsync(configuredProviders, activeProviderName, activeModelName, CancellationToken.None).ConfigureAwait(true);
            UpdateStatus(_selectedProviderName, _selectedModelName);
            await InitializeSessionsAsync(solutionDirectory).ConfigureAwait(true);
            await RefreshChangedFilesAsync(CancellationToken.None).ConfigureAwait(true);
        }

        /// <summary>Refreshes the visible provider/model selectors after settings change.</summary>
        internal async Task RefreshProviderConfigurationAsync(IReadOnlyList<ConfiguredProviderDefinition> configuredProviders, string? activeProviderName, string? activeModelName)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            await LoadWorkspaceSettingsAsync(CancellationToken.None).ConfigureAwait(true);
            await ApplyProviderConfigurationAsync(configuredProviders, activeProviderName, activeModelName, CancellationToken.None).ConfigureAwait(true);
            UpdateStatus(_selectedProviderName, _selectedModelName);
        }

        public void UpdateStatus(string? providerName, string? modelName)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => UpdateStatus(providerName, modelName));
                return;
            }

            StatusLabel.Text = string.IsNullOrWhiteSpace(providerName)
                ? "OpenCopilot · No provider configured"
                : $"OpenCopilot · {providerName} / {modelName}";
        }

        private async void CopilotChatWindowControl_Loaded(object sender, RoutedEventArgs e)
        {
            if (_package == null || _llmService == null)
                return;

            await RefreshProviderConfigurationAsync(_package.GetConfiguredProviders(), _llmService.ActiveProvider?.Name, _selectedModelName).ConfigureAwait(true);
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

        private async void SendButton_Click(object sender, RoutedEventArgs e)
        {
            await SendMessageAsync().ConfigureAwait(true);
        }

        private async void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control && e.Key == Key.V && Clipboard.ContainsImage())
            {
                e.Handled = true;
                await AddClipboardImageAttachmentAsync(CancellationToken.None).ConfigureAwait(true);
                return;
            }

            if (e.Key == Key.Enter && !Keyboard.IsKeyDown(Key.LeftShift) && !Keyboard.IsKeyDown(Key.RightShift))
            {
                e.Handled = true;
                await SendMessageAsync().ConfigureAwait(true);
            }
        }

        private async void NewChatButton_Click(object sender, RoutedEventArgs e)
        {
            await StartNewChatAsync(CancellationToken.None).ConfigureAwait(true);
        }

        private async void RenameSessionButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentSession == null || _sessionStore == null)
                return;

            var newTitle = SessionSelector.Text?.Trim();
            if (string.IsNullOrWhiteSpace(newTitle))
                return;

            var snapshot = CreateMessageSnapshot();
            await _sessionStore.RenameSessionAsync(_currentSession, newTitle, snapshot, CancellationToken.None).ConfigureAwait(true);
            SessionSelector.SelectedItem = _currentSession;
        }

        private async void DeleteSessionButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentSession == null || _sessionStore == null)
                return;

            var result = MessageBox.Show(
                $"确定删除会话“{_currentSession.Title}”吗？",
                "删除历史会话",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
                return;

            var removedSession = _currentSession;
            await _sessionStore.DeleteSessionAsync(removedSession, CancellationToken.None).ConfigureAwait(true);

            _sessions.Remove(removedSession);
            _messages.Clear();
            _attachments.Clear();
            _currentSession = null;
            SetIdlePlan();

            if (_sessions.Count > 0)
                await LoadSessionAsync(_sessions[0], CancellationToken.None).ConfigureAwait(true);
            else
                SessionSelector.Text = string.Empty;
        }

        private async void SessionSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isSwitchingSession)
                return;

            if (SessionSelector.SelectedItem is ChatSessionInfo session)
                await LoadSessionAsync(session, CancellationToken.None).ConfigureAwait(true);
        }

        private async void ModeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingSelectors)
                return;

            if (ModeSelector.SelectedItem is not ChatInteractionModeOption option)
                return;

            _selectedInteractionMode = option.Mode;
            _workspaceSettings.InteractionMode = _selectedInteractionMode;
            await PersistWorkspaceSettingsAsync(CancellationToken.None).ConfigureAwait(true);
        }

        private async void ProviderSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingSelectors || _llmService == null)
                return;

            var providerName = ProviderSelector.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(providerName))
                return;

            _selectedProviderName = providerName;
            _workspaceSettings.ProviderName = providerName;
            _workspaceSettings.ModelName = null;
            _llmService.TrySetActiveProvider(providerName);
            await LoadModelsForProviderAsync(providerName, null, CancellationToken.None).ConfigureAwait(true);
            UpdateStatus(_selectedProviderName, _selectedModelName);
            await PersistWorkspaceSettingsAsync(CancellationToken.None).ConfigureAwait(true);
            await NotifyProviderSelectionChangedAsync().ConfigureAwait(true);
        }

        private async void ModelSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingSelectors)
                return;

            _selectedModelName = ModelSelector.SelectedItem as string ?? ModelSelector.Text?.Trim();
            _workspaceSettings.ModelName = _selectedModelName;
            UpdateStatus(_selectedProviderName, _selectedModelName);
            await PersistWorkspaceSettingsAsync(CancellationToken.None).ConfigureAwait(true);
            await NotifyProviderSelectionChangedAsync().ConfigureAwait(true);
        }

        private async void OptionsButton_Click(object sender, RoutedEventArgs e)
        {
            if (_package == null)
                return;

            await _package.ShowOptionsPageAsync().ConfigureAwait(true);

            if (_llmService != null)
                await RefreshProviderConfigurationAsync(_package.GetConfiguredProviders(), _llmService.ActiveProvider?.Name, _selectedModelName).ConfigureAwait(true);
        }

        private async void AttachActiveDocumentButton_Click(object sender, RoutedEventArgs e)
        {
            await AddActiveDocumentAttachmentAsync(CancellationToken.None).ConfigureAwait(true);
        }

        private async void AttachFileButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Multiselect = true,
                CheckFileExists = true,
                Title = "选择要附加的文件"
            };

            if (dialog.ShowDialog() != true)
                return;

            foreach (var fileName in dialog.FileNames)
                await AddFileAttachmentAsync(fileName, "关联文件", CancellationToken.None).ConfigureAwait(true);
        }

        private void RemoveAttachmentButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.CommandParameter is string displayName)
            {
                var attachment = _attachments.FirstOrDefault(item => string.Equals(item.DisplayName, displayName, StringComparison.Ordinal));
                if (attachment != null)
                    _attachments.Remove(attachment);
            }
        }

        private void RetainChangedFileButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.CommandParameter is not string relativePath)
                return;

            _retainedChangedFiles.Add(relativePath);
            var item = _changedFiles.FirstOrDefault(file => string.Equals(file.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));
            if (item != null)
                item.IsRetained = true;

            ChangedFilesList.Items.Refresh();
        }

        private async void DiscardChangedFileButton_Click(object sender, RoutedEventArgs e)
        {
            if (_gitTracker == null || (sender as Button)?.CommandParameter is not string relativePath)
                return;

            var item = _changedFiles.FirstOrDefault(file => string.Equals(file.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));
            if (item == null)
                return;

            await _gitTracker.DiscardAsync(item, CancellationToken.None).ConfigureAwait(true);
            _retainedChangedFiles.Remove(relativePath);
            await RefreshChangedFilesAsync(CancellationToken.None).ConfigureAwait(true);
        }

        private async void OpenDiffButton_Click(object sender, RoutedEventArgs e)
        {
            if (_gitTracker == null || string.IsNullOrWhiteSpace(_solutionDirectory) || (sender as Button)?.CommandParameter is not string relativePath)
                return;

            var currentPath = Path.Combine(_solutionDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(currentPath))
                return;

            var headText = await _gitTracker.GetHeadFileTextAsync(relativePath, CancellationToken.None).ConfigureAwait(true) ?? string.Empty;
            var baselinePath = Path.Combine(Path.GetTempPath(), $"OpenCopilot-{Guid.NewGuid():N}-{Path.GetFileName(currentPath)}");
            await Task.Run(() => File.WriteAllText(baselinePath, headText), CancellationToken.None).ConfigureAwait(true);

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
            dte?.ExecuteCommand("Tools.DiffFiles", $"\"{baselinePath}\" \"{currentPath}\"");
        }

        private async void DiscardHunkButton_Click(object sender, RoutedEventArgs e)
        {
            if (_gitTracker == null || (sender as Button)?.CommandParameter is not string relativePath)
                return;

            var hunks = await _gitTracker.LoadHunksAsync(relativePath, CancellationToken.None).ConfigureAwait(true);
            if (hunks.Count == 0)
                return;

            var selectedHunk = hunks.Count == 1 ? hunks[0] : SelectHunk(hunks);
            if (selectedHunk == null)
                return;

            await _gitTracker.DiscardHunkAsync(relativePath, selectedHunk, CancellationToken.None).ConfigureAwait(true);
            _retainedChangedFiles.Remove(relativePath);
            await RefreshChangedFilesAsync(CancellationToken.None).ConfigureAwait(true);
        }

        private async Task SendMessageAsync()
        {
            var userText = InputBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(userText) || _llmService == null)
                return;
            if (string.IsNullOrWhiteSpace(_selectedProviderName))
            {
                _messages.Add(new ChatMessage("OpenCopilot", "⚠ 请先在设置中补全至少一个服务商配置。"));
                ChatScrollViewer.ScrollToBottom();
                return;
            }

            _llmService.TrySetActiveProvider(_selectedProviderName);

            var history = BuildHistory();
            var promptMessage = BuildPromptMessage(userText);
            var displayMessage = BuildDisplayMessage(userText);

            InputBox.Clear();
            InputBox.IsEnabled = false;
            SendButton.IsEnabled = false;
            TypingIndicator.Visibility = Visibility.Visible;
            SetPendingPlan(userText);

            _messages.Add(new ChatMessage("You", displayMessage));
            ChatScrollViewer.ScrollToBottom();

            await PersistCurrentSessionAsync(CancellationToken.None).ConfigureAwait(true);
            await UpdateFallbackSessionTitleAsync(CancellationToken.None).ConfigureAwait(true);

            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            _activeToolSummarySteps.Clear();

            try
            {
                var response = _selectedInteractionMode == ChatInteractionMode.Agent
                    ? await RunAgentTurnAsync(history, promptMessage, _cts.Token).ConfigureAwait(false)
                    : await _llmService.CompleteAsync(CreateChatRequest(history, promptMessage), _cts.Token).ConfigureAwait(false);

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                TypingIndicator.Visibility = Visibility.Collapsed;
                if (response.IsSuccess)
                    _messages.Add(new ChatMessage("OpenCopilot", response.Content));
                else
                    _messages.Add(new ChatMessage("OpenCopilot", $"⚠ {response.ErrorMessage}"));

                ChatScrollViewer.ScrollToBottom();

                await PersistCurrentSessionAsync(CancellationToken.None).ConfigureAwait(true);
                await TryGenerateSessionTitleAsync(CancellationToken.None).ConfigureAwait(true);
                _attachments.Clear();
                await RefreshChangedFilesAsync(CancellationToken.None).ConfigureAwait(true);
                if (response.IsSuccess && _selectedInteractionMode == ChatInteractionMode.Agent)
                    await ExtractPlanFromResponseAsync(response.Content, CancellationToken.None).ConfigureAwait(true);
                else
                    SetIdlePlan();
            }
            catch (OperationCanceledException)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                TypingIndicator.Visibility = Visibility.Collapsed;
                _messages.Add(new ChatMessage("OpenCopilot", "Request cancelled."));
                await PersistCurrentSessionAsync(CancellationToken.None).ConfigureAwait(true);
                SetIdlePlan();
            }
            catch (InvalidOperationException ex)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                TypingIndicator.Visibility = Visibility.Collapsed;
                _messages.Add(new ChatMessage("OpenCopilot", $"⚠ {ex.Message}"));
                await PersistCurrentSessionAsync(CancellationToken.None).ConfigureAwait(true);
                SetIdlePlan();
            }
            catch (IOException ex)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                TypingIndicator.Visibility = Visibility.Collapsed;
                _messages.Add(new ChatMessage("OpenCopilot", $"⚠ {ex.Message}"));
                await PersistCurrentSessionAsync(CancellationToken.None).ConfigureAwait(true);
                SetIdlePlan();
            }
            finally
            {
                _activeToolSummarySteps.Clear();
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                InputBox.IsEnabled = true;
                SendButton.IsEnabled = true;
                InputBox.Focus();
            }
        }

        private async Task ApplyProviderConfigurationAsync(IReadOnlyList<ConfiguredProviderDefinition> configuredProviders, string? activeProviderName, string? activeModelName, CancellationToken cancellationToken)
        {
            _providers.Clear();
            _models.Clear();
            _preferredModelsByProvider.Clear();
            if (_llmService == null)
                return;

            foreach (var provider in configuredProviders)
            {
                if (string.IsNullOrWhiteSpace(provider.Name))
                    continue;

                _providers.Add(provider.Name);
                _preferredModelsByProvider[provider.Name] = provider.PreferredModel;
            }

            var selectedProvider = _providers.FirstOrDefault(provider => string.Equals(provider, _workspaceSettings.ProviderName, StringComparison.OrdinalIgnoreCase))
                ?? _providers.FirstOrDefault();

            ProviderSelector.Items.Refresh();

            _isUpdatingSelectors = true;
            try
            {
                ApplyInteractionModeSelection(_workspaceSettings.InteractionMode);
                ProviderSelector.SelectedItem = selectedProvider;
                if (ProviderSelector.SelectedItem == null && _providers.Count > 0)
                    ProviderSelector.SelectedIndex = 0;

                ProviderSelector.IsEnabled = _providers.Count > 0;
                ModelSelector.IsEnabled = _providers.Count > 0;
                _selectedProviderName = ProviderSelector.SelectedItem as string ?? selectedProvider;
                if (!string.IsNullOrWhiteSpace(selectedProvider))
                {
                    _llmService.TrySetActiveProvider(_selectedProviderName);
                    var initialModelName = GetInitialModelName(selectedProvider, activeProviderName, activeModelName);
                    await LoadModelsForProviderAsync(_selectedProviderName, initialModelName, cancellationToken).ConfigureAwait(true);
                    ProviderSelector.Text = _selectedProviderName;
                }
                else
                {
                    ProviderSelector.Text = string.Empty;
                    ModelSelector.SelectedItem = null;
                    ModelSelector.Text = string.Empty;
                    _selectedModelName = null;
                }

                ProviderSelector.UpdateLayout();
                ModelSelector.UpdateLayout();
            }
            finally
            {
                _isUpdatingSelectors = false;
            }

            await PersistWorkspaceSettingsAsync(cancellationToken).ConfigureAwait(true);
        }

        private async Task LoadModelsForProviderAsync(string providerName, string? preferredModel, CancellationToken cancellationToken)
        {
            _models.Clear();
            if (_llmService == null || string.IsNullOrWhiteSpace(providerName))
                return;

            var provider = _llmService.Providers.FirstOrDefault(item => string.Equals(item.Name, providerName, StringComparison.OrdinalIgnoreCase));
            if (provider == null)
                return;

            var availableModels = await provider.GetAvailableModelsAsync(cancellationToken).ConfigureAwait(true);
            foreach (var model in availableModels.Where(model => !string.IsNullOrWhiteSpace(model)).Distinct(StringComparer.OrdinalIgnoreCase))
                _models.Add(model);

            var effectivePreferredModel = !string.IsNullOrWhiteSpace(preferredModel)
                ? preferredModel
                : GetPreferredModelForProvider(providerName);

            var discoveredModelsAvailable = _models.Count > 0;
            var modelName = _models.FirstOrDefault(model => string.Equals(model, effectivePreferredModel, StringComparison.OrdinalIgnoreCase))
                ?? _models.FirstOrDefault()
                ?? effectivePreferredModel
                ?? string.Empty;

            if (!discoveredModelsAvailable && !string.IsNullOrWhiteSpace(modelName) && !_models.Contains(modelName))
                _models.Add(modelName);

            ModelSelector.Items.Refresh();

            ModelSelector.SelectedItem = modelName;
            if (ModelSelector.SelectedItem == null && _models.Count > 0)
                ModelSelector.SelectedIndex = 0;

            ModelSelector.Text = ModelSelector.SelectedItem as string ?? modelName;
            _selectedModelName = modelName;
            _workspaceSettings.ProviderName = providerName;
            _workspaceSettings.ModelName = modelName;
        }

        private string? GetPreferredModelForProvider(string providerName)
        {
            if (string.IsNullOrWhiteSpace(providerName))
                return null;

            return _preferredModelsByProvider.TryGetValue(providerName, out var preferredModel)
                ? preferredModel
                : null;
        }

        private string? GetInitialModelName(string providerName, string? activeProviderName, string? activeModelName)
        {
            if (string.Equals(providerName, _workspaceSettings.ProviderName, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(_workspaceSettings.ModelName))
            {
                return _workspaceSettings.ModelName;
            }

            return string.Equals(providerName, activeProviderName, StringComparison.OrdinalIgnoreCase)
                ? activeModelName
                : null;
        }

        private async Task LoadWorkspaceSettingsAsync(CancellationToken cancellationToken)
        {
            _workspaceSettings = _workspaceSettingsStore == null
                ? new ChatWorkspaceSettings()
                : await _workspaceSettingsStore.LoadAsync(cancellationToken).ConfigureAwait(true);

            _selectedInteractionMode = _workspaceSettings.InteractionMode;
            ApplyInteractionModeSelection(_selectedInteractionMode);
        }

        private void ApplyInteractionModeSelection(ChatInteractionMode interactionMode)
        {
            _selectedInteractionMode = interactionMode;
            var selectedOption = _interactionModes.FirstOrDefault(option => option.Mode == interactionMode)
                ?? _interactionModes.FirstOrDefault(option => option.Mode == ChatInteractionMode.Agent)
                ?? _interactionModes.FirstOrDefault();

            ModeSelector.SelectedItem = selectedOption;
            ModeSelector.IsEnabled = selectedOption != null;
        }

        private async Task PersistWorkspaceSettingsAsync(CancellationToken cancellationToken)
        {
            if (_workspaceSettingsStore == null)
                return;

            _workspaceSettings.ProviderName = _selectedProviderName;
            _workspaceSettings.ModelName = _selectedModelName;
            _workspaceSettings.InteractionMode = _selectedInteractionMode;
            await _workspaceSettingsStore.SaveAsync(_workspaceSettings, cancellationToken).ConfigureAwait(true);
        }

        private LlmRequest CreateChatRequest(IReadOnlyList<LlmMessage> history, string userMessage)
        {
            var messages = new List<LlmMessage>(history) { LlmMessage.User(userMessage) };
            return CreateChatRequestFromMessages(messages, BuildSystemPrompt());
        }

        private LlmRequest CreateChatRequestFromMessages(IReadOnlyList<LlmMessage> messages, string systemPrompt)
        {
            return new LlmRequest
            {
                Model = _selectedModelName,
                SystemPrompt = systemPrompt,
                Messages = new List<LlmMessage>(messages),
                Temperature = _selectedInteractionMode == ChatInteractionMode.Ask ? 0.4 : 0.2,
                MaxTokens = 2048
            };
        }

        private string BuildSystemPrompt()
        {
            return _selectedInteractionMode == ChatInteractionMode.Ask
                ? "You are OpenCopilot in consultation mode. Focus on asking clarifying questions, evaluating options, explaining trade-offs, and having a technical dialogue. Do not assume the user wants code changes unless they explicitly ask for code examples."
                : "You are OpenCopilot in agent mode. Help with programming tasks such as implementation planning, code generation, bug fixing, refactoring, and concrete code changes. Prefer actionable engineering guidance."
                ;
        }

        private async Task<LlmResponse> RunAgentTurnAsync(IReadOnlyList<LlmMessage> history, string promptMessage, CancellationToken cancellationToken)
        {
            if (_llmService == null || _package == null)
                throw new InvalidOperationException("Agent mode requires initialized services.");

            var mcpService = await _package.GetServiceAsync(typeof(McpService)).ConfigureAwait(true) as McpService;
            if (mcpService == null)
                return await _llmService.CompleteAsync(CreateChatRequest(history, promptMessage), cancellationToken).ConfigureAwait(false);

            var tools = await mcpService.GetAllToolsAsync(cancellationToken).ConfigureAwait(false);
            if (tools.Count == 0)
                return await _llmService.CompleteAsync(CreateChatRequest(history, promptMessage), cancellationToken).ConfigureAwait(false);

            var agentMessages = new List<LlmMessage>(history) { LlmMessage.User(promptMessage) };
            var systemPrompt = BuildAgentSystemPrompt(tools);
            var primingPrompts = await PrimeAgentWorkspaceContextAsync(mcpService, cancellationToken).ConfigureAwait(false);
            var hasWrittenToIde = false;
            if (primingPrompts.Count > 0)
                agentMessages.Add(LlmMessage.User(string.Join("\n\n", primingPrompts)));

            for (var iteration = 0; iteration < MaxAgentIterations; iteration++)
            {
                var response = await _llmService.CompleteAsync(CreateChatRequestFromMessages(agentMessages, systemPrompt), cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccess)
                    return response;

                var directive = AgentDirectiveParser.Parse(response.Content);
                if (directive.ToolCalls.Count == 0)
                {
                    if (AgentTaskClassifier.ShouldEnforceIdeWrite(promptMessage, hasWrittenToIde))
                    {
                        agentMessages.Add(LlmMessage.Assistant(response.Content));
                        agentMessages.Add(LlmMessage.User("You have not written the requested code changes into the IDE yet. For implementation, bug-fix, refactor, or edit tasks, you must apply the change through MCP IDE editing tools before returning the final answer. Continue with JSON tool calls only."));
                        continue;
                    }

                    return LlmResponse.Success(directive.FinalMessage, response.FinishReason, response.PromptTokens, response.CompletionTokens);
                }

                agentMessages.Add(LlmMessage.Assistant(response.Content));

                var toolPrompts = new List<string>();
                var hasFileMutations = false;
                foreach (var toolCall in directive.ToolCalls)
                {
                    var toolResult = await ExecuteAgentToolCallAsync(mcpService, toolCall, cancellationToken).ConfigureAwait(false);
                    toolPrompts.Add(BuildToolResultPrompt(toolCall.ToolName, toolResult));
                    hasFileMutations |= IsMutatingTool(toolCall.ToolName);
                }

                hasWrittenToIde |= hasFileMutations;

                if (hasFileMutations)
                {
                    var buildResult = await ExecuteAgentToolCallAsync(mcpService, AgentToolCallModel.Empty("vs_build_solution"), cancellationToken).ConfigureAwait(false);
                    toolPrompts.Add(BuildToolResultPrompt("vs_build_solution", buildResult));

                    if (buildResult.IsError)
                    {
                        var errorResult = await ExecuteAgentToolCallAsync(mcpService, AgentToolCallModel.Empty("vs_get_build_errors"), cancellationToken).ConfigureAwait(false);
                        toolPrompts.Add(BuildToolResultPrompt("vs_get_build_errors", errorResult));
                    }
                    else
                    {
                        var testResult = await ExecuteAgentToolCallAsync(mcpService, AgentToolCallModel.Empty("vs_run_tests"), cancellationToken).ConfigureAwait(false);
                        toolPrompts.Add(BuildToolResultPrompt("vs_run_tests", testResult));
                    }
                }

                agentMessages.Add(LlmMessage.User(string.Join("\n\n", toolPrompts)));
            }

            return LlmResponse.Failure("Agent reached the maximum tool iteration count before producing a final answer.");
        }

        private string BuildAgentSystemPrompt(IReadOnlyList<McpTool> tools)
        {
            var builder = new StringBuilder();
            builder.AppendLine(BuildSystemPrompt());
            builder.AppendLine();
            builder.AppendLine("You can inspect and modify the open Visual Studio solution by calling IDE tools.");
            builder.AppendLine("When you need tools, respond with ONLY a single JSON object using one of these shapes:");
            builder.AppendLine("{\"type\":\"tool_call\",\"tool\":\"tool_name\",\"arguments\":{...},\"reason\":\"short reason\"}");
            builder.AppendLine("{\"type\":\"tool_calls\",\"calls\":[{\"tool\":\"tool_name\",\"arguments\":{...}}]}");
            builder.AppendLine("When you are done, respond with ONLY a single JSON object using this shape:");
            builder.AppendLine("{\"type\":\"final\",\"message\":\"user-facing answer\"}");
            builder.AppendLine("You may request multiple tools in one response when the order is clear. Do not invent tool results.");
            builder.AppendLine("vs_get_ide_context has already been called for this turn. Before any real code changes, you must have that IDE context available and use it as your starting point.");
            builder.AppendLine("For coding tasks, start by calling vs_get_ide_context so you know the current solution, current project, active document, language, and runtime.");
            builder.AppendLine("Before editing code, read the relevant project file and source files with MCP tools, then analyze the language and runtime using the current solution, current project, and current active document together.");
            builder.AppendLine("Current project first: keep your investigation and edits inside the current project unless the user explicitly asks for broader changes or the dependency chain proves another project must change.");
            builder.AppendLine("Current active document first: when the active document is related to the task, inspect it before searching elsewhere and treat it as the primary file for local context.");
            builder.AppendLine("Repository AI instruction or memory files are high priority. Early in the task, inspect files such as .github/copilot-instructions.md, AGENTS.md, CLAUDE.md, GEMINI.md, CODEX.md, OPENCODE.md, OPENCLAW.md, and similar files when they exist, then follow them.");
            builder.AppendLine("If the task requires code changes, you must write the change into the IDE by using MCP file-editing tools such as vs_apply_patch, vs_edit_file, vs_replace_file_content, and vs_create_file before you return a final answer.");
            builder.AppendLine("Do not stop at analysis, advice, or code blocks. Do not ask the user to paste code manually when you can edit through the IDE tools.");
            builder.AppendLine("For implementation, bug-fix, refactor, rename, delete, create-file, or patch tasks, a final answer is valid only after the code has been applied inside the IDE.");
            builder.AppendLine("After any file modifications, expect the system to run a solution build automatically, return build errors when the build fails, and run unit tests automatically when the build succeeds.");
            builder.AppendLine("Use vs_read_file with line ranges, character windows, target-line context windows, or anchorText windows for focused inspection when possible.");
            builder.AppendLine("Use vs_list_project_files after vs_get_ide_context when you need to understand the current solution or project layout before selecting files to read.");
            builder.AppendLine("vs_run_tests will try to narrow execution to affected test projects and infer useful FullyQualifiedName filters from changed source files, including common method-based `Should_`, `When_`, `Given_`, and `Given_When_Then` naming patterns, when no explicit path or filter is provided.");
            builder.AppendLine("Prefer patch-level edits with vs_apply_patch when you need multiple hunks or multiple files; it supports SEARCH/REPLACE blocks, fuller unified diff headers, copy/rename headers, mode-change metadata, and @@ hunks, applies file modes on non-Windows when possible, and rejects duplicate, directory-level, or unsafe target paths.");
            builder.AppendLine();
            builder.AppendLine("Available tools:");

            foreach (var tool in tools)
            {
                builder.AppendLine(tool.ToPromptDescription());
                builder.AppendLine();
            }

            return builder.ToString().TrimEnd();
        }

        private async Task<List<string>> PrimeAgentWorkspaceContextAsync(McpService mcpService, CancellationToken cancellationToken)
        {
            var prompts = new List<string>();

            var ideContextResult = await ExecuteAgentToolCallAsync(mcpService, AgentToolCallModel.Empty("vs_get_ide_context"), cancellationToken).ConfigureAwait(false);
            prompts.Add(BuildToolResultPrompt("vs_get_ide_context", ideContextResult));

            var filesResult = await ExecuteAgentToolCallAsync(mcpService, AgentToolCallModel.Empty("vs_list_project_files"), cancellationToken).ConfigureAwait(false);
            prompts.Add(BuildToolResultPrompt("vs_list_project_files", filesResult));

            if (filesResult.IsError)
                return prompts;

            var knownInstructionFiles = AiInstructionFileLocator.FindRelevantFiles(_solutionDirectory, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
                .Take(6)
                .ToArray();

            foreach (var instructionFile in knownInstructionFiles)
            {
                var readInstructionResult = await ExecuteAgentToolCallAsync(
                        mcpService,
                        new AgentToolCallModel(
                            "vs_read_file",
                            JObject.FromObject(new { path = instructionFile }),
                            "read repository AI instruction file"),
                        cancellationToken)
                    .ConfigureAwait(false);

                prompts.Add(BuildToolResultPrompt("vs_read_file", readInstructionResult));
            }

            return prompts;
        }

        private async Task<McpToolCallResult> ExecuteAgentToolCallAsync(McpService mcpService, AgentToolCallModel toolCall, CancellationToken cancellationToken)
        {
            var arguments = toolCall.Arguments?.Properties().ToDictionary(
                property => property.Name,
                property => property.Value.ToObject<object>(),
                StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            var result = await mcpService.CallToolAsync(toolCall.ToolName, arguments, cancellationToken).ConfigureAwait(false);

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            AppendToolExecutionSummary(ChatToolActivityFormatter.FormatStep(toolCall, result));
            ChatScrollViewer.ScrollToBottom();
            return result;
        }

        private void AppendToolExecutionSummary(string stepSummary)
        {
            if (string.IsNullOrWhiteSpace(stepSummary))
                return;

            _activeToolSummarySteps.Add(stepSummary);
            var summaryCard = ChatToolActivityFormatter.FormatExecutionSummary(_activeToolSummarySteps);

            if (_messages.Count > 0 && string.Equals(_messages[_messages.Count - 1].Sender, "Tool", StringComparison.Ordinal))
                _messages[_messages.Count - 1] = new ChatMessage("Tool", summaryCard);
            else
                _messages.Add(new ChatMessage("Tool", summaryCard));
        }

        private static string BuildToolResultPrompt(string toolName, McpToolCallResult result)
        {
            var builder = new StringBuilder();
            builder.AppendLine($"Tool result for {toolName}:");
            if (result.IsError)
                builder.AppendLine("ERROR: " + (result.ErrorMessage ?? "Unknown tool error."));
            else
                builder.AppendLine(result.GetTextContent());

            builder.AppendLine();
            builder.AppendLine("Continue by either requesting the next tool with JSON or returning the final answer JSON.");
            return builder.ToString().TrimEnd();
        }

        private static bool IsMutatingTool(string toolName)
        {
            return string.Equals(toolName, "vs_create_file", StringComparison.OrdinalIgnoreCase)
                || string.Equals(toolName, "vs_edit_file", StringComparison.OrdinalIgnoreCase)
                || string.Equals(toolName, "vs_replace_file_content", StringComparison.OrdinalIgnoreCase)
                || string.Equals(toolName, "vs_reencode_file", StringComparison.OrdinalIgnoreCase)
                || string.Equals(toolName, "vs_apply_patch", StringComparison.OrdinalIgnoreCase);
        }

        private async Task NotifyProviderSelectionChangedAsync()
        {
            if (_package == null)
                return;

            await _package.NotifyProviderSelectionChangedAsync(_selectedProviderName, _selectedModelName).ConfigureAwait(true);
        }

        private async Task InitializeSessionsAsync(string? solutionDirectory)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            _sessionStore = null;
            _sessions.Clear();
            _messages.Clear();
            _attachments.Clear();
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
            _attachments.Clear();
            SetIdlePlan();

            if (_sessionStore == null)
            {
                _currentSession = null;
                SessionSelector.SelectedItem = null;
                SessionSelector.Text = string.Empty;
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
                _attachments.Clear();
                foreach (var message in messages)
                    _messages.Add(message);

                SessionSelector.SelectedItem = session;
                SessionSelector.Text = session.Title;
                SetIdlePlan();
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
                    SessionSelector.Text = _currentSession.Title;
                }
                finally
                {
                    _isSwitchingSession = false;
                }
            }

            var snapshot = CreateMessageSnapshot();
            await _sessionStore.SaveSessionAsync(_currentSession, snapshot, cancellationToken).ConfigureAwait(true);
            MoveSessionToTop(_currentSession);
        }

        private List<ChatMessage> CreateMessageSnapshot()
            => _messages.Select(message => new ChatMessage(message.Sender, message.Content)).ToList();

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
            SessionSelector.Text = fallbackTitle;
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

            var request = new LlmRequest
            {
                Model = _selectedModelName,
                SystemPrompt = "Generate a concise title for this conversation. Return a single plain-text title with no markdown or wrapping punctuation.",
                Messages = new List<LlmMessage>
                {
                    LlmMessage.User($"Create a short title (max 12 words) for this conversation:\n\n{transcript}")
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
            SessionSelector.Text = _currentSession.Title;
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

        private async Task AddActiveDocumentAttachmentAsync(CancellationToken cancellationToken)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
            var document = dte?.ActiveDocument;
            if (document == null || string.IsNullOrWhiteSpace(document.FullName))
                return;

            string content;
            try
            {
                if (document.Object("TextDocument") is EnvDTE.TextDocument textDocument)
                {
                    var start = textDocument.StartPoint.CreateEditPoint();
                    content = start.GetText(textDocument.EndPoint);
                }
                else
                {
                    content = await ReadFileTextAsync(document.FullName, cancellationToken).ConfigureAwait(true);
                }
            }
            catch (ArgumentException)
            {
                content = await ReadFileTextAsync(document.FullName, cancellationToken).ConfigureAwait(true);
            }

            AddAttachment(new ChatAttachment(
                "活动文档 · " + Path.GetFileName(document.FullName),
                BuildFileAttachmentPrompt("活动文档", document.FullName, content),
                "document",
                document.FullName));
        }

        private async Task AddFileAttachmentAsync(string filePath, string label, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return;

            var content = await ReadFileTextAsync(filePath, cancellationToken).ConfigureAwait(true);
            AddAttachment(new ChatAttachment(
                $"{label} · {Path.GetFileName(filePath)}",
                BuildFileAttachmentPrompt(label, filePath, content),
                "file",
                filePath));
        }

        private async Task AddClipboardImageAttachmentAsync(CancellationToken cancellationToken)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var image = Clipboard.GetImage();
            if (image == null)
                return;

            var outputPath = _sessionStore != null
                ? _sessionStore.CreateAttachmentFilePath(".png")
                : Path.Combine(Path.GetTempPath(), $"OpenCopilot-{DateTime.UtcNow:yyyyMMdd-HHmmss}.png");

            await Task.Run(() => SaveBitmap(image, outputPath), cancellationToken).ConfigureAwait(true);
            AddAttachment(new ChatAttachment(
                "剪贴板图片 · " + Path.GetFileName(outputPath),
                $"[剪贴板图片]\n已保存图片文件: {outputPath}\n当前模型如果不支持图像输入，请根据文件名或请用户手动查看该图片。",
                "image",
                outputPath));
        }

        private void AddAttachment(ChatAttachment attachment)
        {
            if (_attachments.Any(existing =>
                    !string.IsNullOrWhiteSpace(existing.SourcePath)
                    && string.Equals(existing.SourcePath, attachment.SourcePath, StringComparison.OrdinalIgnoreCase)))
                return;

            _attachments.Add(attachment);
        }

        private async Task<string> ReadFileTextAsync(string filePath, CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var content = File.ReadAllText(filePath);
                return TrimLargeText(content);
            }, cancellationToken).ConfigureAwait(false);
        }

        private string BuildPromptMessage(string userText)
        {
            if (_attachments.Count == 0)
                return userText;

            var builder = new StringBuilder();
            builder.AppendLine(userText);
            builder.AppendLine();
            builder.AppendLine("Attached context:");
            builder.AppendLine();

            foreach (var attachment in _attachments)
            {
                builder.AppendLine(attachment.PromptContent);
                builder.AppendLine();
            }

            return builder.ToString().TrimEnd();
        }

        private string BuildDisplayMessage(string userText)
        {
            if (_attachments.Count == 0)
                return userText;

            var builder = new StringBuilder();
            builder.AppendLine("[附件]");
            foreach (var attachment in _attachments)
                builder.AppendLine("- " + attachment.DisplayName);
            builder.AppendLine();
            builder.Append(userText);
            return builder.ToString();
        }

        private string BuildFileAttachmentPrompt(string label, string filePath, string content)
        {
            var language = Path.GetExtension(filePath)?.TrimStart('.');
            var builder = new StringBuilder();
            builder.AppendLine("[" + label + "]");
            builder.AppendLine("Path: " + filePath);
            builder.AppendLine("```" + language);
            builder.AppendLine(content);
            builder.AppendLine("```");
            return builder.ToString();
        }

        private static void SaveBitmap(BitmapSource bitmap, string outputPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? Path.GetTempPath());
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(outputPath))
                encoder.Save(stream);
        }

        private async Task RefreshChangedFilesAsync(CancellationToken cancellationToken)
        {
            if (_gitTracker == null)
            {
                _changedFiles.Clear();
                return;
            }

            var files = await _gitTracker.LoadChangesAsync(_retainedChangedFiles, cancellationToken).ConfigureAwait(true);
            _changedFiles.Clear();
            foreach (var file in files)
                _changedFiles.Add(file);
        }

        private void SetIdlePlan()
        {
            _planItems.Clear();
            _planItems.Add(new ChatPlanItem("等待新的聊天任务", "pending"));
            _planItems.Add(new ChatPlanItem("附加上下文或文件后发送请求", "pending"));
        }

        private void SetPendingPlan(string userText)
        {
            var summary = BuildFallbackTitle(userText);
            _planItems.Clear();
            _planItems.Add(new ChatPlanItem("分析请求：" + summary, "completed"));
            _planItems.Add(new ChatPlanItem("生成回复与操作建议", "in-progress"));
            _planItems.Add(new ChatPlanItem("根据回复提炼下一步计划", "pending"));
        }

        private async Task ExtractPlanFromResponseAsync(string responseContent, CancellationToken cancellationToken)
        {
            if (_llmService == null || string.IsNullOrWhiteSpace(responseContent))
            {
                SetIdlePlan();
                return;
            }

            var request = new LlmRequest
            {
                Model = _selectedModelName,
                SystemPrompt = "Read the assistant reply and extract the concrete next-step plan. Output 2 to 5 lines. Each line must be in the format status|title where status is one of completed,pending,in-progress. Keep titles short and actionable.",
                Messages = new List<LlmMessage>
                {
                    LlmMessage.User(responseContent)
                },
                Temperature = 0.1,
                MaxTokens = 160
            };

            var response = await _llmService.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
            var items = response.IsSuccess
                ? ParsePlanItems(response.Content)
                : Array.Empty<ChatPlanItem>();

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            _planItems.Clear();
            foreach (var item in items.Length > 0 ? items : BuildFallbackPlanItems(responseContent))
                _planItems.Add(item);

            PlanList.Items.Refresh();
        }

        private static ChatPlanItem[] ParsePlanItems(string? rawContent)
        {
            if (string.IsNullOrWhiteSpace(rawContent))
                return Array.Empty<ChatPlanItem>();

            var items = new List<ChatPlanItem>();
            foreach (var rawLine in rawContent.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                var line = rawLine.Trim().TrimStart('-', '*', ' ');
                var separatorIndex = line.IndexOf('|');
                if (separatorIndex <= 0 || separatorIndex >= line.Length - 1)
                    continue;

                var status = line.Substring(0, separatorIndex).Trim();
                var title = line.Substring(separatorIndex + 1).Trim();
                if (string.IsNullOrWhiteSpace(title))
                    continue;

                items.Add(new ChatPlanItem(title, NormalizePlanStatus(status)));
            }

            return items.Take(5).ToArray();
        }

        private static ChatPlanItem[] BuildFallbackPlanItems(string responseContent)
        {
            var candidates = responseContent
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => line.TrimStart('-', '*', ' ', '1', '2', '3', '4', '5', '6', '7', '8', '9', '.', ')'))
                .Where(line => line.Length > 0)
                .Take(4)
                .Select(line => new ChatPlanItem(BuildFallbackTitle(line), "pending"))
                .ToList();

            if (candidates.Count == 0)
                candidates.Add(new ChatPlanItem("已返回回复内容", "completed"));

            return candidates.ToArray();
        }

        private static string NormalizePlanStatus(string status)
        {
            return status.Trim().ToLowerInvariant() switch
            {
                "completed" => "completed",
                "done" => "completed",
                "in-progress" => "in-progress",
                "progress" => "in-progress",
                _ => "pending",
            };
        }

        private GitDiffHunk? SelectHunk(IReadOnlyList<GitDiffHunk> hunks)
        {
            if (hunks == null || hunks.Count == 0)
                return null;

            var listBox = new ListBox
            {
                ItemsSource = hunks,
                SelectedIndex = 0,
                Margin = new Thickness(0, 0, 0, 12),
                MinWidth = 520,
                MinHeight = 220,
            };

            var okButton = new Button { Content = "撤销选中块", MinWidth = 96, IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
            var cancelButton = new Button { Content = "取消", MinWidth = 80, IsCancel = true };

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Children = { okButton, cancelButton }
            };

            var panel = new DockPanel { Margin = new Thickness(12) };
            DockPanel.SetDock(buttons, Dock.Bottom);
            panel.Children.Add(buttons);
            panel.Children.Add(listBox);

            var dialog = new Window
            {
                Title = "选择要撤销的差异块",
                Content = panel,
                SizeToContent = SizeToContent.WidthAndHeight,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.CanResizeWithGrip,
                Owner = Window.GetWindow(this),
            };

            okButton.Click += (_, _) => dialog.DialogResult = true;
            return dialog.ShowDialog() == true ? listBox.SelectedItem as GitDiffHunk : null;
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

        private static string TrimLargeText(string content)
        {
            var normalized = content ?? string.Empty;
            return normalized.Length <= MaxAttachmentCharacters
                ? normalized
                : normalized.Substring(0, MaxAttachmentCharacters) + "\n...\n[truncated]";
        }

        private List<LlmMessage> BuildHistory()
        {
            var history = new List<LlmMessage>();
            var start = Math.Max(0, _messages.Count - MaxHistoryMessages);
            for (var i = start; i < _messages.Count; i++)
            {
                var message = _messages[i];
                if (message.Sender == "You")
                    history.Add(LlmMessage.User(message.Content));
                else if (message.Sender == "OpenCopilot")
                    history.Add(LlmMessage.Assistant(message.Content));
            }

            return history;
        }
    }

    /// <summary>View model for a single chat bubble.</summary>
    public class ChatMessage
    {
        public ChatMessage(string sender, string content)
        {
            Sender = sender;
            Content = content;
        }

        public string Sender { get; }

        public string Content { get; }

        public bool IsUser => Sender == "You";

        public HorizontalAlignment SenderAlignment => IsUser ? HorizontalAlignment.Right : HorizontalAlignment.Left;

        public HorizontalAlignment BubbleAlignment => IsUser ? HorizontalAlignment.Right : HorizontalAlignment.Left;

        public Brush BubbleBackground => IsUser
            ? ResolveBrush(SystemColors.HighlightBrushKey, SystemColors.HighlightBrush)
            : ResolveBrush(SystemColors.ControlBrushKey, SystemColors.ControlBrush);

        public CornerRadius BubbleCornerRadius => IsUser ? new CornerRadius(8, 8, 2, 8) : new CornerRadius(8, 8, 8, 2);

        public Thickness BubbleMargin => IsUser ? new Thickness(40, 4, 8, 4) : new Thickness(8, 4, 40, 4);

        public Brush TextForeground => IsUser
            ? ResolveBrush(SystemColors.HighlightTextBrushKey, SystemColors.HighlightTextBrush)
            : ResolveBrush(VsBrushes.ToolWindowTextKey, SystemColors.ControlTextBrush);

        public FontFamily TextFont => IsUser ? SystemFonts.MessageFontFamily : new FontFamily("Consolas, Courier New, monospace");

        private static Brush ResolveBrush(object resourceKey, Brush fallback)
            => Application.Current?.TryFindResource(resourceKey) as Brush ?? fallback;
    }
}
