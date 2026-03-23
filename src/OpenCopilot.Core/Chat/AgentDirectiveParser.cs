using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace OpenCopilot.Chat
{
    /// <summary>
    /// Parses agent-mode JSON directives from model output.
    /// </summary>
    public static class AgentDirectiveParser
    {
        /// <summary>
        /// Parses the first valid directive object from the response content.
        /// </summary>
        public static AgentDirective Parse(string? responseContent)
        {
            var fallbackContent = responseContent ?? string.Empty;
            var normalizedContent = NormalizeContent(fallbackContent);

            foreach (var payload in EnumerateJsonObjects(normalizedContent))
            {
                if (TryParseDirective(payload, out var directive))
                    return directive;
            }

            return AgentDirective.Final(fallbackContent);
        }

        private static bool TryParseDirective(string payload, out AgentDirective directive)
        {
            directive = AgentDirective.Final(string.Empty);

            try
            {
                var json = JObject.Parse(payload);
                var type = json["type"]?.ToString();
                if (string.Equals(type, "tool_call", StringComparison.OrdinalIgnoreCase))
                {
                    var toolName = json["tool"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(toolName))
                    {
                        directive = AgentDirective.FromToolCalls(new[]
                        {
                            new AgentToolCall(
                                toolName,
                                json["arguments"] as JObject ?? new JObject(),
                                json["reason"]?.ToString())
                        });
                        return true;
                    }

                    return false;
                }

                if (string.Equals(type, "tool_calls", StringComparison.OrdinalIgnoreCase))
                {
                    var calls = json["calls"] as JArray;
                    if (calls == null)
                        return false;

                    var parsedCalls = calls
                        .OfType<JObject>()
                        .Select(call =>
                        {
                            var toolName = call["tool"]?.ToString();
                            return string.IsNullOrWhiteSpace(toolName)
                                ? null
                                : new AgentToolCall(
                                    toolName,
                                    call["arguments"] as JObject ?? new JObject(),
                                    call["reason"]?.ToString());
                        })
                        .Where(call => call != null)
                        .Cast<AgentToolCall>()
                        .ToArray();

                    if (parsedCalls.Length == 0)
                        return false;

                    directive = AgentDirective.FromToolCalls(parsedCalls);
                    return true;
                }

                if (string.Equals(type, "final", StringComparison.OrdinalIgnoreCase))
                {
                    directive = AgentDirective.Final(json["message"]?.ToString() ?? string.Empty);
                    return true;
                }
            }
            catch (JsonException)
            {
            }

            return false;
        }

        private static string NormalizeContent(string responseContent)
        {
            var trimmed = responseContent.Trim();
            if (!trimmed.StartsWith("```", StringComparison.Ordinal))
                return trimmed;

            var firstLineBreak = trimmed.IndexOf('\n');
            if (firstLineBreak >= 0)
                trimmed = trimmed.Substring(firstLineBreak + 1).Trim();

            var closingFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            return closingFence >= 0
                ? trimmed.Substring(0, closingFence).Trim()
                : trimmed;
        }

        private static IEnumerable<string> EnumerateJsonObjects(string responseContent)
        {
            var depth = 0;
            var start = -1;
            var inString = false;
            var isEscaped = false;

            for (var i = 0; i < responseContent.Length; i++)
            {
                var current = responseContent[i];

                if (isEscaped)
                {
                    isEscaped = false;
                    continue;
                }

                if (current == '\\' && inString)
                {
                    isEscaped = true;
                    continue;
                }

                if (current == '"')
                {
                    inString = !inString;
                    continue;
                }

                if (inString)
                    continue;

                if (current == '{')
                {
                    if (depth == 0)
                        start = i;

                    depth++;
                    continue;
                }

                if (current != '}' || depth == 0)
                    continue;

                depth--;
                if (depth == 0 && start >= 0)
                {
                    yield return responseContent.Substring(start, i - start + 1);
                    start = -1;
                }
            }
        }
    }

    /// <summary>
    /// Represents a parsed agent directive.
    /// </summary>
    public sealed class AgentDirective
    {
        private AgentDirective(IReadOnlyList<AgentToolCall> toolCalls, string finalMessage)
        {
            ToolCalls = toolCalls;
            FinalMessage = finalMessage;
        }

        public IReadOnlyList<AgentToolCall> ToolCalls { get; }

        public string FinalMessage { get; }

        /// <summary>
        /// Creates a directive that requests one or more tool calls.
        /// </summary>
        public static AgentDirective FromToolCalls(IReadOnlyList<AgentToolCall> toolCalls)
            => new AgentDirective(toolCalls ?? Array.Empty<AgentToolCall>(), string.Empty);

        /// <summary>
        /// Creates a directive that contains a final assistant message.
        /// </summary>
        public static AgentDirective Final(string finalMessage)
            => new AgentDirective(Array.Empty<AgentToolCall>(), finalMessage ?? string.Empty);
    }

    /// <summary>
    /// Represents a single tool call requested by the agent.
    /// </summary>
    public sealed class AgentToolCall
    {
        public AgentToolCall(string toolName, JObject arguments, string? reason = null)
        {
            ToolName = toolName ?? string.Empty;
            Arguments = arguments ?? new JObject();
            Reason = reason ?? string.Empty;
        }

        public string ToolName { get; }

        public JObject Arguments { get; }

        public string Reason { get; }

        /// <summary>
        /// Creates an empty tool call for system-triggered tools.
        /// </summary>
        public static AgentToolCall Empty(string toolName)
            => new AgentToolCall(toolName, new JObject());
    }
}
