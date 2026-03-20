using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;

namespace OpenCopilot.Mcp
{
    /// <summary>
    /// Discovers MCP server configurations from standard config file locations.
    /// Checks (in order):
    ///   1. {solutionDir}/.mcp.json
    ///   2. {solutionDir}/.vscode/mcp.json
    ///   3. %APPDATA%/Claude/claude_desktop_config.json
    ///   4. %USERPROFILE%/.config/mcp/config.json
    /// </summary>
    public class McpDiscovery
    {
        public List<McpServerConfig> Discover(string? solutionDirectory = null)
        {
            var configs = new List<McpServerConfig>();

            if (!string.IsNullOrWhiteSpace(solutionDirectory))
            {
                TryLoadMcpJson(Path.Combine(solutionDirectory, ".mcp.json"), configs);
                TryLoadVscodeMcpJson(Path.Combine(solutionDirectory, ".vscode", "mcp.json"), configs);
            }

            var appData = Environment.GetEnvironmentVariable("APPDATA") ??
                          Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            TryLoadClaudeDesktopConfig(Path.Combine(appData, "Claude", "claude_desktop_config.json"), configs);

            var userProfile = Environment.GetEnvironmentVariable("USERPROFILE") ??
                              Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            TryLoadGenericConfig(Path.Combine(userProfile, ".config", "mcp", "config.json"), configs);

            return configs;
        }

        private static void LogParseFailure(string path, Exception ex)
            => System.Diagnostics.Debug.WriteLine($"[McpDiscovery] Failed to parse '{path}': {ex.Message}");

        private void TryLoadMcpJson(string path, List<McpServerConfig> configs)
        {
            if (!File.Exists(path)) return;
            try
            {
                var root = JObject.Parse(File.ReadAllText(path));
                ParseMcpServersBlock(root["mcpServers"] as JObject, configs);
            }
            catch (Exception ex) { LogParseFailure(path, ex); }
        }

        private void TryLoadVscodeMcpJson(string path, List<McpServerConfig> configs)
        {
            if (!File.Exists(path)) return;
            try
            {
                var root = JObject.Parse(File.ReadAllText(path));
                ParseMcpServersBlock(root["servers"] as JObject ?? root["mcpServers"] as JObject, configs);
            }
            catch (Exception ex) { LogParseFailure(path, ex); }
        }

        private void TryLoadClaudeDesktopConfig(string path, List<McpServerConfig> configs)
        {
            if (!File.Exists(path)) return;
            try
            {
                var root = JObject.Parse(File.ReadAllText(path));
                ParseMcpServersBlock(root["mcpServers"] as JObject, configs);
            }
            catch (Exception ex) { LogParseFailure(path, ex); }
        }

        private void TryLoadGenericConfig(string path, List<McpServerConfig> configs)
        {
            if (!File.Exists(path)) return;
            try
            {
                var root = JObject.Parse(File.ReadAllText(path));
                ParseMcpServersBlock(root["mcpServers"] as JObject ?? root["servers"] as JObject, configs);
            }
            catch (Exception ex) { LogParseFailure(path, ex); }
        }

        private static void ParseMcpServersBlock(JObject? servers, List<McpServerConfig> configs)
        {
            if (servers == null) return;

            foreach (var prop in servers.Properties())
            {
                var entry = prop.Value as JObject;
                if (entry == null) continue;

                var config = new McpServerConfig { Name = prop.Name };

                var url = entry["url"]?.ToString();
                if (!string.IsNullOrWhiteSpace(url))
                {
                    config.Transport = McpTransport.Http;
                    config.Url = url;
                }
                else
                {
                    config.Transport = McpTransport.Stdio;
                    config.Command = entry["command"]?.ToString() ?? string.Empty;

                    if (entry["args"] is JArray argsArray)
                        foreach (var a in argsArray)
                            config.Args.Add(a.ToString());

                    if (entry["env"] is JObject envObj)
                        foreach (var kv in envObj.Properties())
                            config.Env[kv.Name] = kv.Value.ToString();
                }

                configs.Add(config);
            }
        }
    }
}
