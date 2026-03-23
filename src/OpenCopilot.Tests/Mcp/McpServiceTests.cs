using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using OpenCopilot.Mcp;
using Xunit;

namespace OpenCopilot.Tests.Mcp
{
    public class McpServiceTests
    {
        private static McpTool MakeTool(string name, string description = "desc", string server = "TestServer")
            => new McpTool { Name = name, Description = description, ServerName = server, IsBuiltIn = false };

        private static McpTool MakeBuiltInTool(string name, string description = "built-in desc")
            => new McpTool { Name = name, Description = description, ServerName = "BuiltIn", IsBuiltIn = true };

        [Fact]
        public async Task GetAllToolsAsync_ReturnsBuiltInTools()
        {
            var service = new McpService();
            service.RegisterBuiltInTool(MakeBuiltInTool("built_in_tool"),
                _ => Task.FromResult(McpToolCallResult.Success("ok")));

            var tools = await service.GetAllToolsAsync();

            Assert.Single(tools);
            Assert.Equal("built_in_tool", tools[0].Name);
        }

        [Fact]
        public async Task GetAllToolsAsync_IncludesToolsFromConnectedClients()
        {
            var service = new McpService();

            var mockClient = new Mock<IMcpClient>();
            mockClient.Setup(c => c.IsConnected).Returns(true);
            mockClient.Setup(c => c.ServerName).Returns("MockServer");
            mockClient.Setup(c => c.ListToolsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<McpTool> { MakeTool("remote_tool", "remote", "MockServer") });

            service.AddClient(mockClient.Object);

            var tools = await service.GetAllToolsAsync();

            Assert.Single(tools);
            Assert.Equal("remote_tool", tools[0].Name);
        }

        [Fact]
        public async Task GetAllToolsAsync_SkipsDisconnectedClients()
        {
            var service = new McpService();

            var mockClient = new Mock<IMcpClient>();
            mockClient.Setup(c => c.IsConnected).Returns(false);
            service.AddClient(mockClient.Object);

            var tools = await service.GetAllToolsAsync();

            Assert.Empty(tools);
            mockClient.Verify(c => c.ListToolsAsync(It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task SearchToolsAsync_FindsByName()
        {
            var service = new McpService();
            service.RegisterBuiltInTool(MakeBuiltInTool("vs_read_file", "Read file content"),
                _ => Task.FromResult(McpToolCallResult.Success("ok")));
            service.RegisterBuiltInTool(MakeBuiltInTool("vs_list_files", "List files"),
                _ => Task.FromResult(McpToolCallResult.Success("ok")));

            var results = await service.SearchToolsAsync("read");

            Assert.Single(results);
            Assert.Equal("vs_read_file", results[0].Name);
        }

        [Fact]
        public async Task SearchToolsAsync_FindsByDescription()
        {
            var service = new McpService();
            service.RegisterBuiltInTool(MakeBuiltInTool("tool_a", "Reads filesystem paths"),
                _ => Task.FromResult(McpToolCallResult.Success("ok")));
            service.RegisterBuiltInTool(MakeBuiltInTool("tool_b", "Writes database records"),
                _ => Task.FromResult(McpToolCallResult.Success("ok")));

            var results = await service.SearchToolsAsync("filesystem");

            Assert.Single(results);
            Assert.Equal("tool_a", results[0].Name);
        }

        [Fact]
        public async Task SearchToolsAsync_IsCaseInsensitive()
        {
            var service = new McpService();
            service.RegisterBuiltInTool(MakeBuiltInTool("MyTool", "Does Something"),
                _ => Task.FromResult(McpToolCallResult.Success("ok")));

            var results = await service.SearchToolsAsync("something");
            Assert.Single(results);
        }

        [Fact]
        public async Task CallToolAsync_RoutesToBuiltIn()
        {
            var service = new McpService();
            service.RegisterBuiltInTool(MakeBuiltInTool("echo"),
                args => Task.FromResult(McpToolCallResult.Success(args["msg"]?.ToString() ?? "")));

            var result = await service.CallToolAsync("echo", new Dictionary<string, object?> { ["msg"] = "hello" });

            Assert.False(result.IsError);
            Assert.Equal("hello", result.GetTextContent());
        }

        [Fact]
        public async Task CallToolAsync_ReturnsFailure_WhenToolNotFound()
        {
            var service = new McpService();

            var result = await service.CallToolAsync("nonexistent_tool", new Dictionary<string, object?>());

            Assert.True(result.IsError);
            Assert.Contains("nonexistent_tool", result.ErrorMessage);
        }

        [Fact]
        public async Task CallToolAsync_RoutesToCorrectClient()
        {
            var service = new McpService();

            var mockClient = new Mock<IMcpClient>();
            mockClient.Setup(c => c.IsConnected).Returns(true);
            mockClient.Setup(c => c.ServerName).Returns("RemoteServer");
            mockClient.Setup(c => c.ListToolsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<McpTool> { MakeTool("remote_action", "does stuff", "RemoteServer") });
            mockClient.Setup(c => c.CallToolAsync("remote_action", It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(McpToolCallResult.Success("remote result"));

            service.AddClient(mockClient.Object);

            var result = await service.CallToolAsync("remote_action", new Dictionary<string, object?>());

            Assert.False(result.IsError);
            Assert.Equal("remote result", result.GetTextContent());
            mockClient.Verify(c => c.CallToolAsync("remote_action", It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task CallToolAsync_ByServerName_RoutesToNamedClient()
        {
            var service = new McpService();

            var mockClient = new Mock<IMcpClient>();
            mockClient.Setup(c => c.IsConnected).Returns(true);
            mockClient.Setup(c => c.ServerName).Returns("SpecificServer");
            mockClient.Setup(c => c.CallToolAsync("do_thing", It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(McpToolCallResult.Success("specific result"));

            service.AddClient(mockClient.Object);

            var result = await service.CallToolAsync("SpecificServer", "do_thing", new Dictionary<string, object?>());

            Assert.False(result.IsError);
            Assert.Equal("specific result", result.GetTextContent());
        }

        [Fact]
        public async Task CallToolAsync_ByServerName_ReturnsFailure_WhenServerNotFound()
        {
            var service = new McpService();

            var result = await service.CallToolAsync("NoSuchServer", "tool", new Dictionary<string, object?>());

            Assert.True(result.IsError);
            Assert.Contains("NoSuchServer", result.ErrorMessage);
        }

        [Fact]
        public async Task InvalidateToolCache_ForcesRefresh()
        {
            var service = new McpService();
            var callCount = 0;

            var mockClient = new Mock<IMcpClient>();
            mockClient.Setup(c => c.IsConnected).Returns(true);
            mockClient.Setup(c => c.ServerName).Returns("S");
            mockClient.Setup(c => c.ListToolsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() =>
                {
                    callCount++;
                    return new List<McpTool>();
                });

            service.AddClient(mockClient.Object);

            await service.GetAllToolsAsync();
            await service.GetAllToolsAsync(); // should use cache
            Assert.Equal(1, callCount);

            service.InvalidateToolCache();
            await service.GetAllToolsAsync(); // should re-fetch
            Assert.Equal(2, callCount);
        }
    }
}
