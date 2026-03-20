using System;
using System.Collections.Generic;
using System.IO;

namespace OpenCopilot.Skills
{
    /// <summary>
    /// Detects Copilot skill, memory, and instruction files in a solution directory
    /// and optionally in the VS extension memory location.
    /// </summary>
    public class CopilotSkillsDetector
    {
        public List<CopilotSkillInfo> Detect(string? solutionDirectory = null, bool includeContent = false)
        {
            var result = new List<CopilotSkillInfo>();

            if (!string.IsNullOrWhiteSpace(solutionDirectory))
            {
                CheckFile(Path.Combine(solutionDirectory, ".github", "copilot-instructions.md"),
                    CopilotSkillFileType.Instructions, result, includeContent);

                ScanDirectory(Path.Combine(solutionDirectory, ".github", "prompts"),
                    "*.prompt.md", CopilotSkillFileType.PromptFile, result, includeContent);

                ScanDirectory(Path.Combine(solutionDirectory, ".copilot", "memory"),
                    "*.md", CopilotSkillFileType.MemoryFile, result, includeContent);

                ScanDirectory(Path.Combine(solutionDirectory, ".copilot", "skills"),
                    "*.*", CopilotSkillFileType.SkillDefinition, result, includeContent);

                foreach (var f in SafeGetFiles(solutionDirectory, "*.copilot.md"))
                    AddFile(f, CopilotSkillFileType.MemoryFile, result, includeContent);
            }

            DetectVsExtensionMemory(result, includeContent);

            return result;
        }

        private void DetectVsExtensionMemory(List<CopilotSkillInfo> result, bool includeContent)
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var vsDir = Path.Combine(localAppData, "Microsoft", "VisualStudio");
            if (!Directory.Exists(vsDir)) return;

            foreach (var versionDir in SafeGetDirectories(vsDir, "*"))
            {
                var extDir = Path.Combine(versionDir, "Extensions");
                if (!Directory.Exists(extDir)) continue;

                foreach (var extSubDir in SafeGetDirectories(extDir, "GitHub.Copilot*"))
                {
                    var memDir = Path.Combine(extSubDir, "Memory");
                    ScanDirectory(memDir, "*.*", CopilotSkillFileType.MemoryFile, result, includeContent);
                }
            }
        }

        private void CheckFile(string path, CopilotSkillFileType type, List<CopilotSkillInfo> result, bool includeContent)
        {
            if (File.Exists(path))
                AddFile(path, type, result, includeContent);
        }

        private void ScanDirectory(string directory, string pattern, CopilotSkillFileType type, List<CopilotSkillInfo> result, bool includeContent)
        {
            foreach (var f in SafeGetFiles(directory, pattern))
                AddFile(f, type, result, includeContent);
        }

        private static void AddFile(string path, CopilotSkillFileType type, List<CopilotSkillInfo> result, bool includeContent)
        {
            try
            {
                var info = new FileInfo(path);
                var skill = new CopilotSkillInfo
                {
                    FilePath = path,
                    FileName = info.Name,
                    FileType = type,
                    FileSizeBytes = info.Length,
                    LastModified = info.LastWriteTime
                };

                if (includeContent)
                    skill.Content = File.ReadAllText(path);

                result.Add(skill);
            }
            catch { /* skip unreadable files */ }
        }

        private static IEnumerable<string> SafeGetFiles(string directory, string pattern)
        {
            if (!Directory.Exists(directory)) yield break;
            string[] files;
            try { files = Directory.GetFiles(directory, pattern); }
            catch { yield break; }
            foreach (var f in files) yield return f;
        }

        private static IEnumerable<string> SafeGetDirectories(string directory, string pattern)
        {
            if (!Directory.Exists(directory)) yield break;
            string[] dirs;
            try { dirs = Directory.GetDirectories(directory, pattern); }
            catch { yield break; }
            foreach (var d in dirs) yield return d;
        }
    }
}
