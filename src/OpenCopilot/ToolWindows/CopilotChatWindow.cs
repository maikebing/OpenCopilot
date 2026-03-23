using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace OpenCopilot.ToolWindows
{
    /// <summary>
    /// The OpenCopilot Chat tool window pane. Hosts <see cref="CopilotChatWindowControl"/>.
    /// </summary>
    [Guid("C3D4E5F6-A7B8-9012-CDEF-012345678902")]
    public class CopilotChatWindow : ToolWindowPane
    {
        public CopilotChatWindow() : base(null)
        {
            Caption = "OpenCopilot Chat";
            Content = new CopilotChatWindowControl();
        }
    }
}
