using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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
        /// Formats a concise summary for a tool call and its result.
        /// </summary>
        public static string Format(AgentToolCall toolCall, McpToolCallResult result)
        {
            if (toolCall == null)
                throw new ArgumentNullException(nameof(toolCall));
            if (result == null)
                throw new ArgumentNullException(nameof(result));

            var builder = new StringBuilder();
            builder.AppendLine((result.IsError ? "[工具失败] " : "[工具调用] ") + GetToolTitle(toolCall.ToolName));

            if (!string.IsNullOrWhiteSpace(toolCall.Reason))
                builder.AppendLine("- 步骤：" + Shorten(toolCall.Reason));

            var targetSummary = BuildTargetSummary(toolCall);
            if (!string.IsNullOrWhiteSpace(targetSummary))
                builder.AppendLine("- 目标：" + targetSummary);

            builder.AppendLine(result.IsError
                ? "- 结果：" + Shorten(result.ErrorMessage ?? "未知错误")
                : "- 结果：" + BuildResultSummary(toolCall.ToolName, result));

            return builder.ToString().TrimEnd();
        }

        private static string GetToolTitle(string toolName)
        {
            switch (toolName ?? string.Empty)
            {
                case "vs_read_file":
                    return "读取文件";
                case "vs_list_project_files":
                    return "列出项目文件";
                case "vs_create_file":
                    return "创建文件";
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
                    return GetString(args, "path");
                case "vs_apply_patch":
                    return BuildPatchTargetSummary(args);
                case "vs_run_tests":
                    return JoinSegments(new[]
                    {
                        FormatNamedValue("路径", GetString(args, "path")),
                        FormatNamedValue("筛选", GetString(args, "filter"))
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
            var path = GetString(arguments, "path");
            if (!string.IsNullOrWhiteSpace(path))
                segments.Add(path);

            var anchorText = GetString(arguments, "anchorText");
            if (!string.IsNullOrWhiteSpace(anchorText))
                segments.Add("锚点 " + Shorten(anchorText, 40));

            var startLine = GetInt(arguments, "startLine");
            var endLine = GetInt(arguments, "endLine");
            if (startLine > 0 || endLine > 0)
                segments.Add(endLine > 0 ? $"第 {Math.Max(1, startLine)}-{endLine} 行" : $"从第 {Math.Max(1, startLine)} 行开始");

            var targetLine = GetInt(arguments, "targetLine");
            var contextLines = GetInt(arguments, "contextLines");
            if (targetLine > 0)
                segments.Add(contextLines > 0 ? $"目标行 {targetLine}，上下文 {contextLines} 行" : $"目标行 {targetLine}");

            var startChar = GetInt(arguments, "startChar");
            var charLength = GetInt(arguments, "charLength");
            if (startChar >= 0)
                segments.Add(charLength > 0 ? $"字符 {startChar}+{charLength}" : $"字符 {startChar} 起");

            return JoinSegments(segments);
        }

        private static string BuildPatchTargetSummary(JObject arguments)
        {
            var path = GetString(arguments, "path");
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

        private static string BuildResultSummary(string toolName, McpToolCallResult result)
        {
            switch (toolName ?? string.Empty)
            {
                case "vs_read_file":
                    return "已读取文件内容";
                case "vs_list_project_files":
                    return FormatCountSummary(result.GetTextContent(), "个文件");
                case "vs_create_file":
                    return "已创建文件";
                case "vs_edit_file":
                case "vs_replace_file_content":
                    return "已更新文件";
                case "vs_apply_patch":
                    return "已应用补丁";
                case "vs_reencode_file":
                    return "已完成编码转换";
                case "vs_build_solution":
                    return "已返回构建结果";
                case "vs_get_build_errors":
                    return "已返回构建错误摘要";
                case "vs_run_tests":
                    return "已返回测试结果";
                default:
                    return string.IsNullOrWhiteSpace(result.GetTextContent()) ? "已完成" : "已返回结果摘要";
            }
        }

        private static string FormatCountSummary(string text, string unit)
        {
            var count = text
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Length;

            return count > 0 ? $"已返回 {count} {unit}" : "已返回结果摘要";
        }

        private static string FormatNamedValue(string name, string value)
            => string.IsNullOrWhiteSpace(value) ? string.Empty : name + "：" + value;

        private static string JoinSegments(IEnumerable<string> segments)
            => string.Join("，", segments.Where(segment => !string.IsNullOrWhiteSpace(segment)));

        private static string GetString(JObject arguments, string name)
            => arguments[name]?.ToString() ?? string.Empty;

        private static int GetInt(JObject arguments, string name)
            => arguments[name]?.ToObject<int>() ?? 0;

        private static string Shorten(string value, int maxLength = 80)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var singleLine = value.Replace("\r", " ").Replace("\n", " ").Trim();
            return singleLine.Length <= maxLength ? singleLine : singleLine.Substring(0, maxLength).TrimEnd() + "...";
        }
    }
}
