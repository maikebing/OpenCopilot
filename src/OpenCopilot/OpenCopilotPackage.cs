using System;
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
    [InstalledProductRegistration(
        "#110",
        "#112",
        "1.0",
        IconResourceID = 400)]
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
        private OpenCopilotOptionsPage _optionsPage;

        #region Package initialisation

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await base.InitializeAsync(cancellationToken, progress);

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

            var discovery = new McpDiscovery();
            var mcpConfigs = discovery.Discover(GetSolutionDirectory());
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

            // Update status bar
            if (options.ShowStatusBarInfo)
                await UpdateStatusBarAsync(options);

            // Wire up options-changed notification
            _optionsPage.SettingsChanged += OnSettingsChanged;
        }

        #endregion

        #region Provider registration

        private void RegisterProviders(OpenCopilotOptions options)
        {
            _llmService.RegisterProvider(
                new OpenAIProvider(options.OpenAIApiKey, options.OpenAIModel, options.OpenAIBaseUrl));

            _llmService.RegisterProvider(
                new DeepSeekProvider(options.DeepSeekApiKey, options.DeepSeekModel));

            _llmService.RegisterProvider(
                new DoubaoProvider(options.DoubaoApiKey, options.DoubaoModel));

            _llmService.RegisterProvider(
                new OllamaProvider(options.OllamaBaseUrl, options.OllamaModel));

            _llmService.RegisterProvider(
                new DockerDesktopAIProvider(options.DockerDesktopAIModel, options.DockerDesktopAIBaseUrl));
        }

        private void ApplyActiveProvider(OpenCopilotOptions options)
        {
            if (!_llmService.TrySetActiveProvider(options.ActiveProvider))
            {
                // Fall back to the first registered provider
                if (_llmService.Providers.Count > 0)
                    _llmService.ActiveProvider = _llmService.Providers[0];
            }
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
                    var provider = _llmService.ActiveProvider;
                    control.Initialize(_llmService, provider?.Name, GetActiveModel());
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

            if (options.ShowStatusBarInfo)
                _ = UpdateStatusBarAsync(options);
        }

        private void UpdateProviderSettings(OpenCopilotOptions options)
        {
            if (_llmService.GetProvider("OpenAI") is OpenAIProvider openai)
                openai.UpdateSettings(options.OpenAIApiKey, options.OpenAIModel, options.OpenAIBaseUrl);

            if (_llmService.GetProvider("DeepSeek") is DeepSeekProvider deepseek)
                deepseek.UpdateSettings(options.DeepSeekApiKey, options.DeepSeekModel, "https://api.deepseek.com/v1");

            if (_llmService.GetProvider("Doubao") is DoubaoProvider doubao)
                doubao.UpdateSettings(options.DoubaoApiKey, options.DoubaoModel, "https://ark.cn-beijing.volces.com/api/v3");

            if (_llmService.GetProvider("Ollama") is OllamaProvider ollama)
                ollama.UpdateSettings(options.OllamaBaseUrl, options.OllamaModel);

            if (_llmService.GetProvider("Docker Desktop AI") is DockerDesktopAIProvider docker)
                docker.UpdateSettings(string.Empty, options.DockerDesktopAIModel, options.DockerDesktopAIBaseUrl);
        }

        #endregion

        #region MCP

        private string? GetSolutionDirectory()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var dte = GetService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
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
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
            var statusBar = await GetServiceAsync(typeof(SVsStatusbar)) as IVsStatusbar;
            if (statusBar == null) return;

            statusBar.IsFrozen(out var frozen);
            if (frozen != 0) statusBar.FreezeOutput(0);

            var provider = _llmService.ActiveProvider;
            var text = provider != null
                ? $"OpenCopilot: {provider.Name} / {GetActiveModel()}"
                : "OpenCopilot: inactive";

            statusBar.SetText(text);
        }

        private string GetActiveModel()
        {
            var options = _optionsPage.GetOptions();
            return _llmService.ActiveProvider?.Name switch
            {
                "OpenAI" => options.OpenAIModel,
                "DeepSeek" => options.DeepSeekModel,
                "Doubao" => options.DoubaoModel,
                "Ollama" => options.OllamaModel,
                "Docker Desktop AI" => options.DockerDesktopAIModel,
                _ => "unknown"
            };
        }

        #endregion
    }
}
