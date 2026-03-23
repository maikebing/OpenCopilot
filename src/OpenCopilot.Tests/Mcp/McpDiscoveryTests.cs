using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using OpenCopilot.Mcp;
using Xunit;

namespace OpenCopilot.Tests.Mcp
{
    public class McpDiscoveryTests
    {
        private static string WriteTempFile(string dir, string fileName, object content)
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, fileName);
            File.WriteAllText(path, JsonConvert.SerializeObject(content));
            return path;
        }

        [Fact]
        public void Discover_ReturnsMcpServersFromDotMcpJson()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            try
            {
                WriteTempFile(tempDir, ".mcp.json", new
                {
                    mcpServers = new Dictionary<string, object>
                    {
                        ["filesystem"] = new { command = "npx", args = new[] { "-y", "@modelcontextprotocol/server-filesystem" }, env = new { } }
                    }
                });

                var discovery = new McpDiscovery();
                var configs = discovery.Discover(tempDir);

                Assert.Contains(configs, c => c.Name == "filesystem");
                var fs = configs.Find(c => c.Name == "filesystem");
                Assert.NotNull(fs);
                Assert.Equal(McpTransport.Stdio, fs!.Transport);
                Assert.Equal("npx", fs.Command);
                Assert.Contains("-y", fs.Args);
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public void Discover_ParsesHttpServerConfig()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            try
            {
                WriteTempFile(tempDir, ".mcp.json", new
                {
                    mcpServers = new Dictionary<string, object>
                    {
                        ["my-http-server"] = new { url = "http://localhost:3000" }
                    }
                });

                var discovery = new McpDiscovery();
                var configs = discovery.Discover(tempDir);

                var http = configs.Find(c => c.Name == "my-http-server");
                Assert.NotNull(http);
                Assert.Equal(McpTransport.Http, http!.Transport);
                Assert.Equal("http://localhost:3000", http.Url);
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public void Discover_ReturnsEmptyList_WhenNoFilesExist()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(tempDir);
            try
            {
                var discovery = new McpDiscovery();
                var configs = discovery.Discover(tempDir);

                // Only global configs might come back (Claude Desktop / .config/mcp/config.json)
                // but those typically don't exist in CI — local entries should be empty
                Assert.DoesNotContain(configs, c => c.Name == "filesystem" || c.Name == "my-http-server");
            }
            finally
            {
                Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public void Discover_ParsesClaudeDesktopConfigFormat()
        {
            // Use a temp directory to simulate APPDATA by writing a temp .mcp.json
            // (Claude Desktop format is identical to .mcp.json format)
            var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            try
            {
                WriteTempFile(tempDir, ".mcp.json", new
                {
                    mcpServers = new Dictionary<string, object>
                    {
                        ["claude-tool"] = new { command = "python", args = new[] { "-m", "mcp_server" }, env = new { API_KEY = "abc" } }
                    }
                });

                var discovery = new McpDiscovery();
                var configs = discovery.Discover(tempDir);

                var tool = configs.Find(c => c.Name == "claude-tool");
                Assert.NotNull(tool);
                Assert.Equal("python", tool!.Command);
                Assert.True(tool.Env.ContainsKey("API_KEY"));
                Assert.Equal("abc", tool.Env["API_KEY"]);
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public void Discover_ParsesVscodeMcpJson()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            var vscodeDir = Path.Combine(tempDir, ".vscode");
            try
            {
                WriteTempFile(vscodeDir, "mcp.json", new
                {
                    servers = new Dictionary<string, object>
                    {
                        ["vscode-tool"] = new { command = "node", args = new[] { "server.js" } }
                    }
                });

                var discovery = new McpDiscovery();
                var configs = discovery.Discover(tempDir);

                var tool = configs.Find(c => c.Name == "vscode-tool");
                Assert.NotNull(tool);
                Assert.Equal("node", tool!.Command);
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }
    }
}
