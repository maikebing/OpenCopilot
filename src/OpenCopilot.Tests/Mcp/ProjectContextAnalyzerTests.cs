using OpenCopilot.Mcp;
using Xunit;

namespace OpenCopilot.Tests.Mcp
{
    public class ProjectContextAnalyzerTests
    {
        [Fact]
        public void Analyze_ReturnsCSharpProjectLanguage_WhenProjectIsCsproj()
        {
            var analysis = ProjectContextAnalyzer.Analyze(
                "src/OpenCopilot/OpenCopilot.csproj",
                "<Project><PropertyGroup><TargetFramework>net48</TargetFramework></PropertyGroup></Project>",
                "src/OpenCopilot/ToolWindows/CopilotChatWindowControl.xaml.cs");

            Assert.Equal("C#", analysis.ProjectLanguage);
        }

        [Fact]
        public void Analyze_ReturnsRuntime_WhenTargetFrameworksArePresent()
        {
            var analysis = ProjectContextAnalyzer.Analyze(
                "src/OpenCopilot.Core/OpenCopilot.Core.csproj",
                "<Project><PropertyGroup><TargetFrameworks>net8.0;net48</TargetFrameworks></PropertyGroup></Project>",
                "src/OpenCopilot.Core/Mcp/IdeToolLogic.cs");

            Assert.Equal("net8.0, net48", analysis.Runtime);
        }

        [Fact]
        public void Analyze_ReturnsActiveDocumentLanguage_WhenDocumentIsXamlCodeBehind()
        {
            var analysis = ProjectContextAnalyzer.Analyze(
                "src/OpenCopilot/OpenCopilot.csproj",
                "<Project><PropertyGroup><TargetFramework>net48</TargetFramework></PropertyGroup></Project>",
                "src/OpenCopilot/ToolWindows/CopilotChatWindowControl.xaml.cs");

            Assert.Equal("C#", analysis.DocumentLanguage);
        }
    }
}
