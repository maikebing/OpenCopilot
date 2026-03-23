using System.Collections.Generic;

namespace OpenCopilot.Mcp
{
    public enum McpTransport { Stdio, Http }

    public class McpServerConfig
    {
        public string Name { get; set; } = string.Empty;
        public McpTransport Transport { get; set; } = McpTransport.Stdio;
        // Stdio
        public string Command { get; set; } = string.Empty;
        public List<string> Args { get; set; } = new List<string>();
        public Dictionary<string, string> Env { get; set; } = new Dictionary<string, string>();
        // HTTP
        public string Url { get; set; } = string.Empty;
        // Optional: override displayed name
        public string? DisplayName { get; set; }
        public bool IsBuiltIn { get; set; } = false;
        /// <summary>
        /// When <see langword="false"/> the server is discovered but not connected and its
        /// tools are excluded from the active tool set. Users control this through the
        /// Tools &gt; Options &gt; OpenCopilot &gt; MCP Scope setting.
        /// </summary>
        public bool IsEnabled { get; set; } = true;
    }
}
