using Newtonsoft.Json.Linq;

namespace OpenCopilot.Mcp
{
    public class McpTool
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public JObject? InputSchema { get; set; }
        public string ServerName { get; set; } = string.Empty;
        public bool IsBuiltIn { get; set; } = false;

        /// <summary>Returns a human-readable summary for the LLM.</summary>
        public string ToPromptDescription()
            => $"Tool: {Name} (from {ServerName})\nDescription: {Description}\n" +
               (InputSchema != null ? $"Parameters: {InputSchema}" : string.Empty);
    }
}
