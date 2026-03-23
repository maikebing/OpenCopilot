using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using OpenCopilot.Mcp;

namespace OpenCopilot.Chat
{
    /// <summary>
    /// Creates concise user-facing summaries for tool activity shown in the chat panel.
    /// </summary>
    public static class ChatToolActivityFormatter
    {
        /// <summary>
        /// Formats a single compact execution step for a tool call.
        /// </summary>
        public static string FormatStep(AgentToolCall toolCall, McpToolCallResult result)
        {
            if (toolCall == null)
                throw new ArgumentNullException(nameof(toolCall));
            if (result == null)
                throw new ArgumentNullException(nameof(result));

            var summary = BuildStepSummary(toolCall);
            return result.IsError
                ? $"{GetToolIcon(toolCall.ToolName)} {summary}（失败：{Shorten(result.ErrorMessage ?? "未知错误", 36)}）"
                : $"{GetToolIcon(toolCall.ToolName)} {summary}";
        }

        /// <summary>
        /// Formats an execution-summary card from one or more compact tool steps.
        /// </summary>
        public static string FormatExecutionSummary(IEnumerable<string> stepSummaries)
        {
            if (stepSummaries == null)
                throw new ArgumentNullException(nameof(stepSummaries));

            var steps = stepSummaries
                .Where(step => !string.IsNullOrWhiteSpace(step))
                .ToArray();

            return steps.Length == 0
                ? "执行过程摘要：等待工具操作"
                : "执行过程摘要：" + string.Join(" · ", steps);
        }

        private static string BuildStepSummary(AgentToolCall toolCall)
        {
            var reason = Shorten(toolCall.Reason, 28);
            var target = BuildTargetSummary(toolCall);
            if (!string.IsNullOrWhiteSpace(reason) && !string.IsNullOrWhiteSpace(target))
                return reason + "（" + target + "）";
            if (!string.IsNullOrWhiteSpace(reason))
                return reason;
            if (!string.IsNullOrWhiteSpace(target))
                return GetToolLabel(toolCall.ToolName) + " " + target;

            return GetToolLabel(toolCall.ToolName);
        }

        private static string GetToolIcon(string toolName)
        {
            switch (toolName ?? string.Empty)
            {
                case "vs_read_file":
                    return "📖";
                case "vs_list_project_files":
                    return "🗂️";
                case "vs_create_file":
                    return "📝";
                case "vs_edit_file":
                case "vs_replace_file_content":
                    return "✏️";
                case "vs_apply_patch":
                    return "🩹";
                case "vs_reencode_file":
                    return "🔤";
                case "vs_build_solution":
                    return "🏗️";
                case "vs_get_build_errors":
                    return "📋";
                case "vs_run_tests":
                    return "🧪";
                default:
                    return "⚙️";
            }
        }

        private static string GetToolLabel(string toolName)
        {
            switch (toolName ?? string.Empty)
            {
                case "vs_read_file":
                    return "读文件";
                case "vs_list_project_files":
                    return "列文件";
                case "vs_create_file":
                    return "新建文件";
                case "vs_edit_file":
                    return "编辑文件";
                case "vs_replace_file_content":
                    return "覆盖文件";
                case "vs_apply_patch":
                    return "应用补丁";
                case "vs_reencode_file":
                    return "转换文件编码";
                case "vs_build_solution":
                    return "构建解决方案";
                case "vs_get_build_errors":
                    return "读取构建错误";
                case "vs_run_tests":
                    return "运行测试";
                default:
                    return string.IsNullOrWhiteSpace(toolName) ? "执行工具" : toolName ?? "执行工具";
            }
        }

        private static string BuildTargetSummary(AgentToolCall toolCall)
        {
            var args = toolCall.Arguments;
            switch (toolCall.ToolName ?? string.Empty)
            {
                case "vs_read_file":
                    return BuildReadFileTargetSummary(args);
                case "vs_create_file":
                case "vs_edit_file":
                case "vs_replace_file_content":
                case "vs_reencode_file":
                    return ShortenPath(GetString(args, "path"));
                case "vs_apply_patch":
                    return BuildPatchTargetSummary(args);
                case "vs_run_tests":
                    return JoinSegments(new[]
                    {
                        FormatNamedValue("路径", Shorten(GetString(args, "path"), 36)),
                        FormatNamedValue("筛选", Shorten(GetString(args, "filter"), 36))
                    });
                case "vs_get_build_errors":
                    return "当前构建输出";
                default:
                    return JoinSegments(args.Properties().Take(3).Select(property => property.Name).ToArray());
            }
        }

        private static string BuildReadFileTargetSummary(JObject arguments)
        {
            var segments = new List<string>();
            var path = ShortenPath(GetString(arguments, "path"));
            if (!string.IsNullOrWhiteSpace(path))
                segments.Add(path);

            var anchorText = GetString(arguments, "anchorText");
            if (!string.IsNullOrWhiteSpace(anchorText))
                segments.Add("锚点 " + Shorten(anchorText, 40));

            var startLine = GetInt(arguments, "startLine");
            var endLine = GetInt(arguments, "endLine");
            if (startLine > 0 || endLine > 0)
                segments.Add(endLine > 0 ? $"{Math.Max(1, startLine)}-{endLine} 行" : $"第 {Math.Max(1, startLine)} 行起");

            var targetLine = GetInt(arguments, "targetLine");
            var contextLines = GetInt(arguments, "contextLines");
            if (targetLine > 0)
                segments.Add(contextLines > 0 ? $"目标行 {targetLine}±{contextLines}" : $"目标行 {targetLine}");

            var startChar = GetInt(arguments, "startChar");
            var charLength = GetInt(arguments, "charLength");
            if (startChar >= 0)
                segments.Add(charLength > 0 ? $"字符 {startChar}+{charLength}" : $"字符 {startChar}");

            return JoinSegments(segments);
        }

        private static string BuildPatchTargetSummary(JObject arguments)
        {
            var path = ShortenPath(GetString(arguments, "path"));
            if (!string.IsNullOrWhiteSpace(path))
                return path;

            var patch = GetString(arguments, "patch");
            if (string.IsNullOrWhiteSpace(patch))
                return string.Empty;

            var fileCount = patch
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Count(line => line.StartsWith("*** Update File:", StringComparison.Ordinal)
                    || line.StartsWith("*** Add File:", StringComparison.Ordinal)
                    || line.StartsWith("*** Delete File:", StringComparison.Ordinal));

            return fileCount > 0 ? $"{fileCount} 个文件" : "补丁内容";
        }

        private static string FormatNamedValue(string name, string value)
            => string.IsNullOrWhiteSpace(value) ? string.Empty : name + "：" + value;

        private static string JoinSegments(IEnumerable<string> segments)
            => string.Join("，", segments.Where(segment => !string.IsNullOrWhiteSpace(segment)));

        private static string GetString(JObject arguments, string name)
            => arguments[name]?.ToString() ?? string.Empty;

        private static int GetInt(JObject arguments, string name)
            => arguments[name]?.ToObject<int>() ?? 0;

        private static string ShortenPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            var normalized = path.Replace('\\', '/').Trim();
            var segments = normalized.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length <= 3)
                return normalized;

            return string.Join("/", segments.Skip(segments.Length - 3));
        }

        private static string Shorten(string value, int maxLength = 80)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var singleLine = value.Replace("\r", " ").Replace("\n", " ").Trim();
            return singleLine.Length <= maxLength ? singleLine : singleLine.Substring(0, maxLength).TrimEnd() + "...";
        }
    }
}
