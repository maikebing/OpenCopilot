using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using OpenCopilot.Providers;
using OpenCopilot.Services;
using OpenCopilot.ToolWindows;

namespace OpenCopilot.Commands
{
    /// <summary>
    /// Explains the currently selected code using the active LLM provider.
    /// </summary>
    internal sealed class ExplainCodeCommand
    {
        public const int CommandId = 0x0100;
        public static readonly Guid CommandSet = new Guid("A1B2C3D4-E5F6-7890-ABCD-EF1234567891");

        private readonly AsyncPackage _package;
        private readonly LlmService _llmService;

        private ExplainCodeCommand(AsyncPackage package, LlmService llmService)
        {
            _package = package ?? throw new ArgumentNullException(nameof(package));
            _llmService = llmService ?? throw new ArgumentNullException(nameof(llmService));
        }

        public static ExplainCodeCommand? Instance { get; private set; }

        public static async Task InitializeAsync(AsyncPackage package, LlmService llmService)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            var commandService = await package.GetServiceAsync(typeof(System.ComponentModel.Design.IMenuCommandService))
                as OleMenuCommandService;

            Instance = new ExplainCodeCommand(package, llmService);

            var menuCommandID = new System.ComponentModel.Design.CommandID(CommandSet, CommandId);
            var menuItem = new OleMenuCommand(Instance.Execute, menuCommandID);
            menuItem.BeforeQueryStatus += Instance.OnBeforeQueryStatus;
            commandService?.AddCommand(menuItem);
        }

        private void OnBeforeQueryStatus(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (sender is OleMenuCommand cmd)
                cmd.Enabled = !string.IsNullOrWhiteSpace(GetSelectedText());
        }

        private void Execute(object sender, EventArgs e)
        {
            _ = ExecuteAsync();
        }

        private async Task ExecuteAsync()
        {
            string selectedText;
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            selectedText = GetSelectedText();

            if (string.IsNullOrWhiteSpace(selectedText))
                return;

            var request = new LlmRequest
            {
                SystemPrompt = "You are an expert code reviewer. Explain the provided code clearly and concisely.",
                Messages = new System.Collections.Generic.List<LlmMessage>
                {
                    LlmMessage.User($"Please explain the following code:\n\n```\n{selectedText}\n```")
                },
                Temperature = 0.3,
                MaxTokens = 1024
            };

            var response = await _llmService.CompleteAsync(request).ConfigureAwait(false);

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            await ShowInChatWindowAsync(
                $"**Explain Code**\n\n```\n{selectedText}\n```",
                response);
        }

        private async Task ShowInChatWindowAsync(string userMessage, LlmResponse response)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var window = _package.FindToolWindow(typeof(CopilotChatWindow), 0, true) as CopilotChatWindow;
            if (window?.Frame is IVsWindowFrame frame)
                frame.Show();

            if (window?.Content is CopilotChatWindowControl control)
            {
                control.AddMessage("You", userMessage);
                if (response.IsSuccess)
                    control.AddMessage("OpenCopilot", response.Content);
                else
                    control.AddMessage("OpenCopilot", $"Error: {response.ErrorMessage}");
            }
        }

        private static string GetSelectedText()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var dte = (EnvDTE.DTE)Microsoft.VisualStudio.Shell.ServiceProvider.GlobalProvider.GetService(typeof(EnvDTE.DTE));
            return dte?.ActiveDocument?.Selection is EnvDTE.TextSelection sel ? sel.Text : string.Empty;
        }
    }
}
