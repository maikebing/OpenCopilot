using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace OpenCopilot.Mcp
{
    /// <summary>
    /// MCP client that communicates with an MCP server over stdio using JSON-RPC 2.0.
    /// </summary>
    public class McpStdioClient : IMcpClient, IDisposable
    {
        private readonly McpServerConfig _config;
        private Process? _process;
        private StreamWriter? _stdin;
        private readonly ConcurrentDictionary<int, TaskCompletionSource<JObject>> _pending
            = new ConcurrentDictionary<int, TaskCompletionSource<JObject>>();
        private int _nextId;
        private bool _disposed;
        private bool _connected;
        private Thread? _readerThread;

        private const int InitTimeoutSeconds = 15;
        private const int CallTimeoutSeconds = 30;

        public string ServerName => _config.Name;
        public bool IsConnected => _connected && _process?.HasExited == false;

        public McpStdioClient(McpServerConfig config) { _config = config; }

        public async Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _config.Command,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardInputEncoding = Encoding.UTF8
                };

                foreach (var arg in _config.Args)
                    psi.Arguments += (string.IsNullOrEmpty(psi.Arguments) ? "" : " ") + EscapeArg(arg);

                foreach (var kv in _config.Env)
                    psi.EnvironmentVariables[kv.Key] = kv.Value;

                _process = Process.Start(psi);
                if (_process == null) return false;

                _stdin = _process.StandardInput;
                _stdin.AutoFlush = true;

                // Start background reader
                _readerThread = new Thread(ReadLoop) { IsBackground = true, Name = $"MCP-{_config.Name}" };
                _readerThread.Start();

                // Send initialize request
                var initId = Interlocked.Increment(ref _nextId);
                var initTcs = new TaskCompletionSource<JObject>();
                _pending[initId] = initTcs;

                var initRequest = new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = initId,
                    ["method"] = "initialize",
                    ["params"] = new JObject
                    {
                        ["protocolVersion"] = "2024-11-05",
                        ["capabilities"] = new JObject(),
                        ["clientInfo"] = new JObject
                        {
                            ["name"] = "OpenCopilot",
                            ["version"] = "1.0.0"
                        }
                    }
                };

                await SendRawAsync(initRequest.ToString(Formatting.None)).ConfigureAwait(false);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(InitTimeoutSeconds));

                var completedTask = await Task.WhenAny(initTcs.Task, Task.Delay(Timeout.Infinite, cts.Token))
                    .ConfigureAwait(false);

                if (completedTask != initTcs.Task)
                {
                    _pending.TryRemove(initId, out _);
                    return false;
                }

                // Send initialized notification (no response expected)
                var notification = new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["method"] = "notifications/initialized"
                };
                await SendRawAsync(notification.ToString(Formatting.None)).ConfigureAwait(false);

                _connected = true;
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[McpStdioClient] ConnectAsync failed for '{_config.Name}': {ex.Message}");
                return false;
            }
        }

        public async Task<List<McpTool>> ListToolsAsync(CancellationToken cancellationToken = default)
        {
            var response = await SendRequestAsync("tools/list", null, cancellationToken).ConfigureAwait(false);
            var tools = new List<McpTool>();

            var toolsArray = response?["result"]?["tools"] as JArray;
            if (toolsArray == null) return tools;

            foreach (var t in toolsArray)
            {
                tools.Add(new McpTool
                {
                    Name = t["name"]?.ToString() ?? string.Empty,
                    Description = t["description"]?.ToString() ?? string.Empty,
                    InputSchema = t["inputSchema"] as JObject,
                    ServerName = _config.Name
                });
            }

            return tools;
        }

        public async Task<McpToolCallResult> CallToolAsync(
            string toolName,
            Dictionary<string, object?> arguments,
            CancellationToken cancellationToken = default)
        {
            var args = new JObject();
            foreach (var kv in arguments)
                args[kv.Key] = kv.Value == null ? JValue.CreateNull() : JToken.FromObject(kv.Value);

            var parameters = new JObject
            {
                ["name"] = toolName,
                ["arguments"] = args
            };

            var response = await SendRequestAsync("tools/call", parameters, cancellationToken).ConfigureAwait(false);
            if (response == null)
                return McpToolCallResult.Failure("No response from server.");

            var result = response["result"];
            if (result == null)
            {
                var error = response["error"]?["message"]?.ToString() ?? "Unknown error";
                return McpToolCallResult.Failure(error);
            }

            var isError = result["isError"]?.ToObject<bool>() ?? false;
            var contentArray = result["content"] as JArray;
            var contents = new List<McpContent>();

            if (contentArray != null)
            {
                foreach (var item in contentArray)
                {
                    contents.Add(new McpContent
                    {
                        Type = item["type"]?.ToString() ?? "text",
                        Text = item["text"]?.ToString(),
                        Data = item["data"]?.ToString(),
                        MimeType = item["mimeType"]?.ToString()
                    });
                }
            }

            return new McpToolCallResult { IsError = isError, Content = contents };
        }

        public void Disconnect()
        {
            _connected = false;
            try
            {
                if (_process != null && !_process.HasExited)
                    _process.Kill();
            }
            catch { /* ignore */ }

            // Fail all pending requests
            foreach (var kv in _pending)
                kv.Value.TrySetCanceled();
            _pending.Clear();
        }

        private async Task<JObject?> SendRequestAsync(string method, JObject? parameters, CancellationToken cancellationToken)
        {
            var id = Interlocked.Increment(ref _nextId);
            var tcs = new TaskCompletionSource<JObject>();
            _pending[id] = tcs;

            var request = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method
            };
            if (parameters != null)
                request["params"] = parameters;

            await SendRawAsync(request.ToString(Formatting.None)).ConfigureAwait(false);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(CallTimeoutSeconds));

            var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(Timeout.Infinite, cts.Token))
                .ConfigureAwait(false);

            if (completedTask != tcs.Task)
            {
                _pending.TryRemove(id, out _);
                return null;
            }

            return await tcs.Task.ConfigureAwait(false);
        }

        private Task SendRawAsync(string line)
        {
            if (_stdin == null) return Task.CompletedTask;
            return _stdin.WriteLineAsync(line);
        }

        private void ReadLoop()
        {
            try
            {
                var reader = _process!.StandardOutput;
                while (!_process.HasExited)
                {
                    var line = reader.ReadLine();
                    if (line == null) break;
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    JObject? obj;
                    try { obj = JObject.Parse(line); }
                    catch { continue; }

                    var idToken = obj["id"];
                    if (idToken != null && idToken.Type != JTokenType.Null)
                    {
                        var id = idToken.ToObject<int>();
                        if (_pending.TryRemove(id, out var tcs))
                            tcs.TrySetResult(obj);
                    }
                }
            }
            catch { /* process exited */ }
            finally
            {
                // Fail all remaining pending requests
                foreach (var kv in _pending)
                    kv.Value.TrySetCanceled();
                _pending.Clear();
            }
        }

        private static string EscapeArg(string arg)
        {
            if (!arg.Contains(' ') && !arg.Contains('"'))
                return arg;
            return "\"" + arg.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                Disconnect();
                _process?.Dispose();
                _stdin?.Dispose();
            }
        }
    }
}
