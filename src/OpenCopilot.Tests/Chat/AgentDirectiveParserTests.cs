using OpenCopilot.Chat;
using Xunit;

namespace OpenCopilot.Tests.Chat
{
    public class AgentDirectiveParserTests
    {
        [Fact]
        public void Parse_ReturnsFirstToolCall_WhenResponseContainsMultipleJsonObjects()
        {
            var response =
                "{\"type\":\"tool_call\",\"tool\":\"vs_list_project_files\",\"arguments\":{},\"reason\":\"inspect solution files\"}" +
                "{\"type\":\"tool_call\",\"tool\":\"vs_list_project_files\",\"arguments\":{},\"reason\":\"inspect solution files\"}" +
                "{\"type\":\"final\",\"message\":\"done\"}";

            var directive = AgentDirectiveParser.Parse(response);

            Assert.Single(directive.ToolCalls);
            Assert.Equal("vs_list_project_files", directive.ToolCalls[0].ToolName);
        }

        [Fact]
        public void Parse_ReturnsFinalMessage_WhenResponseContainsSingleFinalDirective()
        {
            var response = "{\"type\":\"final\",\"message\":\"可以开始\"}";

            var directive = AgentDirectiveParser.Parse(response);

            Assert.Empty(directive.ToolCalls);
            Assert.Equal("可以开始", directive.FinalMessage);
        }

        [Fact]
        public void Parse_HandlesCodeFenceWrappedDirective_WhenResponseContainsJsonFence()
        {
            var response = "```json\n{\"type\":\"tool_call\",\"tool\":\"vs_read_file\",\"arguments\":{\"path\":\"Program.cs\"}}\n```";

            var directive = AgentDirectiveParser.Parse(response);

            Assert.Single(directive.ToolCalls);
            Assert.Equal("vs_read_file", directive.ToolCalls[0].ToolName);
        }

        [Fact]
        public void Parse_PreservesReason_WhenToolCallContainsReason()
        {
            var response = "{\"type\":\"tool_call\",\"tool\":\"vs_read_file\",\"arguments\":{\"path\":\"Program.cs\"},\"reason\":\"inspect entry point\"}";

            var directive = AgentDirectiveParser.Parse(response);

            Assert.Equal("inspect entry point", directive.ToolCalls[0].Reason);
        }
    }
}
