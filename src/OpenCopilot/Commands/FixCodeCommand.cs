using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using OpenCopilot.Providers;
using OpenCopilot.Services;
using OpenCopilot.ToolWindows;

namespace OpenCopilot.Commands
{
    /// <summary>
    /// Sends the selected code to the LLM to identify and fix issues.
    /// </summary>
    internal sealed class FixCodeCommand
    {
        public const int CommandId = 0x0102;
        public static readonly Guid CommandSet = new Guid("A1B2C3D4-E5F6-7890-ABCD-EF1234567891");

        private readonly AsyncPackage _package;
        private readonly LlmService _llmService;

        private FixCodeCommand(AsyncPackage package, LlmService llmService)
        {
            _package = package ?? throw new ArgumentNullException(nameof(package));
            _llmService = llmService ?? throw new ArgumentNullException(nameof(llmService));
        }

        public static FixCodeCommand? Instance { get; private set; }

        public static async Task InitializeAsync(AsyncPackage package, LlmService llmService)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            var commandService = await package.GetServiceAsync(typeof(System.ComponentModel.Design.IMenuCommandService))
                as OleMenuCommandService;

            Instance = new FixCodeCommand(package, llmService);

            var menuCommandID = new System.ComponentModel.Design.CommandID(CommandSet, CommandId);
            var menuItem = new OleMenuCommand(Instance.Execute, menuCommandID);
            menuItem.BeforeQueryStatus += Instance.OnBeforeQueryStatus;
            commandService?.AddCommand(menuItem);
        }

        private void OnBeforeQueryStatus(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (sender is OleMenuCommand cmd)
                cmd.Enabled = HasSelection();
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

            if (string.IsNullOrWhiteSpace(selectedText)) return;

            var request = new LlmRequest
            {
                SystemPrompt =
                    "You are an expert code reviewer and debugger. " +
                    "Identify bugs, issues, and improvements in the provided code. " +
                    "Return the fixed code followed by a brief explanation of the changes.",
                Messages = new List<LlmMessage>
                {
                    LlmMessage.User($"Please find and fix any issues in the following code:\n\n```\n{selectedText}\n```")
                },
                Temperature = 0.2,
                MaxTokens = 2048
            };

            var response = await _llmService.CompleteAsync(request).ConfigureAwait(false);

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var window = _package.FindToolWindow(typeof(CopilotChatWindow), 0, true) as CopilotChatWindow;
            if (window?.Frame is IVsWindowFrame frame)
                frame.Show();

            if (window?.Content is CopilotChatWindowControl control)
            {
                control.AddMessage("You", $"**Fix Code**\n\n```\n{selectedText}\n```");
                if (response.IsSuccess)
                    control.AddMessage("OpenCopilot", response.Content);
                else
                    control.AddMessage("OpenCopilot", $"Error: {response.ErrorMessage}");
            }
        }

        private static bool HasSelection()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return !string.IsNullOrWhiteSpace(GetSelectedText());
        }

        private static string GetSelectedText()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var dte = (EnvDTE.DTE)Microsoft.VisualStudio.Shell.ServiceProvider.GlobalProvider.GetService(typeof(EnvDTE.DTE));
            return dte?.ActiveDocument?.Selection is EnvDTE.TextSelection sel ? sel.Text : string.Empty;
        }
    }
}
