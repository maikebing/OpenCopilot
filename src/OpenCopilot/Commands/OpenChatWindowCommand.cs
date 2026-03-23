using System;
using System.ComponentModel.Design;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;

namespace OpenCopilot.Commands
{
    /// <summary>
    /// Opens the OpenCopilot chat tool window.
    /// </summary>
    internal sealed class OpenChatWindowCommand
    {
        public const int CommandId = 0x0103;
        public static readonly Guid CommandSet = new Guid("A1B2C3D4-E5F6-7890-ABCD-EF1234567891");

        private readonly AsyncPackage _package;

        private OpenChatWindowCommand(AsyncPackage package)
        {
            _package = package ?? throw new ArgumentNullException(nameof(package));
        }

        public static OpenChatWindowCommand? Instance { get; private set; }

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (commandService == null)
                return;

            Instance = new OpenChatWindowCommand(package);
            var menuCommandID = new CommandID(CommandSet, CommandId);
            var menuItem = new OleMenuCommand(Instance.Execute, menuCommandID);
            commandService.AddCommand(menuItem);
        }

        private void Execute(object sender, EventArgs e)
        {
            _ = ExecuteAsync();
        }

        private async Task ExecuteAsync()
        {
            if (_package is OpenCopilotPackage package)
                await package.ShowChatWindowAsync();
        }
    }
}
