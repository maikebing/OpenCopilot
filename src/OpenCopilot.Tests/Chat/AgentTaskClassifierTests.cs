using OpenCopilot.Chat;
using Xunit;

namespace OpenCopilot.Tests.Chat
{
    public class AgentTaskClassifierTests
    {
        [Fact]
        public void Classify_ReturnsIdeWriteRequired_WhenPromptRequestsImplementation()
        {
            var taskKind = AgentTaskClassifier.Classify("Please implement the missing validation in the current project.");

            Assert.Equal(AgentTaskKind.IdeWriteRequired, taskKind);
        }

        [Fact]
        public void Classify_ReturnsInspectionOnly_WhenPromptRequestsExplanationOnly()
        {
            var taskKind = AgentTaskClassifier.Classify("Explain how this service works without changing code.");

            Assert.Equal(AgentTaskKind.InspectionOnly, taskKind);
        }

        [Fact]
        public void Classify_ReturnsPlanningOnly_WhenPromptRequestsPlanOnly()
        {
            var taskKind = AgentTaskClassifier.Classify("请先给我一个重构计划和设计方案，不要改代码。");

            Assert.Equal(AgentTaskKind.PlanningOnly, taskKind);
        }

        [Fact]
        public void Classify_ReturnsCrossProjectWriteRequired_WhenPromptRequestsSolutionWideChanges()
        {
            var taskKind = AgentTaskClassifier.Classify("Implement the rename across multiple projects in the whole solution.");

            Assert.Equal(AgentTaskKind.CrossProjectWriteRequired, taskKind);
        }

        [Fact]
        public void ShouldEnforceIdeWrite_ReturnsTrue_WhenWriteTaskHasNotBeenApplied()
        {
            var shouldEnforce = AgentTaskClassifier.ShouldEnforceIdeWrite("修复这个 bug 并改代码", hasWrittenToIde: false);

            Assert.True(shouldEnforce);
        }

        [Fact]
        public void ShouldEnforceIdeWrite_ReturnsFalse_WhenWriteTaskHasAlreadyBeenApplied()
        {
            var shouldEnforce = AgentTaskClassifier.ShouldEnforceIdeWrite("修复这个 bug 并改代码", hasWrittenToIde: true);

            Assert.False(shouldEnforce);
        }

        [Fact]
        public void ShouldEnforceIdeWrite_ReturnsTrue_WhenCrossProjectWriteTaskHasNotBeenApplied()
        {
            var shouldEnforce = AgentTaskClassifier.ShouldEnforceIdeWrite("在整个解决方案里跨项目更新这个接口实现", hasWrittenToIde: false);

            Assert.True(shouldEnforce);
        }
    }
}
