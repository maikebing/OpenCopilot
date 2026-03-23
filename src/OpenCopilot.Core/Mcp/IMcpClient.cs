using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenCopilot.Mcp
{
    public interface IMcpClient
    {
        string ServerName { get; }
        bool IsConnected { get; }
        Task<bool> ConnectAsync(CancellationToken cancellationToken = default);
        Task<List<McpTool>> ListToolsAsync(CancellationToken cancellationToken = default);
        Task<McpToolCallResult> CallToolAsync(string toolName, Dictionary<string, object?> arguments, CancellationToken cancellationToken = default);
        void Disconnect();
    }
}
