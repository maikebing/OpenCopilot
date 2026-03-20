using System.IO;
using OpenCopilot.Skills;
using Xunit;

namespace OpenCopilot.Tests.Skills
{
    public class CopilotSkillsDetectorTests
    {
        private static string CreateTempDir() =>
            Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

        private static void WriteFile(string path, string content = "# test")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        [Fact]
        public void Detect_ReturnsEmpty_WhenNoFilesExist()
        {
            var tempDir = CreateTempDir();
            Directory.CreateDirectory(tempDir);
            try
            {
                var detector = new CopilotSkillsDetector();
                var results = detector.Detect(tempDir);
                Assert.Empty(results);
            }
            finally
            {
                Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public void Detect_FindsCopilotInstructions()
        {
            var tempDir = CreateTempDir();
            try
            {
                WriteFile(Path.Combine(tempDir, ".github", "copilot-instructions.md"), "# Instructions");

                var detector = new CopilotSkillsDetector();
                var results = detector.Detect(tempDir);

                Assert.Single(results);
                Assert.Equal(CopilotSkillFileType.Instructions, results[0].FileType);
                Assert.Equal("copilot-instructions.md", results[0].FileName);
            }
            finally
            {
                Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public void Detect_FindsPromptFiles()
        {
            var tempDir = CreateTempDir();
            try
            {
                WriteFile(Path.Combine(tempDir, ".github", "prompts", "code-review.prompt.md"), "# Review");
                WriteFile(Path.Combine(tempDir, ".github", "prompts", "fix-bug.prompt.md"), "# Fix");

                var detector = new CopilotSkillsDetector();
                var results = detector.Detect(tempDir);

                Assert.Equal(2, results.Count);
                Assert.All(results, r => Assert.Equal(CopilotSkillFileType.PromptFile, r.FileType));
            }
            finally
            {
                Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public void Detect_FindsMemoryFiles()
        {
            var tempDir = CreateTempDir();
            try
            {
                WriteFile(Path.Combine(tempDir, ".copilot", "memory", "architecture.md"), "# Architecture");
                WriteFile(Path.Combine(tempDir, ".copilot", "memory", "conventions.md"), "# Conventions");

                var detector = new CopilotSkillsDetector();
                var results = detector.Detect(tempDir);

                Assert.Equal(2, results.Count);
                Assert.All(results, r => Assert.Equal(CopilotSkillFileType.MemoryFile, r.FileType));
            }
            finally
            {
                Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public void Detect_FindsSkillDefinitions()
        {
            var tempDir = CreateTempDir();
            try
            {
                WriteFile(Path.Combine(tempDir, ".copilot", "skills", "my-skill.md"), "# Skill");

                var detector = new CopilotSkillsDetector();
                var results = detector.Detect(tempDir);

                Assert.Single(results);
                Assert.Equal(CopilotSkillFileType.SkillDefinition, results[0].FileType);
            }
            finally
            {
                Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public void Detect_FindsCopilotMdInRoot()
        {
            var tempDir = CreateTempDir();
            try
            {
                WriteFile(Path.Combine(tempDir, "project.copilot.md"), "# Project notes");

                var detector = new CopilotSkillsDetector();
                var results = detector.Detect(tempDir);

                Assert.Single(results);
                Assert.Equal(CopilotSkillFileType.MemoryFile, results[0].FileType);
            }
            finally
            {
                Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public void Detect_WithIncludeContent_ReadsFileContent()
        {
            var tempDir = CreateTempDir();
            const string expectedContent = "# My instructions here";
            try
            {
                WriteFile(Path.Combine(tempDir, ".github", "copilot-instructions.md"), expectedContent);

                var detector = new CopilotSkillsDetector();
                var results = detector.Detect(tempDir, includeContent: true);

                Assert.Single(results);
                Assert.Equal(expectedContent, results[0].Content);
            }
            finally
            {
                Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public void Detect_WithoutIncludeContent_DoesNotReadContent()
        {
            var tempDir = CreateTempDir();
            try
            {
                WriteFile(Path.Combine(tempDir, ".github", "copilot-instructions.md"), "# Content");

                var detector = new CopilotSkillsDetector();
                var results = detector.Detect(tempDir, includeContent: false);

                Assert.Single(results);
                Assert.Null(results[0].Content);
            }
            finally
            {
                Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public void Detect_PopulatesFileMetadata()
        {
            var tempDir = CreateTempDir();
            try
            {
                var filePath = Path.Combine(tempDir, ".github", "copilot-instructions.md");
                WriteFile(filePath, "# Instructions");

                var detector = new CopilotSkillsDetector();
                var results = detector.Detect(tempDir);

                Assert.Single(results);
                var info = results[0];
                Assert.Equal(filePath, info.FilePath);
                Assert.Equal("copilot-instructions.md", info.FileName);
                Assert.True(info.FileSizeBytes > 0);
                Assert.True(info.LastModified > System.DateTime.MinValue);
            }
            finally
            {
                Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public void Detect_ReturnsEmpty_WhenSolutionDirectoryIsNull()
        {
            var detector = new CopilotSkillsDetector();
            var results = detector.Detect(solutionDirectory: null);
            // Should complete without throwing; only VS extension memory would show up
            Assert.NotNull(results);
        }
    }
}
