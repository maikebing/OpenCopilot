using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using OpenCopilot.Mcp;
using Xunit;

namespace OpenCopilot.Tests.Mcp
{
    /// <summary>
    /// Tests for per-server enable/disable scoping in <see cref="McpService"/>.
    /// </summary>
    public class McpServerScopeTests
    {
        private static McpTool MakeTool(string name, string server)
            => new McpTool { Name = name, Description = "desc", ServerName = server };

        private static Mock<IMcpClient> MakeMockClient(string serverName, params McpTool[] tools)
        {
            var mock = new Mock<IMcpClient>();
            mock.Setup(c => c.ServerName).Returns(serverName);
            mock.Setup(c => c.IsConnected).Returns(true);
            mock.Setup(c => c.ListToolsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<McpTool>(tools));
            return mock;
        }

        // ── IsServerEnabled ───────────────────────────────────────────────────

        [Fact]
        public void IsServerEnabled_ReturnsTrueByDefault()
        {
            var service = new McpService();
            Assert.True(service.IsServerEnabled("AnyServer"));
        }

        [Fact]
        public void SetServerEnabled_False_DisablesServer()
        {
            var service = new McpService();
            service.SetServerEnabled("MyServer", false);
            Assert.False(service.IsServerEnabled("MyServer"));
        }

        [Fact]
        public void SetServerEnabled_True_ReEnablesServer()
        {
            var service = new McpService();
            service.SetServerEnabled("MyServer", false);
            service.SetServerEnabled("MyServer", true);
            Assert.True(service.IsServerEnabled("MyServer"));
        }

        [Fact]
        public void SetDisabledServers_ReplacesExistingSet()
        {
            var service = new McpService();
            service.SetServerEnabled("Alpha", false);
            service.SetDisabledServers(new[] { "Beta", "Gamma" });

            Assert.True(service.IsServerEnabled("Alpha"));   // no longer disabled
            Assert.False(service.IsServerEnabled("Beta"));
            Assert.False(service.IsServerEnabled("Gamma"));
        }

        [Fact]
        public void SetDisabledServers_EmptyCollection_EnablesAll()
        {
            var service = new McpService();
            service.SetServerEnabled("X", false);
            service.SetDisabledServers(new string[0]);

            Assert.True(service.IsServerEnabled("X"));
        }

        [Fact]
        public void SetDisabledServers_IsCaseInsensitive()
        {
            var service = new McpService();
            service.SetDisabledServers(new[] { "myserver" });

            Assert.False(service.IsServerEnabled("MyServer"));
            Assert.False(service.IsServerEnabled("MYSERVER"));
        }

        [Fact]
        public void DisabledServers_ReturnsCurrentSet()
        {
            var service = new McpService();
            service.SetDisabledServers(new[] { "Alpha", "Beta" });

            Assert.Contains("Alpha", service.DisabledServers);
            Assert.Contains("Beta", service.DisabledServers);
            Assert.Equal(2, service.DisabledServers.Count);
        }

        // ── Tool enumeration filtering ────────────────────────────────────────

        [Fact]
        public async Task GetAllToolsAsync_ExcludesToolsFromDisabledServer()
        {
            var service = new McpService();
            service.AddClient(MakeMockClient("ServerA", MakeTool("tool_a", "ServerA")).Object);
            service.AddClient(MakeMockClient("ServerB", MakeTool("tool_b", "ServerB")).Object);

            service.SetServerEnabled("ServerA", false);

            var tools = await service.GetAllToolsAsync();

            Assert.DoesNotContain(tools, t => t.Name == "tool_a");
            Assert.Contains(tools, t => t.Name == "tool_b");
        }

        [Fact]
        public async Task GetAllToolsAsync_IncludesToolsWhenServerRe_Enabled()
        {
            var service = new McpService();
            service.AddClient(MakeMockClient("ServerA", MakeTool("tool_a", "ServerA")).Object);

            service.SetServerEnabled("ServerA", false);
            var toolsWhenDisabled = await service.GetAllToolsAsync();
            Assert.Empty(toolsWhenDisabled);

            service.SetServerEnabled("ServerA", true);
            var toolsWhenEnabled = await service.GetAllToolsAsync();
            Assert.Single(toolsWhenEnabled);
        }

