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
    /// Prompts the user for a description and generates code using the active LLM provider.
    /// </summary>
    internal sealed class GenerateCodeCommand
    {
        public const int CommandId = 0x0101;
        public static readonly Guid CommandSet = new Guid("A1B2C3D4-E5F6-7890-ABCD-EF1234567891");

        private readonly AsyncPackage _package;
        private readonly LlmService _llmService;

        private GenerateCodeCommand(AsyncPackage package, LlmService llmService)
        {
            _package = package ?? throw new ArgumentNullException(nameof(package));
            _llmService = llmService ?? throw new ArgumentNullException(nameof(llmService));
        }

        public static GenerateCodeCommand? Instance { get; private set; }

        public static async Task InitializeAsync(AsyncPackage package, LlmService llmService)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            var commandService = await package.GetServiceAsync(typeof(System.ComponentModel.Design.IMenuCommandService))
                as OleMenuCommandService;

            Instance = new GenerateCodeCommand(package, llmService);

            var menuCommandID = new System.ComponentModel.Design.CommandID(CommandSet, CommandId);
            var menuItem = new OleMenuCommand(Instance.Execute, menuCommandID);
            commandService?.AddCommand(menuItem);
        }

        private void Execute(object sender, EventArgs e)
        {
            _ = ExecuteAsync();
        }

        private async Task ExecuteAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var prompt = ShowInputDialog("Generate Code", "Describe the code you want to generate:");
            if (string.IsNullOrWhiteSpace(prompt)) return;

            var language = DetectCurrentLanguage();

            var request = new LlmRequest
            {
                SystemPrompt = $"You are an expert {language} programmer. Generate clean, idiomatic {language} code based on the user's description. Output only the code with brief inline comments where helpful.",
                Messages = new List<LlmMessage>
                {
                    LlmMessage.User($"Generate {language} code for: {prompt}")
                },
                Temperature = 0.3,
                MaxTokens = 2048
            };

            var response = await _llmService.CompleteAsync(request).ConfigureAwait(false);

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var window = _package.FindToolWindow(typeof(CopilotChatWindow), 0, true) as CopilotChatWindow;
            if (window?.Frame is IVsWindowFrame frame)
                frame.Show();

            if (window?.Content is CopilotChatWindowControl control)
            {
                control.AddMessage("You", $"Generate {language} code for: {prompt}");
                if (response.IsSuccess)
                    control.AddMessage("OpenCopilot", response.Content);
                else
                    control.AddMessage("OpenCopilot", $"Error: {response.ErrorMessage}");
            }
        }

        private string DetectCurrentLanguage()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var dte = (EnvDTE.DTE)Microsoft.VisualStudio.Shell.ServiceProvider.GlobalProvider.GetService(typeof(EnvDTE.DTE));
            var doc = dte?.ActiveDocument;
            if (doc == null) return "code";

            return System.IO.Path.GetExtension(doc.Name)?.ToLowerInvariant() switch
            {
                ".cs" => "C#",
                ".py" => "Python",
                ".ts" => "TypeScript",
                ".js" => "JavaScript",
                ".cpp" or ".cc" or ".cxx" => "C++",
                ".java" => "Java",
                ".go" => "Go",
                ".rs" => "Rust",
                _ => "code"
            };
        }

        private static string? ShowInputDialog(string title, string prompt)
        {
            var form = new System.Windows.Forms.Form
            {
                Text = title,
                Width = 500,
                Height = 180,
                FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog,
                StartPosition = System.Windows.Forms.FormStartPosition.CenterParent,
                MaximizeBox = false,
                MinimizeBox = false
            };

            var label = new System.Windows.Forms.Label { Left = 12, Top = 15, Width = 460, Text = prompt };
            var input = new System.Windows.Forms.TextBox { Left = 12, Top = 40, Width = 460 };
            var ok = new System.Windows.Forms.Button { Text = "OK", Left = 310, Top = 80, Width = 80, DialogResult = System.Windows.Forms.DialogResult.OK };
            var cancel = new System.Windows.Forms.Button { Text = "Cancel", Left = 400, Top = 80, Width = 80, DialogResult = System.Windows.Forms.DialogResult.Cancel };

            form.Controls.AddRange(new System.Windows.Forms.Control[] { label, input, ok, cancel });
            form.AcceptButton = ok;
            form.CancelButton = cancel;

            return form.ShowDialog() == System.Windows.Forms.DialogResult.OK ? input.Text : null;
        }
    }
}
