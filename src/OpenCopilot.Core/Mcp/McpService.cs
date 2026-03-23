using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenCopilot.Mcp
{
    /// <summary>
    /// Central service that aggregates tools from all registered MCP clients and built-in tools,
    /// routes tool calls, caches the tool list, and supports per-server enable/disable scoping.
    /// </summary>
    public class McpService
    {
        private readonly List<IMcpClient> _clients = new List<IMcpClient>();
        private readonly HashSet<string> _disabledServers
            = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Func<Dictionary<string, object?>, Task<McpToolCallResult>>> _builtInTools
            = new Dictionary<string, Func<Dictionary<string, object?>, Task<McpToolCallResult>>>(StringComparer.OrdinalIgnoreCase);
        private readonly List<McpTool> _builtInToolDefinitions = new List<McpTool>();
        private readonly List<McpTool> _cachedTools = new List<McpTool>();
        private bool _toolsCached = false;

        public IReadOnlyList<IMcpClient> Clients => _clients.AsReadOnly();

        // ── Client management ────────────────────────────────────────────────

        public void AddClient(IMcpClient client)
        {
            _clients.Add(client);
            _toolsCached = false;
        }

        // ── Server scope (enable / disable) ──────────────────────────────────

        /// <summary>
        /// Enables or disables a named server. Disabled servers are skipped during tool
        /// enumeration and calls; their tools disappear from the active tool set immediately.
        /// Built-in tools are always enabled regardless of this setting.
        /// </summary>
        public void SetServerEnabled(string serverName, bool enabled)
        {
            if (enabled)
                _disabledServers.Remove(serverName);
            else
                _disabledServers.Add(serverName);

            _toolsCached = false;
        }

        /// <summary>
        /// Replaces the full disabled-server set (e.g. when loading persisted settings).
        /// Pass an empty collection to enable all servers.
        /// </summary>
        public void SetDisabledServers(IEnumerable<string> serverNames)
        {
            _disabledServers.Clear();
            foreach (var name in serverNames)
                if (!string.IsNullOrWhiteSpace(name))
                    _disabledServers.Add(name);

            _toolsCached = false;
        }

        /// <summary>Returns <see langword="true"/> when the server is currently enabled.</summary>
        public bool IsServerEnabled(string serverName)
            => !_disabledServers.Contains(serverName);

        /// <summary>Returns a snapshot of all currently disabled server names.</summary>
        public IReadOnlyCollection<string> DisabledServers => _disabledServers;

        // ── Built-in tools ────────────────────────────────────────────────────

        public void RegisterBuiltInTool(McpTool definition, Func<Dictionary<string, object?>, Task<McpToolCallResult>> handler)
        {
            _builtInToolDefinitions.Add(definition);
            _builtInTools[definition.Name] = handler;
            _toolsCached = false;
        }

        // ── Tool enumeration ──────────────────────────────────────────────────

        public async Task<List<McpTool>> GetAllToolsAsync(CancellationToken cancellationToken = default)
        {
            if (_toolsCached)
                return new List<McpTool>(_cachedTools);

            _cachedTools.Clear();
            _cachedTools.AddRange(_builtInToolDefinitions);

            foreach (var client in _clients)
            {
                if (!client.IsConnected) continue;
                if (!IsServerEnabled(client.ServerName)) continue;
                try
                {
                    var tools = await client.ListToolsAsync(cancellationToken).ConfigureAwait(false);
                    _cachedTools.AddRange(tools);
                }
                catch { /* skip failed clients */ }
            }

            _toolsCached = true;
            return new List<McpTool>(_cachedTools);
        }

        public async Task<List<McpTool>> SearchToolsAsync(string query, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(query))
                return await GetAllToolsAsync(cancellationToken).ConfigureAwait(false);

            var all = await GetAllToolsAsync(cancellationToken).ConfigureAwait(false);
            return all
                .Where(t => t.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                         || t.Description.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();
        }

        // ── Tool calls ────────────────────────────────────────────────────────

        /// <summary>Calls a tool by name, routing to built-ins first then to the first enabled client that has it.</summary>
        public async Task<McpToolCallResult> CallToolAsync(
            string toolName,
            Dictionary<string, object?> arguments,
            CancellationToken cancellationToken = default)
        {
            if (_builtInTools.TryGetValue(toolName, out var handler))
                return await handler(arguments).ConfigureAwait(false);

            // Find which client owns this tool (only search enabled clients)
            var all = await GetAllToolsAsync(cancellationToken).ConfigureAwait(false);
            var tool = all.FirstOrDefault(t => string.Equals(t.Name, toolName, StringComparison.OrdinalIgnoreCase));
            if (tool == null)
                return McpToolCallResult.Failure($"Tool '{toolName}' not found.");

            var client = _clients.FirstOrDefault(c =>
                string.Equals(c.ServerName, tool.ServerName, StringComparison.OrdinalIgnoreCase)
                && IsServerEnabled(c.ServerName));
            if (client == null || !client.IsConnected)
                return McpToolCallResult.Failure($"Server '{tool.ServerName}' is not connected or is disabled.");

            return await client.CallToolAsync(toolName, arguments, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Calls a tool on a specific named server (server must be enabled).</summary>
        public async Task<McpToolCallResult> CallToolAsync(
            string serverName,
            string toolName,
            Dictionary<string, object?> arguments,
            CancellationToken cancellationToken = default)
        {
            if (!IsServerEnabled(serverName))
                return McpToolCallResult.Failure($"Server '{serverName}' is disabled.");

            var client = _clients.FirstOrDefault(c => string.Equals(c.ServerName, serverName, StringComparison.OrdinalIgnoreCase));
            if (client == null)
                return McpToolCallResult.Failure($"Server '{serverName}' not found.");
            if (!client.IsConnected)
                return McpToolCallResult.Failure($"Server '{serverName}' is not connected.");

            return await client.CallToolAsync(toolName, arguments, cancellationToken).ConfigureAwait(false);
        }

        public void InvalidateToolCache() { _toolsCached = false; }
    }
}
