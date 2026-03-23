using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using OpenCopilot.Commands;
using OpenCopilot.Completion;
using OpenCopilot.Mcp;
using OpenCopilot.Options;
using OpenCopilot.Providers;
using OpenCopilot.Services;
using OpenCopilot.Skills;
using OpenCopilot.ToolWindows;
using Task = System.Threading.Tasks.Task;

namespace OpenCopilot
{
    /// <summary>
    /// Main VSIX package. Initialises all services, registers providers, and wires up commands.
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(PackageGuidString)]
    [InstalledProductRegistration("#110", "#112", "1.0")]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideToolWindow(
        typeof(CopilotChatWindow),
        Style = VsDockStyle.Tabbed,
        Window = "DocumentWell",
        Orientation = ToolWindowOrientation.Right)]
    [ProvideOptionPage(
        typeof(OpenCopilotOptionsPage),
        "OpenCopilot",
        "General",
        0, 0,
        true)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.NoSolution_string, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExists_string, PackageAutoLoadFlags.BackgroundLoad)]
    public sealed class OpenCopilotPackage : AsyncPackage
    {
        public const string PackageGuidString = "A1B2C3D4-E5F6-7890-ABCD-EF1234567890";

        private LlmService _llmService;
        private McpService _mcpService;
        private BingWebSearchTool? _bingSearchTool;
        private OpenCopilotOptionsPage _optionsPage;

        #region Package initialisation

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await base.InitializeAsync(cancellationToken, progress);

            // GetDialogPage and several VS services require UI thread access.
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            // Load the options page
            _optionsPage = (OpenCopilotOptionsPage)GetDialogPage(typeof(OpenCopilotOptionsPage));
            var options = _optionsPage.GetOptions();

            // Build and populate the LLM service
            _llmService = new LlmService();
            RegisterProviders(options);
            ApplyActiveProvider(options);

            // Expose the service so other components can request it
            AddService(typeof(LlmService), (container, cancellation, type) => Task.FromResult<object>(_llmService), promote: true);

            // Set up MCP service with built-in VS tools and discovered servers
            _mcpService = new McpService();
            new VsFileEditingTools(this).RegisterInto(_mcpService);

            // Built-in web search tool (Bing)
            var proxyUrlForSearch = options.UseProxy ? options.ProxyUrl : null;
            _bingSearchTool = new BingWebSearchTool(options.BingSearchApiKey, proxyUrlForSearch);
            if (options.McpEnableWebSearch)
                _bingSearchTool.RegisterInto(_mcpService);

            // Apply disabled-server scope from settings
            _mcpService.SetDisabledServers(ParseDisabledServers(options.DisabledMcpServers));

            var discovery = new McpDiscovery();
            var solutionDirectory = await GetSolutionDirectoryAsync(cancellationToken);
            var mcpConfigs = discovery.Discover(solutionDirectory);
            foreach (var config in mcpConfigs)
            {
                if (config.Transport == McpTransport.Stdio && !string.IsNullOrWhiteSpace(config.Command))
                    _mcpService.AddClient(new McpStdioClient(config));
            }

            AddService(typeof(McpService), (container, cancellation, type) => Task.FromResult<object>(_mcpService), promote: true);
            _ = ConnectMcpClientsAsync();

            // Make the service and options available to the MEF completion source
            CompletionSourceProvider.LlmServiceInstance = _llmService;
            CompletionSourceProvider.OptionsPageInstance = _optionsPage;

            // Register commands on the UI thread
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            await ExplainCodeCommand.InitializeAsync(this, _llmService);
            await GenerateCodeCommand.InitializeAsync(this, _llmService);
            await FixCodeCommand.InitializeAsync(this, _llmService);
            await OpenChatWindowCommand.InitializeAsync(this);

            // Update status bar
            if (options.ShowStatusBarInfo)
                await UpdateStatusBarAsync(options);

            await RefreshChatWindowProviderConfigurationAsync(options);

            // Wire up options-changed notification
            _optionsPage.SettingsChanged += OnSettingsChanged;
        }

        #endregion

        #region Provider registration

        private void RegisterProviders(OpenCopilotOptions options)
        {
            var proxyUrl = options.UseProxy ? options.ProxyUrl : null;

            _llmService.RegisterProvider(
                new OpenAIProvider(options.OpenAIApiKey, options.OpenAIModel, options.OpenAIBaseUrl, proxyUrl));

            _llmService.RegisterProvider(
                new DeepSeekProvider(options.DeepSeekApiKey, options.DeepSeekModel, proxyUrl));

            _llmService.RegisterProvider(
                new DoubaoProvider(options.DoubaoApiKey, options.DoubaoModel, proxyUrl));

            // Local providers: pass proxyUrl too; bypassOnLocal=true in HttpClientFactory
            // means localhost traffic is never sent through the proxy regardless.
            _llmService.RegisterProvider(
                new OllamaProvider(options.OllamaBaseUrl, options.OllamaModel, proxyUrl));

            _llmService.RegisterProvider(
                new DockerDesktopAIProvider(options.DockerDesktopAIModel, options.DockerDesktopAIBaseUrl, proxyUrl));
        }

        private void ApplyActiveProvider(OpenCopilotOptions options)
        {
            var configuredProviders = GetConfiguredProviders(options);
            if (configuredProviders.Count > 0 && _llmService.TrySetActiveProvider(configuredProviders[0].Name))
                return;

            if (_llmService.Providers.Count > 0)
                _llmService.ActiveProvider = _llmService.Providers[0];
        }

        internal IReadOnlyList<ConfiguredProviderDefinition> GetConfiguredProviders()
            => GetConfiguredProviders(_optionsPage.GetOptions());

        internal async Task ShowOptionsPageAsync()
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
            ShowOptionPage(typeof(OpenCopilotOptionsPage));
        }

        internal async Task NotifyProviderSelectionChangedAsync(string? providerName, string? modelName)
        {
            var options = _optionsPage.GetOptions();
            if (!string.IsNullOrWhiteSpace(providerName))
                _llmService.TrySetActiveProvider(providerName);

            if (options.ShowStatusBarInfo)
                await UpdateStatusBarAsync(options, providerName, modelName).ConfigureAwait(false);
        }

        #endregion

        #region Tool window

        /// <summary>Shows the OpenCopilot Chat tool window.</summary>
        public async Task ShowChatWindowAsync()
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
            var window = await FindToolWindowAsync(typeof(CopilotChatWindow), 0, true, DisposalToken) as CopilotChatWindow;

            if (window?.Frame is IVsWindowFrame frame)
            {
                ErrorHandler.ThrowOnFailure(frame.Show());

                if (window.Content is CopilotChatWindowControl control)
                {
                    var options = _optionsPage.GetOptions();
                    var solutionDirectory = await GetSolutionDirectoryAsync(DisposalToken);
                    await control.InitializeAsync(this, _llmService, GetConfiguredProviders(options), _llmService.ActiveProvider?.Name, GetActiveModel(options), solutionDirectory);
                }
            }
        }

        #endregion

        #region Settings change

        private void OnSettingsChanged(object sender, EventArgs e)
        {
            var options = _optionsPage.GetOptions();

            // Update each provider's settings
            UpdateProviderSettings(options);
            ApplyActiveProvider(options);

            // Bing web search tool — update key/proxy; re-register if newly enabled
            if (_bingSearchTool != null)
            {
                var proxyUrl = options.UseProxy ? options.ProxyUrl : null;
                _bingSearchTool.UpdateSettings(options.BingSearchApiKey, proxyUrl);
                // Re-register only if enabled and not already registered
                // (re-registration is idempotent: RegisterBuiltInTool overwrites)
                if (options.McpEnableWebSearch)
                    _bingSearchTool.RegisterInto(_mcpService);
            }

            // Apply updated disabled-server scope
            _mcpService.SetDisabledServers(ParseDisabledServers(options.DisabledMcpServers));

            if (options.ShowStatusBarInfo)
                _ = UpdateStatusBarAsync(options);

            _ = RefreshChatWindowProviderConfigurationAsync(options);
        }

        private void UpdateProviderSettings(OpenCopilotOptions options)
        {
            var proxyUrl = options.UseProxy ? options.ProxyUrl : null;

            if (_llmService.GetProvider("OpenAI") is OpenAIProvider openai)
                openai.UpdateSettings(options.OpenAIApiKey, options.OpenAIModel, options.OpenAIBaseUrl, proxyUrl);

            if (_llmService.GetProvider("DeepSeek") is DeepSeekProvider deepseek)
                deepseek.UpdateSettings(options.DeepSeekApiKey, options.DeepSeekModel, "https://api.deepseek.com/v1", proxyUrl);

            if (_llmService.GetProvider("Doubao") is DoubaoProvider doubao)
                doubao.UpdateSettings(options.DoubaoApiKey, options.DoubaoModel, "https://ark.cn-beijing.volces.com/api/v3", proxyUrl);

            if (_llmService.GetProvider("Ollama") is OllamaProvider ollama)
                ollama.UpdateSettings(options.OllamaBaseUrl, options.OllamaModel, proxyUrl);

            if (_llmService.GetProvider("Docker Desktop AI") is DockerDesktopAIProvider docker)
                docker.UpdateSettings(string.Empty, options.DockerDesktopAIModel, options.DockerDesktopAIBaseUrl, proxyUrl);
        }

        #endregion

        #region MCP

        private static IEnumerable<string> ParseDisabledServers(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                yield break;
            foreach (var part in raw.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var name = part.Trim();
                if (!string.IsNullOrWhiteSpace(name))
                    yield return name;
            }
        }

        private async Task<string?> GetSolutionDirectoryAsync(CancellationToken cancellationToken)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            var dte = await GetServiceAsync(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
            var solutionPath = dte?.Solution?.FullName;
            return string.IsNullOrWhiteSpace(solutionPath)
                ? null
                : System.IO.Path.GetDirectoryName(solutionPath);
        }

        private async Task ConnectMcpClientsAsync()
        {
            foreach (var client in _mcpService.Clients)
            {
                try
                {
                    await client.ConnectAsync(DisposalToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    ActivityLog.LogWarning(
                        nameof(OpenCopilotPackage),
                        $"Failed to connect MCP client '{client.ServerName}': {ex.Message}");
                }
            }

            _mcpService.InvalidateToolCache();
        }

        #endregion

        #region Status bar

        private async Task UpdateStatusBarAsync(OpenCopilotOptions options)
            => await UpdateStatusBarAsync(options, _llmService.ActiveProvider?.Name, GetActiveModel(options)).ConfigureAwait(false);

        private async Task UpdateStatusBarAsync(OpenCopilotOptions options, string? providerName, string? modelName)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
            var statusBar = await GetServiceAsync(typeof(SVsStatusbar)) as IVsStatusbar;
            if (statusBar == null) return;

            statusBar.IsFrozen(out var frozen);
            if (frozen != 0) statusBar.FreezeOutput(0);

            var text = !string.IsNullOrWhiteSpace(providerName)
                ? $"OpenCopilot: {providerName} / {modelName}"
                : "OpenCopilot: inactive";

            statusBar.SetText(text);
        }

        private string GetActiveModel(OpenCopilotOptions options)
        {
            var activeProviderName = _llmService.ActiveProvider?.Name;
            var configuredModel = ProviderConfigurationCatalog.GetPreferredModel(options, activeProviderName ?? string.Empty);
            return !string.IsNullOrWhiteSpace(configuredModel)
                ? configuredModel
                : "unknown";
        }

        private IReadOnlyList<ConfiguredProviderDefinition> GetConfiguredProviders(OpenCopilotOptions options)
            => new ReadOnlyCollection<ConfiguredProviderDefinition>(new List<ConfiguredProviderDefinition>(ProviderConfigurationCatalog.GetConfiguredProviders(options, _llmService.Providers)));

        private async Task RefreshChatWindowProviderConfigurationAsync(OpenCopilotOptions options)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
            var window = await FindToolWindowAsync(typeof(CopilotChatWindow), 0, false, DisposalToken) as CopilotChatWindow;
            if (window?.Content is CopilotChatWindowControl control)
            {
                var configuredProviders = GetConfiguredProviders(options);
                var activeProviderName = _llmService.ActiveProvider?.Name;
                var activeModelName = GetActiveModel(options);
                var solutionDirectory = await GetSolutionDirectoryAsync(DisposalToken);

                if (!control.IsInitialized)
                    await control.InitializeAsync(this, _llmService, configuredProviders, activeProviderName, activeModelName, solutionDirectory).ConfigureAwait(true);
                else
                    await control.RefreshProviderConfigurationAsync(configuredProviders, activeProviderName, activeModelName).ConfigureAwait(true);
            }
        }

        #endregion
    }
}
