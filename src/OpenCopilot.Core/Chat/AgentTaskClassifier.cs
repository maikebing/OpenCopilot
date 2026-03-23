using System;

namespace OpenCopilot.Chat
{
    /// <summary>
    /// Classifies agent requests to determine whether an IDE write is required before returning a final answer.
    /// </summary>
    public static class AgentTaskClassifier
    {
        /// <summary>
        /// Classifies the request into a high-level agent task kind.
        /// </summary>
        public static AgentTaskKind Classify(string? promptMessage)
        {
            if (string.IsNullOrWhiteSpace(promptMessage))
                return AgentTaskKind.InspectionOnly;

            var normalized = promptMessage.ToLowerInvariant();
            if (ContainsPlanningIntent(normalized) && ContainsNoWriteDirective(normalized))
                return AgentTaskKind.PlanningOnly;

            if (ContainsWriteIntent(normalized))
                return ContainsCrossProjectIntent(normalized)
                    ? AgentTaskKind.CrossProjectWriteRequired
                    : AgentTaskKind.IdeWriteRequired;

            return ContainsPlanningIntent(normalized)
                ? AgentTaskKind.PlanningOnly
                : AgentTaskKind.InspectionOnly;
        }

        /// <summary>
        /// Returns <see langword="true"/> when the correction rule should force IDE writes before a final answer.
        /// </summary>
        public static bool ShouldEnforceIdeWrite(string? promptMessage, bool hasWrittenToIde)
        {
            var taskKind = Classify(promptMessage);
            return (taskKind == AgentTaskKind.IdeWriteRequired || taskKind == AgentTaskKind.CrossProjectWriteRequired)
                && !hasWrittenToIde;
        }

        private static bool ContainsWriteIntent(string normalizedPrompt)
        {
            return normalizedPrompt.Contains("fix")
                || normalizedPrompt.Contains("bug")
                || normalizedPrompt.Contains("implement")
                || normalizedPrompt.Contains("refactor")
                || normalizedPrompt.Contains("edit")
                || normalizedPrompt.Contains("change")
                || normalizedPrompt.Contains("modify")
                || normalizedPrompt.Contains("patch")
                || normalizedPrompt.Contains("rename")
                || normalizedPrompt.Contains("create")
                || normalizedPrompt.Contains("delete")
                || normalizedPrompt.Contains("update")
                || normalizedPrompt.Contains("新增")
                || normalizedPrompt.Contains("添加")
                || normalizedPrompt.Contains("修改")
                || normalizedPrompt.Contains("修复")
                || normalizedPrompt.Contains("重构")
                || normalizedPrompt.Contains("实现")
                || normalizedPrompt.Contains("创建")
                || normalizedPrompt.Contains("删除")
                || normalizedPrompt.Contains("更新")
                || normalizedPrompt.Contains("改代码")
                || normalizedPrompt.Contains("写代码");
        }

        private static bool ContainsPlanningIntent(string normalizedPrompt)
        {
            return normalizedPrompt.Contains("plan")
                || normalizedPrompt.Contains("planning")
                || normalizedPrompt.Contains("design")
                || normalizedPrompt.Contains("strategy")
                || normalizedPrompt.Contains("approach")
                || normalizedPrompt.Contains("步骤")
                || normalizedPrompt.Contains("计划")
                || normalizedPrompt.Contains("方案")
                || normalizedPrompt.Contains("设计")
                || normalizedPrompt.Contains("思路");
        }

        private static bool ContainsNoWriteDirective(string normalizedPrompt)
        {
            return normalizedPrompt.Contains("without changing code")
                || normalizedPrompt.Contains("without code changes")
                || normalizedPrompt.Contains("do not change code")
                || normalizedPrompt.Contains("don't change code")
                || normalizedPrompt.Contains("不要改代码")
                || normalizedPrompt.Contains("不要修改代码")
                || normalizedPrompt.Contains("先不要改代码")
                || normalizedPrompt.Contains("只给方案");
        }

        private static bool ContainsCrossProjectIntent(string normalizedPrompt)
        {
            return normalizedPrompt.Contains("solution")
                || normalizedPrompt.Contains("cross-project")
                || normalizedPrompt.Contains("across projects")
                || normalizedPrompt.Contains("multiple projects")
                || normalizedPrompt.Contains("entire repo")
                || normalizedPrompt.Contains("whole repo")
                || normalizedPrompt.Contains("whole solution")
                || normalizedPrompt.Contains("entire solution")
                || normalizedPrompt.Contains("跨项目")
                || normalizedPrompt.Contains("多个项目")
                || normalizedPrompt.Contains("整个解决方案")
                || normalizedPrompt.Contains("整个仓库")
                || normalizedPrompt.Contains("全仓库")
                || normalizedPrompt.Contains("全局");
        }
    }

    /// <summary>
    /// Represents the classified task intent for agent-mode requests.
    /// </summary>
    public enum AgentTaskKind
    {
        InspectionOnly,
        PlanningOnly,
        IdeWriteRequired,
        CrossProjectWriteRequired,
    }
}
