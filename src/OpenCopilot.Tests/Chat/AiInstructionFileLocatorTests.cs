using OpenCopilot.Chat;
using System;
using System.IO;
using Xunit;

namespace OpenCopilot.Tests.Chat
{
    public class AiInstructionFileLocatorTests
    {
        [Fact]
        public void ResolveSearchRoot_ReturnsGitRoot_WhenSolutionIsInsideRepository()
        {
            var gitRoot = Path.Combine(Path.GetTempPath(), "OpenCopilot-AiLocator", Guid.NewGuid().ToString("N"));
            var solutionDirectory = Path.Combine(gitRoot, "src", "App");
            Directory.CreateDirectory(Path.Combine(gitRoot, ".git"));
            Directory.CreateDirectory(solutionDirectory);

            var resolvedRoot = AiInstructionFileLocator.ResolveSearchRoot(solutionDirectory);

            Assert.Equal(gitRoot, resolvedRoot);
        }

        [Fact]
        public void FindRelevantFiles_ReturnsConfiguredRootFiles_WhenFilesExistAtRoot()
        {
            var solutionDirectory = Path.Combine(Path.GetTempPath(), "OpenCopilot-AiLocator", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(solutionDirectory, ".github"));
            var copilotInstructionsPath = Path.Combine(solutionDirectory, ".github", "copilot-instructions.md");
            var agentsPath = Path.Combine(solutionDirectory, "AGENTS.md");
            File.WriteAllText(copilotInstructionsPath, "instructions");
            File.WriteAllText(agentsPath, "agents");

            var files = AiInstructionFileLocator.FindRelevantFiles(solutionDirectory);

            Assert.Equal(copilotInstructionsPath, files[0]);
            Assert.Equal(agentsPath, files[1]);
        }

        [Fact]
        public void FindRelevantFiles_ReturnsMarkdownFiles_WhenConfiguredDirectoryContainsMarkdownFiles()
        {
            var solutionDirectory = Path.Combine(Path.GetTempPath(), "OpenCopilot-AiLocator", Guid.NewGuid().ToString("N"));
            var memoryDirectory = Path.Combine(solutionDirectory, ".codex", "memories");
            var siteConfigPath = Path.Combine(memoryDirectory, "site-config.md");
            var contentAuthoringPath = Path.Combine(memoryDirectory, "content-authoring.md");
            Directory.CreateDirectory(memoryDirectory);
            File.WriteAllText(siteConfigPath, "config");
            File.WriteAllText(contentAuthoringPath, "content");

            var files = AiInstructionFileLocator.FindRelevantFiles(solutionDirectory);

            Assert.Contains(siteConfigPath, files);
            Assert.Contains(contentAuthoringPath, files);
        }

        [Fact]
        public void FindRelevantFiles_ReturnsUserDirectoryAgentAndSkillFiles_WhenConfiguredDirectoriesExistUnderUserRoot()
        {
            var solutionDirectory = Path.Combine(Path.GetTempPath(), "OpenCopilot-AiLocator", Guid.NewGuid().ToString("N"));
            var userDirectory = Path.Combine(Path.GetTempPath(), "OpenCopilot-AiLocator-User", Guid.NewGuid().ToString("N"));
            var agentsDirectory = Path.Combine(userDirectory, ".claude", "agents");
            var skillsDirectory = Path.Combine(userDirectory, ".codex", "skills");
            var agentFilePath = Path.Combine(agentsDirectory, "c-pro.agent.json");
            var skillFilePath = Path.Combine(skillsDirectory, "site.skills.yaml");
            Directory.CreateDirectory(agentsDirectory);
            Directory.CreateDirectory(skillsDirectory);
            File.WriteAllText(agentFilePath, "agent");
            File.WriteAllText(skillFilePath, "skill");

            var files = AiInstructionFileLocator.FindRelevantFiles(solutionDirectory, userDirectory);

            Assert.Contains(agentFilePath, files);
            Assert.Contains(skillFilePath, files);
        }

        [Fact]
        public void FindRelevantFiles_ReturnsJsonYamlAndTomlMemoryFiles_WhenConfiguredDirectoriesContainThem()
        {
            var solutionDirectory = Path.Combine(Path.GetTempPath(), "OpenCopilot-AiLocator", Guid.NewGuid().ToString("N"));
            var userDirectory = Path.Combine(Path.GetTempPath(), "OpenCopilot-AiLocator-User", Guid.NewGuid().ToString("N"));
            var memoriesDirectory = Path.Combine(userDirectory, ".codex", "memories");
            var jsonPath = Path.Combine(memoriesDirectory, "frontend-skill.json");
            var yamlPath = Path.Combine(memoriesDirectory, "backend-memory.yaml");
            var tomlPath = Path.Combine(memoriesDirectory, "workspace-skill.toml");
            Directory.CreateDirectory(memoriesDirectory);
            File.WriteAllText(jsonPath, "{}");
            File.WriteAllText(yamlPath, "name: backend");
            File.WriteAllText(tomlPath, "name='workspace'");

            var files = AiInstructionFileLocator.FindRelevantFiles(solutionDirectory, userDirectory);

            Assert.Contains(jsonPath, files);
            Assert.Contains(yamlPath, files);
            Assert.Contains(tomlPath, files);
        }
    }
}
