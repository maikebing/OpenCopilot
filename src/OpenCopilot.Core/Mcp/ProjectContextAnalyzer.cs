using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace OpenCopilot.Mcp
{
    /// <summary>
    /// Analyzes the current project and active document to infer language and runtime information.
    /// </summary>
    public static class ProjectContextAnalyzer
    {
        /// <summary>
        /// Builds a context summary from the current project file and active document.
        /// </summary>
        public static ProjectContextAnalysis Analyze(string? projectFilePath, string? projectFileContent, string? activeDocumentPath)
        {
            return new ProjectContextAnalysis(
                projectFilePath,
                activeDocumentPath,
                InferProjectLanguage(projectFilePath, activeDocumentPath),
                InferDocumentLanguage(activeDocumentPath),
                InferRuntime(projectFilePath, projectFileContent));
        }

        private static string InferProjectLanguage(string? projectFilePath, string? activeDocumentPath)
        {
            var projectExtension = Path.GetExtension(projectFilePath ?? string.Empty);
            switch (projectExtension?.ToLowerInvariant())
            {
                case ".csproj":
                    return "C#";
                case ".fsproj":
                    return "F#";
                case ".vbproj":
                    return "Visual Basic";
                case ".vcxproj":
                    return "C++";
            }

            return InferDocumentLanguage(activeDocumentPath);
        }

        private static string InferDocumentLanguage(string? activeDocumentPath)
        {
            if (string.IsNullOrWhiteSpace(activeDocumentPath))
                return string.Empty;

            var normalizedPath = activeDocumentPath.Trim().Replace("\\", "/");
            if (normalizedPath.EndsWith(".xaml.cs", StringComparison.OrdinalIgnoreCase))
                return "C#";
            if (normalizedPath.EndsWith(".xaml.vb", StringComparison.OrdinalIgnoreCase))
                return "Visual Basic";

            switch (Path.GetExtension(activeDocumentPath).ToLowerInvariant())
            {
                case ".cs":
                    return "C#";
                case ".fs":
                case ".fsx":
                    return "F#";
                case ".vb":
                    return "Visual Basic";
                case ".xaml":
                    return "XAML";
                case ".js":
                    return "JavaScript";
                case ".ts":
                    return "TypeScript";
                case ".py":
                    return "Python";
                case ".cpp":
                case ".c":
                case ".h":
                case ".hpp":
                    return "C++";
                default:
                    return string.Empty;
            }
        }

        private static string InferRuntime(string? projectFilePath, string? projectFileContent)
        {
            var projectExtension = Path.GetExtension(projectFilePath ?? string.Empty);
            if (string.Equals(projectExtension, ".vcxproj", StringComparison.OrdinalIgnoreCase))
                return "native";

            if (string.IsNullOrWhiteSpace(projectFileContent))
                return string.Empty;

            try
            {
                var document = XDocument.Parse(projectFileContent);
                var namespaceName = document.Root?.Name.Namespace ?? XNamespace.None;
                var targetFramework = document.Descendants(namespaceName + "TargetFramework")
                    .Select(element => element.Value?.Trim())
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
                if (!string.IsNullOrWhiteSpace(targetFramework))
                    return targetFramework ?? string.Empty;

                var targetFrameworks = document.Descendants(namespaceName + "TargetFrameworks")
                    .Select(element => element.Value)
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
                if (string.IsNullOrWhiteSpace(targetFrameworks))
                    return string.Empty;

                return string.Join(", ", targetFrameworks
                    .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(value => value.Trim())
                    .Where(value => value.Length > 0));
            }
            catch (InvalidOperationException)
            {
                return string.Empty;
            }
            catch (System.Xml.XmlException)
            {
                return string.Empty;
            }
        }
    }

    /// <summary>
    /// Represents the inferred IDE project context for the agent.
    /// </summary>
    public sealed class ProjectContextAnalysis
    {
        public ProjectContextAnalysis(string? projectFilePath, string? activeDocumentPath, string projectLanguage, string documentLanguage, string runtime)
        {
            ProjectFilePath = projectFilePath ?? string.Empty;
            ActiveDocumentPath = activeDocumentPath ?? string.Empty;
            ProjectLanguage = projectLanguage ?? string.Empty;
            DocumentLanguage = documentLanguage ?? string.Empty;
            Runtime = runtime ?? string.Empty;
        }

        public string ProjectFilePath { get; }

        public string ActiveDocumentPath { get; }

        public string ProjectLanguage { get; }

        public string DocumentLanguage { get; }

        public string Runtime { get; }
    }
}