        [Fact]
        public async Task GetAllToolsAsync_AlwaysIncludesBuiltInTools()
        {
            var service = new McpService();
            service.RegisterBuiltInTool(
                new McpTool { Name = "built_in", Description = "built-in", ServerName = "BuiltIn", IsBuiltIn = true },
                _ => Task.FromResult(McpToolCallResult.Success("ok")));

            // Disabling "BuiltIn" should have no effect on built-in tools
            // (they live outside the client list)
            service.SetServerEnabled("BuiltIn", false);

            var tools = await service.GetAllToolsAsync();
            Assert.Contains(tools, t => t.Name == "built_in");
        }

        // ── Call routing with scope ────────────────────────────────────────────

        [Fact]
        public async Task CallToolAsync_ReturnsError_WhenServerDisabled()
        {
            var service = new McpService();
            var mockClient = MakeMockClient("ServerA", MakeTool("tool_a", "ServerA"));
            mockClient.Setup(c => c.CallToolAsync("tool_a", It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()))
                      .ReturnsAsync(McpToolCallResult.Success("should not reach here"));
            service.AddClient(mockClient.Object);

            service.SetServerEnabled("ServerA", false);

            var result = await service.CallToolAsync("tool_a", new Dictionary<string, object?>());

            Assert.True(result.IsError);
            Assert.Contains("tool_a", result.ErrorMessage!);
        }

        [Fact]
        public async Task CallToolAsync_ByServerName_ReturnsError_WhenServerDisabled()
        {
            var service = new McpService();
            var mockClient = MakeMockClient("ServerA", MakeTool("tool_a", "ServerA"));
            service.AddClient(mockClient.Object);

            service.SetServerEnabled("ServerA", false);

            var result = await service.CallToolAsync("ServerA", "tool_a", new Dictionary<string, object?>());

            Assert.True(result.IsError);
            Assert.Contains("disabled", result.ErrorMessage!);
        }

        [Fact]
        public async Task CallToolAsync_ByServerName_Succeeds_WhenServerEnabled()
        {
            var service = new McpService();
            var mockClient = MakeMockClient("ServerA", MakeTool("tool_a", "ServerA"));
            mockClient.Setup(c => c.CallToolAsync("tool_a", It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()))
                      .ReturnsAsync(McpToolCallResult.Success("ok"));
            service.AddClient(mockClient.Object);

            // Server is enabled by default
            var result = await service.CallToolAsync("ServerA", "tool_a", new Dictionary<string, object?>());

            Assert.False(result.IsError);
            Assert.Equal("ok", result.GetTextContent());
        }

        // ── Cache invalidation ────────────────────────────────────────────────

        [Fact]
        public async Task SetServerEnabled_InvalidatesCache()
        {
            var service = new McpService();
            var callCount = 0;
            var mockClient = MakeMockClient("S");
            mockClient.Setup(c => c.ListToolsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => { callCount++; return new List<McpTool>(); });
            service.AddClient(mockClient.Object);

            await service.GetAllToolsAsync();  // fills cache
            await service.GetAllToolsAsync();  // uses cache
            Assert.Equal(1, callCount);

            service.SetServerEnabled("S", false);  // should invalidate
            await service.GetAllToolsAsync();       // should skip disabled (no call)
            // disabled server is not queried, so callCount stays 1
            Assert.Equal(1, callCount);
        }

        [Fact]
        public async Task SetDisabledServers_InvalidatesCache()
        {
            var service = new McpService();
            var callCount = 0;
            var mockClient = MakeMockClient("S");
            mockClient.Setup(c => c.ListToolsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => { callCount++; return new List<McpTool>(); });
            service.AddClient(mockClient.Object);

            await service.GetAllToolsAsync();  // fills cache
            Assert.Equal(1, callCount);

            service.SetDisabledServers(new[] { "Other" });  // invalidates cache
            await service.GetAllToolsAsync();               // S is enabled → refetch
            Assert.Equal(2, callCount);
        }
    }
}
