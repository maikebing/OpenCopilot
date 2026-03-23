using System.Collections.Generic;

namespace OpenCopilot.Mcp
{
    public class McpToolCallResult
    {
        public bool IsError { get; set; }
        public List<McpContent> Content { get; set; } = new List<McpContent>();
        public string? ErrorMessage { get; set; }

        public string GetTextContent()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var c in Content)
                if (c.Type == "text" && c.Text != null)
                    sb.AppendLine(c.Text);
            return sb.ToString().TrimEnd();
        }

        public static McpToolCallResult Success(string text)
            => new McpToolCallResult { Content = new List<McpContent> { new McpContent { Type = "text", Text = text } } };

        public static McpToolCallResult Failure(string error)
            => new McpToolCallResult { IsError = true, ErrorMessage = error };
    }

    public class McpContent
    {
        public string Type { get; set; } = "text";
        public string? Text { get; set; }
        public string? Data { get; set; }
        public string? MimeType { get; set; }
    }
}
