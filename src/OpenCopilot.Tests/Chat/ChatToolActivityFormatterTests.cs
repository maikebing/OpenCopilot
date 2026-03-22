using Newtonsoft.Json.Linq;
using OpenCopilot.Chat;
using OpenCopilot.Mcp;
using Xunit;

namespace OpenCopilot.Tests.Chat
{
    public class ChatToolActivityFormatterTests
    {
        [Fact]
        public void Format_SummarizesReadFileWithoutIncludingFileContent()
        {
            var toolCall = new AgentToolCall(
                "vs_read_file",
                JObject.FromObject(new { path = "src/Program.cs", startLine = 10, endLine = 20 }),
                "inspect entry point");
            var result = McpToolCallResult.Success("line 10\nsecret implementation body\nline 20");

            var summary = ChatToolActivityFormatter.Format(toolCall, result);

            Assert.Contains("读取文件", summary);
            Assert.Contains("src/Program.cs", summary);
            Assert.DoesNotContain("secret implementation body", summary);
        }

        [Fact]
        public void Format_SummarizesWriteOperationWithPath()
        {
            var toolCall = new AgentToolCall(
                "vs_edit_file",
                JObject.FromObject(new { path = "src/Program.cs" }),
                "update output text");
            var result = McpToolCallResult.Success("replaced 1 occurrence");

            var summary = ChatToolActivityFormatter.Format(toolCall, result);

            Assert.Contains("编辑文件", summary);
            Assert.Contains("目标：src/Program.cs", summary);
            Assert.Contains("步骤：update output text", summary);
        }
    }
}
