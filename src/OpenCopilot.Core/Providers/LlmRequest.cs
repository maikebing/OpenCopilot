using System.Collections.Generic;

namespace OpenCopilot.Providers
{
    public class LlmRequest
    {
        public string Model { get; set; }
        public List<LlmMessage> Messages { get; set; } = new List<LlmMessage>();
        public double Temperature { get; set; } = 0.7;
        public int MaxTokens { get; set; } = 2048;
        public bool Stream { get; set; } = false;
        public string SystemPrompt { get; set; }
    }

    public class LlmMessage
    {
        public string Role { get; set; }
        public string Content { get; set; }

        public static LlmMessage User(string content) => new LlmMessage { Role = "user", Content = content };
        public static LlmMessage Assistant(string content) => new LlmMessage { Role = "assistant", Content = content };
        public static LlmMessage System(string content) => new LlmMessage { Role = "system", Content = content };
    }
}
