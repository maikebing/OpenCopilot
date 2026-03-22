using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OpenCopilot.Chat
{
    /// <summary>
    /// Locates repository instruction or memory files intended for AI coding assistants.
    /// </summary>
    public static class AiInstructionFileLocator
    {
        private static readonly string[] RootInstructionFiles =
        {
            ".github/copilot-instructions.md",
            ".opencopilot",
            "AGENTS.md",
            "agents.md",
            "CLAUDE.md",
            "claude.md",
            "GEMINI.md",
            "gemini.md",
            "CODEX.md",
            "Codex.md",
            "codex.md",
            ".opencopilot.md"
        };

        private static readonly string[] RootInstructionDirectories =
        {
            ".opencopilot",
            ".claude",
            ".claude/commands",
            ".claude/agents",
            ".claude/output-styles",
            ".gemini",
            ".codex",
            ".codex/memories",
            ".codex/skills"
        };

        /// <summary>
        /// Returns <see langword="true"/> when the path matches one of the configured root instruction rules.
        /// </summary>
        public static bool MatchesRelevantPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            var normalizedPath = Normalize(path);
            return RootInstructionFiles.Any(file => MatchesFile(normalizedPath, file))
                || RootInstructionDirectories.Any(directory => MatchesDirectory(normalizedPath, directory));
        }

        /// <summary>
        /// Finds AI instruction files under the current solution directory or the enclosing Git repository root.
        /// </summary>
        public static IReadOnlyList<string> FindRelevantFiles(string? solutionDirectory, string? userDirectory = null)
        {
            var discovered = new List<string>();
            foreach (var searchRoot in EnumerateSearchRoots(solutionDirectory, userDirectory))
            {
                foreach (var relativeFile in RootInstructionFiles)
                {
                    var fullPath = Path.Combine(searchRoot, relativeFile.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(fullPath) && !discovered.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
                        discovered.Add(fullPath);
                }

                foreach (var relativeDirectory in RootInstructionDirectories)
                {
                    var fullPath = Path.Combine(searchRoot, relativeDirectory.Replace('/', Path.DirectorySeparatorChar));
                    if (!Directory.Exists(fullPath))
                        continue;

                    foreach (var contentFile in EnumerateRelevantContentFiles(fullPath))
                    {
                        if (!discovered.Contains(contentFile, StringComparer.OrdinalIgnoreCase))
                            discovered.Add(contentFile);
                    }
                }
            }

            return discovered;
        }

        /// <summary>
        /// Resolves the effective discovery root by preferring the Git repository root over the solution directory.
        /// </summary>
        public static string ResolveSearchRoot(string? solutionDirectory)
        {
            if (string.IsNullOrWhiteSpace(solutionDirectory))
                return string.Empty;

            var directory = new DirectoryInfo(solutionDirectory);
            while (directory != null)
            {
                if (Directory.Exists(Path.Combine(directory.FullName, ".git"))
                    || File.Exists(Path.Combine(directory.FullName, ".git")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            return solutionDirectory;
        }

        private static bool MatchesFile(string normalizedPath, string relativeFile)
            => string.Equals(normalizedPath, Normalize(relativeFile), StringComparison.OrdinalIgnoreCase);

        private static bool MatchesDirectory(string normalizedPath, string relativeDirectory)
            => normalizedPath.StartsWith(Normalize(relativeDirectory).TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase);

        private static IEnumerable<string> EnumerateSearchRoots(string? solutionDirectory, string? userDirectory)
        {
            var discoveredRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var repositoryRoot = ResolveSearchRoot(solutionDirectory);
            if (!string.IsNullOrWhiteSpace(repositoryRoot) && Directory.Exists(repositoryRoot) && discoveredRoots.Add(repositoryRoot))
                yield return repositoryRoot;

            if (!string.IsNullOrWhiteSpace(solutionDirectory) && Directory.Exists(solutionDirectory) && discoveredRoots.Add(solutionDirectory))
                yield return solutionDirectory;

            var effectiveUserDirectory = string.IsNullOrWhiteSpace(userDirectory)
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : userDirectory;
            if (!string.IsNullOrWhiteSpace(effectiveUserDirectory)
                && Directory.Exists(effectiveUserDirectory)
                && discoveredRoots.Add(effectiveUserDirectory))
            {
                yield return effectiveUserDirectory;
            }
        }

        private static IEnumerable<string> EnumerateRelevantContentFiles(string directoryPath)
        {
            return Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories)
                .Where(IsRelevantContentFile)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
        }

        private static bool IsRelevantContentFile(string filePath)
        {
            var fileName = Path.GetFileName(filePath);
            var extension = Path.GetExtension(filePath);
            return string.Equals(extension, ".md", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".yaml", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".yml", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".toml", StringComparison.OrdinalIgnoreCase)
                || fileName.IndexOf("agent", StringComparison.OrdinalIgnoreCase) >= 0
                || fileName.IndexOf("skill", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string GetRelativePath(string rootPath, string fullPath)
        {
            var normalizedRoot = AppendDirectorySeparator(rootPath);
            var rootUri = new Uri(normalizedRoot, UriKind.Absolute);
            var fullUri = new Uri(fullPath, UriKind.Absolute);
            return Uri.UnescapeDataString(rootUri.MakeRelativeUri(fullUri).ToString());
        }

        private static string AppendDirectorySeparator(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            return path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                || path.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? path
                : path + Path.DirectorySeparatorChar;
        }

        private static string Normalize(string path)
            => (path ?? string.Empty).Replace('\\', '/').Trim();
    }
}
