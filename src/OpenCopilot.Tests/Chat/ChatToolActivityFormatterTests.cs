using Newtonsoft.Json.Linq;
using OpenCopilot.Chat;
using OpenCopilot.Mcp;
using Xunit;

namespace OpenCopilot.Tests.Chat
{
    public class ChatToolActivityFormatterTests
    {
        [Fact]
        public void FormatStep_UsesReadIconAndOmitsFileContent()
        {
            var toolCall = new AgentToolCall(
                "vs_read_file",
                JObject.FromObject(new { path = "src/Program.cs", startLine = 10, endLine = 20 }),
                "inspect entry point");
            var result = McpToolCallResult.Success("line 10\nsecret implementation body\nline 20");

            var summary = ChatToolActivityFormatter.FormatStep(toolCall, result);

            Assert.Contains("📖", summary);
            Assert.Contains("inspect entry point", summary);
            Assert.Contains("src/Program.cs", summary);
            Assert.DoesNotContain("secret implementation body", summary);
            Assert.DoesNotContain("\n", summary);
        }

        [Fact]
        public void FormatStep_UsesEditIconForWriteOperation()
        {
            var toolCall = new AgentToolCall(
                "vs_edit_file",
                JObject.FromObject(new { path = "src/Program.cs" }),
                "update output text");
            var result = McpToolCallResult.Success("replaced 1 occurrence");

            var summary = ChatToolActivityFormatter.FormatStep(toolCall, result);

            Assert.Contains("✏️", summary);
            Assert.Contains("update output text", summary);
            Assert.Contains("src/Program.cs", summary);
        }

        [Fact]
        public void FormatExecutionSummary_MergesMultipleStepsIntoSingleLineCard()
        {
            var summary = ChatToolActivityFormatter.FormatExecutionSummary(new[]
            {
                "📖 inspect entry point（src/Program.cs:10-20）",
                "✏️ update output text（src/Program.cs）",
                "🧪 run tests"
            });

            Assert.StartsWith("执行过程摘要：", summary);
            Assert.Contains("📖 inspect entry point", summary);
            Assert.Contains("✏️ update output text", summary);
            Assert.Contains("🧪 run tests", summary);
            Assert.DoesNotContain("\n", summary);
        }
    }
}
