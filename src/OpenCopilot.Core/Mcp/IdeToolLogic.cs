using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace OpenCopilot.Mcp
{
    public enum IdePatchOperationKind
    {
        Modify,
        Create,
        Delete,
        Copy,
        Rename,
    }

    public sealed class IdePatchBlock
    {
        public IdePatchBlock(string searchText, string replaceText)
        {
            SearchText = searchText ?? string.Empty;
            ReplaceText = replaceText ?? string.Empty;
        }

        public string SearchText { get; }

        public string ReplaceText { get; }
    }

    public sealed class IdePatchFileOperation
    {
        public IdePatchFileOperation(string path, IReadOnlyList<IdePatchBlock> blocks, IdePatchOperationKind kind, string? sourcePath = null, string? originalMode = null, string? updatedMode = null)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path is required.", nameof(path));
            if (blocks == null)
                throw new ArgumentNullException(nameof(blocks));

            Path = path;
            Blocks = blocks;
            Kind = kind;
            SourcePath = sourcePath;
            OriginalMode = originalMode;
            UpdatedMode = updatedMode;
        }

        public string Path { get; }

        public IReadOnlyList<IdePatchBlock> Blocks { get; }

        public IdePatchOperationKind Kind { get; }

        public string? SourcePath { get; }

        public string? OriginalMode { get; }

        public string? UpdatedMode { get; }
    }

    public sealed class IdeReadAnchorRequest
    {
        public IdeReadAnchorRequest(string anchorText, int beforeChars = 200, int afterChars = 200, int occurrence = 1, bool useRegex = false, bool ignoreCase = false)
        {
            AnchorText = anchorText ?? string.Empty;
            BeforeChars = beforeChars;
            AfterChars = afterChars;
            Occurrence = occurrence;
            UseRegex = useRegex;
            IgnoreCase = ignoreCase;
        }

        public string AnchorText { get; }

        public int BeforeChars { get; }

        public int AfterChars { get; }

        public int Occurrence { get; }

        public bool UseRegex { get; }

        public bool IgnoreCase { get; }
    }

    public sealed class IdePatchWrite
    {
        public IdePatchWrite(string path, string content, IdePatchOperationKind kind, string? sourcePath = null, string? originalMode = null, string? updatedMode = null, bool shouldWriteContent = true, bool preferMove = false)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path is required.", nameof(path));

            Path = path;
            Content = content ?? string.Empty;
            Kind = kind;
            SourcePath = sourcePath;
            OriginalMode = originalMode;
            UpdatedMode = updatedMode;
            ShouldWriteContent = shouldWriteContent;
            PreferMove = preferMove;
        }

        public string Path { get; }

        public string Content { get; }

        public IdePatchOperationKind Kind { get; }

        public string? SourcePath { get; }

        public string? OriginalMode { get; }

        public string? UpdatedMode { get; }

        public bool ShouldWriteContent { get; }

        public bool PreferMove { get; }
    }

    public static class IdeToolLogic
    {
        /// <summary>
        /// Extracts a character window around an anchor match.
        /// </summary>
        public static string ExtractAnchorWindow(string text, IdeReadAnchorRequest request)
        {
            if (text == null)
                throw new ArgumentNullException(nameof(text));
            if (request == null)
                throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(request.AnchorText))
                throw new ArgumentException("Anchor text is required.", nameof(request));
            if (request.Occurrence <= 0)
                throw new ArgumentOutOfRangeException(nameof(request), "Occurrence must be positive.");

            var anchorIndex = FindAnchorIndex(text, request, out var matchLength);
            if (anchorIndex < 0)
                throw new InvalidOperationException("The requested anchor text was not found.");

            var windowStart = Math.Max(0, anchorIndex - Math.Max(0, request.BeforeChars));
            var windowEnd = Math.Min(text.Length, anchorIndex + matchLength + Math.Max(0, request.AfterChars));
            return text.Substring(windowStart, windowEnd - windowStart);
        }

        /// <summary>
        /// Infers test filter tokens from Roslyn symbols declared in a source file.
        /// </summary>
        public static IReadOnlyList<string> InferTestFilterTokens(string sourceText, string filePath)
        {
            if (sourceText == null)
                throw new ArgumentNullException(nameof(sourceText));
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("File path is required.", nameof(filePath));

            var syntaxTree = CSharpSyntaxTree.ParseText(sourceText, path: filePath);
            var compilation = CSharpCompilation.Create(
                assemblyName: "OpenCopilot.SymbolInference",
                syntaxTrees: new[] { syntaxTree },
                references: new[]
                {
                    MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                    MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location)
                });

            var semanticModel = compilation.GetSemanticModel(syntaxTree, ignoreAccessibility: true);
            var root = syntaxTree.GetRoot();
            var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var node in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                if (semanticModel.GetDeclaredSymbol(node) is not INamedTypeSymbol namedType)
                    continue;

                var namespaceName = namedType.ContainingNamespace?.IsGlobalNamespace == false
                    ? namedType.ContainingNamespace.ToDisplayString()
                    : null;

                tokens.Add(namedType.Name);
                tokens.Add(namedType.Name + "Tests");

                if (!string.IsNullOrWhiteSpace(namespaceName))
                {
                    tokens.Add(namespaceName);
                    tokens.Add(namespaceName + "." + namedType.Name);
                    tokens.Add(namespaceName + "." + namedType.Name + "Tests");
                }

                foreach (var method in namedType.GetMembers().OfType<IMethodSymbol>())
                {
                    if (method.MethodKind != MethodKind.Ordinary)
                        continue;

                    var methodBaseName = TrimAsyncSuffix(method.Name);
                    tokens.Add(method.Name);
                    tokens.Add(methodBaseName);
                    tokens.Add(namedType.Name + "." + method.Name);
                    tokens.Add(methodBaseName + "Tests");
                    tokens.Add("When" + methodBaseName);
                    tokens.Add("When_" + methodBaseName);
                    tokens.Add("Should_" + methodBaseName);
                    tokens.Add("Given_" + methodBaseName);
                    tokens.Add(methodBaseName + "_Should");
                    tokens.Add(methodBaseName + "_When");
                    tokens.Add("Then_" + methodBaseName);
                    tokens.Add("Given_" + methodBaseName + "_When");
                    tokens.Add("Should_" + methodBaseName + "_When");
                    tokens.Add("When_" + methodBaseName + "_Then");
                    tokens.Add("Given_" + methodBaseName + "_Then");
                    tokens.Add("Given_" + methodBaseName + "_When_" + methodBaseName + "_Then");
                    tokens.Add(namedType.Name + "_" + methodBaseName);
                    tokens.Add(namedType.Name + "_Given_" + methodBaseName + "_When_" + methodBaseName + "_Then");
                    tokens.Add(namedType.Name + "_When_" + methodBaseName + "_Then");
                    if (!string.IsNullOrWhiteSpace(namespaceName))
                    {
                        tokens.Add(namespaceName + "." + namedType.Name + "." + method.Name);
                        tokens.Add(namespaceName + "." + namedType.Name + "_" + methodBaseName);
                        tokens.Add(namespaceName + "." + namedType.Name + "_Given_" + methodBaseName + "_When_" + methodBaseName + "_Then");
                    }
                }
            }

            return tokens.ToArray();
        }

        /// <summary>
        /// Parses SEARCH/REPLACE or unified-diff-like patch text into file operations.
        /// </summary>
        public static IReadOnlyList<IdePatchFileOperation> ParsePatchOperations(string? explicitPath, string patch)
        {
            if (string.IsNullOrWhiteSpace(patch))
                throw new ArgumentException("Patch text is required.", nameof(patch));

            const string beginPatchMarker = "*** Begin Patch";
            const string updateFileMarker = "*** Update File:";
            const string endPatchMarker = "*** End Patch";

            var normalized = patch.Replace("\r\n", "\n");
            if (normalized.IndexOf("--- ", StringComparison.Ordinal) >= 0
                && normalized.IndexOf("+++ ", StringComparison.Ordinal) >= 0)
            {
                return ParseUnifiedDiffFileOperations(normalized);
            }

            if (normalized.IndexOf(updateFileMarker, StringComparison.Ordinal) < 0)
            {
                if (string.IsNullOrWhiteSpace(explicitPath))
                    throw new InvalidOperationException("Parameter 'path' is required for single-file patches.");

                return new[] { new IdePatchFileOperation(explicitPath, ParsePatchBlocks(normalized), IdePatchOperationKind.Modify) };
            }

            var operations = new List<IdePatchFileOperation>();
            string? currentPath = null;
            var currentPatch = new StringBuilder();
            foreach (var line in normalized.Split('\n'))
            {
                if (string.Equals(line, beginPatchMarker, StringComparison.Ordinal) || string.Equals(line, endPatchMarker, StringComparison.Ordinal))
                    continue;

                if (line.StartsWith(updateFileMarker, StringComparison.Ordinal))
                {
                    if (!string.IsNullOrWhiteSpace(currentPath))
                        operations.Add(new IdePatchFileOperation(currentPath, ParsePatchBlocks(currentPatch.ToString()), IdePatchOperationKind.Modify));

                    currentPath = line.Substring(updateFileMarker.Length).Trim();
                    currentPatch.Clear();
                    continue;
                }

                currentPatch.AppendLine(line);
            }

            if (!string.IsNullOrWhiteSpace(currentPath))
                operations.Add(new IdePatchFileOperation(currentPath, ParsePatchBlocks(currentPatch.ToString()), IdePatchOperationKind.Modify));

            if (operations.Count == 0)
                throw new InvalidOperationException("No valid file operations were found in the patch.");

            return operations;
        }

        /// <summary>
        /// Applies patch blocks to the provided file content.
        /// </summary>
        public static string ApplySearchReplacePatch(string content, IReadOnlyList<IdePatchBlock> blocks)
        {
            if (content == null)
                throw new ArgumentNullException(nameof(content));
            if (blocks == null)
                throw new ArgumentNullException(nameof(blocks));

            var updated = content;
            foreach (var block in blocks)
            {
                if (string.IsNullOrEmpty(block.SearchText))
                    throw new InvalidOperationException("Patch SEARCH block was not found in the target file.");

                var index = FindUniqueOccurrence(updated, block.SearchText);
                if (index < 0)
                    throw new InvalidOperationException("Patch SEARCH block was not found in the target file.");

                updated = updated.Substring(0, index) + block.ReplaceText + updated.Substring(index + block.SearchText.Length);
            }

            return updated;
        }

        /// <summary>
        /// Builds the full content for a file-creation patch.
        /// </summary>
        public static string BuildCreatedFileContent(IReadOnlyList<IdePatchBlock> blocks)
        {
            if (blocks == null)
                throw new ArgumentNullException(nameof(blocks));
            return string.Join(string.Empty, blocks.Select(block => block.ReplaceText));
        }

        /// <summary>
        /// Resolves the best project-system container path for a target file.
        /// </summary>
        public static string? ResolveProjectAttachmentContainerPath(string targetPath, string? relatedPath, IReadOnlyList<string> knownItemPaths, IReadOnlyList<string> projectRootPaths)
        {
            if (string.IsNullOrWhiteSpace(targetPath))
                throw new ArgumentException("Target path is required.", nameof(targetPath));
            if (knownItemPaths == null)
                throw new ArgumentNullException(nameof(knownItemPaths));
            if (projectRootPaths == null)
                throw new ArgumentNullException(nameof(projectRootPaths));

            var fullTargetPath = System.IO.Path.GetFullPath(targetPath);
            var targetDirectory = System.IO.Path.GetDirectoryName(fullTargetPath);
            if (string.IsNullOrWhiteSpace(targetDirectory))
                return null;

            if (!string.IsNullOrWhiteSpace(relatedPath))
            {
                var fullRelatedPath = System.IO.Path.GetFullPath(relatedPath);
                if (AreSamePath(System.IO.Path.GetDirectoryName(fullRelatedPath), targetDirectory))
                    return fullRelatedPath;
            }

            foreach (var knownItemPath in knownItemPaths.Where(path => !string.IsNullOrWhiteSpace(path)).OrderByDescending(path => path.Length))
            {
                var fullKnownItemPath = System.IO.Path.GetFullPath(knownItemPath);
                if (AreSamePath(fullKnownItemPath, targetDirectory))
                    return fullKnownItemPath;
            }

            string? bestProjectRoot = null;
            foreach (var projectRootPath in projectRootPaths.Where(path => !string.IsNullOrWhiteSpace(path)))
            {
                var fullProjectRootPath = System.IO.Path.GetFullPath(projectRootPath);
                if (!IsPathUnderRoot(fullTargetPath, fullProjectRootPath))
                    continue;

                if (bestProjectRoot == null || fullProjectRootPath.Length > bestProjectRoot.Length)
                    bestProjectRoot = fullProjectRootPath;
            }

            return bestProjectRoot;
        }

        /// <summary>
        /// Determines whether an active document should be synchronized through the editor for a patch write.
        /// </summary>
        public static bool ShouldTryOpenDocumentSync(string? activeDocumentPath, IdePatchWrite write)
        {
            if (write == null)
                throw new ArgumentNullException(nameof(write));
            if (string.IsNullOrWhiteSpace(activeDocumentPath) || !write.ShouldWriteContent)
                return false;

            switch (write.Kind)
            {
                case IdePatchOperationKind.Modify:
                case IdePatchOperationKind.Copy:
                case IdePatchOperationKind.Rename:
                    return AreSamePath(activeDocumentPath, write.Path);
                default:
                    return false;
            }
        }

        /// <summary>
        /// Resolves parsed patch operations into filesystem writes.
        /// </summary>
        public static IReadOnlyList<IdePatchWrite> PreparePatchWrites(string? solutionDirectory, IReadOnlyList<IdePatchFileOperation> operations)
        {
            if (operations == null)
                throw new ArgumentNullException(nameof(operations));

            var writes = new List<IdePatchWrite>();
            var targetPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var operation in operations)
            {
                var resolvedPath = ResolvePatchPath(operation.Path, solutionDirectory);
                ValidateDirectoryTargetConflict(resolvedPath, targetPaths);
                if (!targetPaths.Add(resolvedPath))
                    throw new InvalidOperationException("Patch contains duplicate target paths.");

                switch (operation.Kind)
                {
                    case IdePatchOperationKind.Create:
                        var createdContent = BuildCreatedFileContent(operation.Blocks);
                        if (ShouldSkipExistingTargetWrite(resolvedPath, operation.Kind, null, createdContent))
                            break;
                        ValidateTargetAvailability(resolvedPath, operation.Kind, null, createdContent);
                        writes.Add(new IdePatchWrite(resolvedPath, createdContent, IdePatchOperationKind.Create, null, operation.OriginalMode, operation.UpdatedMode));
                        break;
                    case IdePatchOperationKind.Delete:
                        writes.Add(new IdePatchWrite(resolvedPath, string.Empty, IdePatchOperationKind.Delete, null, operation.OriginalMode, operation.UpdatedMode));
                        break;
                    case IdePatchOperationKind.Copy:
                    case IdePatchOperationKind.Rename:
                        var sourcePath = ResolvePatchPath(operation.SourcePath ?? operation.Path, solutionDirectory);
                        var sourceContent = System.IO.File.ReadAllText(sourcePath);
                        var copiedContent = operation.Blocks.Count == 0 ? sourceContent : ApplySearchReplacePatch(sourceContent, operation.Blocks);
                        if (operation.Kind == IdePatchOperationKind.Rename
                            && System.IO.File.Exists(resolvedPath)
                            && !AreSamePath(resolvedPath, sourcePath)
                            && string.Equals(System.IO.File.ReadAllText(resolvedPath), copiedContent, StringComparison.Ordinal))
                        {
                            writes.Add(new IdePatchWrite(resolvedPath, copiedContent, operation.Kind, sourcePath, operation.OriginalMode, operation.UpdatedMode, shouldWriteContent: false));
                            break;
                        }

                        if (ShouldSkipExistingTargetWrite(resolvedPath, operation.Kind, sourcePath, copiedContent))
                            break;
                        ValidateTargetAvailability(resolvedPath, operation.Kind, sourcePath, copiedContent);
                        writes.Add(new IdePatchWrite(
                            resolvedPath,
                            copiedContent,
                            operation.Kind,
                            sourcePath,
                            operation.OriginalMode,
                            operation.UpdatedMode,
                            preferMove: operation.Kind == IdePatchOperationKind.Rename
                                && operation.Blocks.Count == 0
                                && !AreSamePath(sourcePath, resolvedPath)
                                && !System.IO.File.Exists(resolvedPath)
                                && !System.IO.Directory.Exists(resolvedPath)));
                        break;
                    default:
                        var original = System.IO.File.ReadAllText(resolvedPath);
                        var updated = ApplySearchReplacePatch(original, operation.Blocks);
                        writes.Add(new IdePatchWrite(resolvedPath, updated, IdePatchOperationKind.Modify, null, operation.OriginalMode, operation.UpdatedMode));
                        break;
                }
            }

            return writes;
        }

        /// <summary>
        /// Applies prepared filesystem writes for patch operations.
        /// </summary>
        public static void ApplyPatchWrites(IReadOnlyList<IdePatchWrite> writes)
        {
            if (writes == null)
                throw new ArgumentNullException(nameof(writes));

            foreach (var write in writes)
            {
                if (write.Kind == IdePatchOperationKind.Delete)
                {
                    if (System.IO.File.Exists(write.Path))
                        System.IO.File.Delete(write.Path);
                    continue;
                }

                var directory = System.IO.Path.GetDirectoryName(write.Path);
                if (!string.IsNullOrWhiteSpace(directory) && !System.IO.Directory.Exists(directory))
                    System.IO.Directory.CreateDirectory(directory);

                PrepareWritablePathIfSupported(write.Path, write.UpdatedMode);

                if (write.Kind == IdePatchOperationKind.Rename && write.PreferMove && !string.IsNullOrWhiteSpace(write.SourcePath))
                    System.IO.File.Move(write.SourcePath, write.Path);
                else if (write.ShouldWriteContent)
                    System.IO.File.WriteAllText(write.Path, write.Content);

                ApplyModeIfSupported(write.Path, write.UpdatedMode);

                if (write.Kind == IdePatchOperationKind.Rename
                    && !string.IsNullOrWhiteSpace(write.SourcePath)
                    && !AreSamePath(write.SourcePath, write.Path)
                    && System.IO.File.Exists(write.SourcePath))
                {
                    System.IO.File.Delete(write.SourcePath);
                }
            }
        }

        private static void PrepareWritablePathIfSupported(string path, string? updatedMode)
        {
            if (string.IsNullOrWhiteSpace(updatedMode)
                || !RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                || !System.IO.File.Exists(path)
                || !TryGetWindowsAttributePlan(updatedMode, out var hasWriteBit, out _))
            {
                return;
            }

            if (!hasWriteBit)
                return;

            var attributes = System.IO.File.GetAttributes(path);
            if (attributes.HasFlag(System.IO.FileAttributes.ReadOnly))
                System.IO.File.SetAttributes(path, attributes & ~System.IO.FileAttributes.ReadOnly);
        }

        private static int FindAnchorIndex(string input, IdeReadAnchorRequest request, out int matchLength)
        {
            if (request.UseRegex)
            {
                var options = request.IgnoreCase ? RegexOptions.IgnoreCase : RegexOptions.None;
                var matches = Regex.Matches(input, request.AnchorText, options);
                if (matches.Count < request.Occurrence)
                {
                    matchLength = 0;
                    return -1;
                }

                matchLength = matches[request.Occurrence - 1].Length;
                return matches[request.Occurrence - 1].Index;
            }

            matchLength = request.AnchorText.Length;
            var haystack = request.IgnoreCase ? input.ToLowerInvariant() : input;
            var needle = request.IgnoreCase ? request.AnchorText.ToLowerInvariant() : request.AnchorText;
            return FindNthOccurrence(haystack, needle, request.Occurrence);
        }

        private static int FindNthOccurrence(string input, string value, int occurrence)
        {
            var index = -1;
            for (var current = 0; current < occurrence; current++)
            {
                index = input.IndexOf(value, index + 1, StringComparison.Ordinal);
                if (index < 0)
                    return -1;
            }

            return index;
        }

        private static IReadOnlyList<IdePatchBlock> ParsePatchBlocks(string patch)
        {
            if ((patch ?? string.Empty).IndexOf("<<<<<<< SEARCH", StringComparison.Ordinal) >= 0)
                return ParseSearchReplaceBlocks(patch);

            if ((patch ?? string.Empty).IndexOf("@@", StringComparison.Ordinal) >= 0)
                return ParseUnifiedDiffBlocks(patch);

            throw new InvalidOperationException("No supported patch blocks were found.");
        }

        private static IReadOnlyList<IdePatchBlock> ParseSearchReplaceBlocks(string patch)
        {
            const string searchMarker = "<<<<<<< SEARCH";
            const string dividerMarker = "=======";
            const string replaceMarker = ">>>>>>> REPLACE";

            var blocks = new List<IdePatchBlock>();
            var normalized = patch.Replace("\r\n", "\n");
            var lines = normalized.Split('\n');

            for (var index = 0; index < lines.Length; index++)
            {
                if (!string.Equals(lines[index], searchMarker, StringComparison.Ordinal))
                    continue;

                var searchLines = new List<string>();
                index++;
                while (index < lines.Length && !string.Equals(lines[index], dividerMarker, StringComparison.Ordinal))
                {
                    searchLines.Add(lines[index]);
                    index++;
                }

                if (index >= lines.Length)
                    throw new InvalidOperationException("Patch block is missing the ======= divider.");

                var replaceLines = new List<string>();
                index++;
                while (index < lines.Length && !string.Equals(lines[index], replaceMarker, StringComparison.Ordinal))
                {
                    replaceLines.Add(lines[index]);
                    index++;
                }

                if (index >= lines.Length)
                    throw new InvalidOperationException("Patch block is missing the >>>>>>> REPLACE marker.");

                var searchText = string.Join("\n", searchLines);
                if (string.IsNullOrEmpty(searchText))
                    throw new InvalidOperationException("Patch SEARCH blocks must not be empty.");

                blocks.Add(new IdePatchBlock(searchText, string.Join("\n", replaceLines)));
            }

            if (blocks.Count == 0)
                throw new InvalidOperationException("No valid SEARCH/REPLACE patch blocks were found.");

            return blocks;
        }

        private static IReadOnlyList<IdePatchBlock> ParseUnifiedDiffBlocks(string patch)
        {
            var blocks = new List<IdePatchBlock>();
            var normalized = patch.Replace("\r\n", "\n");
            var lines = normalized.Split('\n');

            for (var index = 0; index < lines.Length; index++)
            {
                if (!lines[index].StartsWith("@@", StringComparison.Ordinal))
                    continue;

                var searchLines = new List<string>();
                var replaceLines = new List<string>();
                var hasChange = false;
                index++;

                while (index < lines.Length && !lines[index].StartsWith("@@", StringComparison.Ordinal) && !lines[index].StartsWith("*** Update File:", StringComparison.Ordinal) && !string.Equals(lines[index], "*** End Patch", StringComparison.Ordinal))
                {
                    var line = lines[index];
                    if (line.StartsWith("\\", StringComparison.Ordinal))
                    {
                        index++;
                        continue;
                    }

                    if (line.Length == 0)
                        throw new InvalidOperationException("Unified diff hunk lines must start with a prefix character.");

                    var prefix = line[0];
                    var content = line.Length > 1 ? line.Substring(1) : string.Empty;
                    switch (prefix)
                    {
                        case ' ':
                            searchLines.Add(content);
                            replaceLines.Add(content);
                            break;
                        case '-':
                            searchLines.Add(content);
                            hasChange = true;
                            break;
                        case '+':
                            replaceLines.Add(content);
                            hasChange = true;
                            break;
                        default:
                            throw new InvalidOperationException("Unsupported unified diff line prefix.");
                    }

                    index++;
                }

                if (!hasChange)
                    throw new InvalidOperationException("Unified diff hunks must contain at least one added or removed line.");

                blocks.Add(new IdePatchBlock(string.Join("\n", searchLines), string.Join("\n", replaceLines)));
                index--;
            }

            if (blocks.Count == 0)
                throw new InvalidOperationException("No valid unified diff hunks were found.");

            return blocks;
        }

        private static IReadOnlyList<IdePatchFileOperation> ParseUnifiedDiffFileOperations(string patch)
        {
            var operations = new List<IdePatchFileOperation>();
            var normalized = patch.Replace("\r\n", "\n");
            var lines = normalized.Split('\n');

            for (var index = 0; index < lines.Length; index++)
            {
                if (lines[index].StartsWith("diff --git ", StringComparison.Ordinal))
                    continue;

                if (!lines[index].StartsWith("--- ", StringComparison.Ordinal))
                    continue;

                string? renameFrom = null;
                string? renameTo = null;
                string? copyFrom = null;
                string? copyTo = null;
                string? originalMode = null;
                string? updatedMode = null;

                var lookahead = index - 1;
                while (lookahead >= 0 && !lines[lookahead].StartsWith("diff --git ", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(lines[lookahead]))
                {
                    if (lines[lookahead].StartsWith("old mode ", StringComparison.Ordinal))
                        originalMode = lines[lookahead].Substring("old mode ".Length).Trim();
                    if (lines[lookahead].StartsWith("new mode ", StringComparison.Ordinal))
                        updatedMode = lines[lookahead].Substring("new mode ".Length).Trim();
                    if (lines[lookahead].StartsWith("new file mode ", StringComparison.Ordinal))
                        updatedMode = lines[lookahead].Substring("new file mode ".Length).Trim();
                    if (lines[lookahead].StartsWith("deleted file mode ", StringComparison.Ordinal))
                        originalMode = lines[lookahead].Substring("deleted file mode ".Length).Trim();
                    if (lines[lookahead].StartsWith("rename from ", StringComparison.Ordinal))
                        renameFrom = lines[lookahead].Substring("rename from ".Length).Trim();
                    if (lines[lookahead].StartsWith("rename to ", StringComparison.Ordinal))
                        renameTo = lines[lookahead].Substring("rename to ".Length).Trim();
                    if (lines[lookahead].StartsWith("copy from ", StringComparison.Ordinal))
                        copyFrom = lines[lookahead].Substring("copy from ".Length).Trim();
                    if (lines[lookahead].StartsWith("copy to ", StringComparison.Ordinal))
                        copyTo = lines[lookahead].Substring("copy to ".Length).Trim();
                    lookahead--;
                }

                var originalPath = NormalizeDiffPath(lines[index].Substring(4));
                index++;
                while (index < lines.Length && !lines[index].StartsWith("+++ ", StringComparison.Ordinal))
                    index++;

                if (index >= lines.Length)
                    throw new InvalidOperationException("Unified diff is missing a +++ file header.");

                var updatedPath = NormalizeDiffPath(lines[index].Substring(4));
                var operationPath = !string.IsNullOrWhiteSpace(copyTo)
                    ? copyTo
                    : !string.IsNullOrWhiteSpace(renameTo)
                        ? renameTo
                        : string.IsNullOrWhiteSpace(updatedPath) ? originalPath : updatedPath;
                if (string.IsNullOrWhiteSpace(operationPath))
                    throw new InvalidOperationException("Unified diff file headers did not contain a usable path.");

                var kind = !string.IsNullOrWhiteSpace(copyFrom) && !string.IsNullOrWhiteSpace(copyTo)
                    ? IdePatchOperationKind.Copy
                    : !string.IsNullOrWhiteSpace(renameFrom) && !string.IsNullOrWhiteSpace(renameTo)
                        ? IdePatchOperationKind.Rename
                        : string.IsNullOrWhiteSpace(originalPath)
                            ? IdePatchOperationKind.Create
                            : string.IsNullOrWhiteSpace(updatedPath)
                                ? IdePatchOperationKind.Delete
                                : IdePatchOperationKind.Modify;

                index++;
                var hunkBuilder = new StringBuilder();
                while (index < lines.Length
                    && !lines[index].StartsWith("diff --git ", StringComparison.Ordinal)
                    && !lines[index].StartsWith("--- ", StringComparison.Ordinal))
                {
                    hunkBuilder.AppendLine(lines[index]);
                    index++;
                }

                var hunkText = hunkBuilder.ToString();
                var blocks = string.IsNullOrWhiteSpace(hunkText)
                    ? Array.Empty<IdePatchBlock>()
                    : ParseUnifiedDiffBlocks(hunkText);
                var sourcePath = !string.IsNullOrWhiteSpace(copyFrom)
                    ? copyFrom
                    : string.IsNullOrWhiteSpace(renameFrom) ? originalPath : renameFrom;
                operations.Add(new IdePatchFileOperation(operationPath, blocks, kind, sourcePath, originalMode, updatedMode));
                index--;
            }

            if (operations.Count == 0)
                throw new InvalidOperationException("No valid unified diff file operations were found.");

            return operations;
        }

        private static int FindUniqueOccurrence(string input, string searchText)
        {
            var firstIndex = input.IndexOf(searchText, StringComparison.Ordinal);
            if (firstIndex < 0)
                return -1;

            var secondIndex = input.IndexOf(searchText, firstIndex + 1, StringComparison.Ordinal);
            if (secondIndex >= 0)
                throw new InvalidOperationException("Patch SEARCH block matched multiple locations. Add more surrounding context.");

            return firstIndex;
        }

        private static string NormalizeDiffPath(string rawHeaderValue)
        {
            var candidate = (rawHeaderValue ?? string.Empty).Split('\t')[0].Trim();
            if (string.Equals(candidate, "/dev/null", StringComparison.OrdinalIgnoreCase))
                return string.Empty;
            if (candidate.StartsWith("a/", StringComparison.Ordinal) || candidate.StartsWith("b/", StringComparison.Ordinal))
                candidate = candidate.Substring(2);

            return candidate;
        }

        private static string ResolvePatchPath(string path, string? solutionDirectory)
        {
            if (System.IO.Path.IsPathRooted(path))
                return path;
            if (string.IsNullOrWhiteSpace(solutionDirectory))
                return System.IO.Path.GetFullPath(path);

            return System.IO.Path.GetFullPath(System.IO.Path.Combine(solutionDirectory, path.Replace('/', System.IO.Path.DirectorySeparatorChar)));
        }

        private static string TrimAsyncSuffix(string methodName)
        {
            return methodName != null && methodName.EndsWith("Async", StringComparison.Ordinal) && methodName.Length > "Async".Length
                ? methodName.Substring(0, methodName.Length - "Async".Length)
                : methodName ?? string.Empty;
        }

        private static bool AreSamePath(string? left, string? right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
                return false;

            return string.Equals(
                System.IO.Path.GetFullPath(left),
                System.IO.Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }

        private static void ValidateTargetAvailability(string targetPath, IdePatchOperationKind kind, string? sourcePath, string? desiredContent)
        {
            if (!System.IO.File.Exists(targetPath) && !System.IO.Directory.Exists(targetPath))
                return;

            if (kind == IdePatchOperationKind.Rename && System.IO.Directory.Exists(targetPath))
                throw new InvalidOperationException("Rename target already exists as a directory.");

            if (kind == IdePatchOperationKind.Rename && System.IO.File.Exists(targetPath) && !AreSamePath(targetPath, sourcePath))
                throw new InvalidOperationException("Rename target already exists with different content.");

            if ((kind == IdePatchOperationKind.Create || kind == IdePatchOperationKind.Copy)
                && System.IO.File.Exists(targetPath)
                && string.Equals(System.IO.File.ReadAllText(targetPath), desiredContent ?? string.Empty, StringComparison.Ordinal))
            {
                return;
            }

            if ((kind == IdePatchOperationKind.Copy || kind == IdePatchOperationKind.Rename) && AreSamePath(targetPath, sourcePath))
                return;

            throw new InvalidOperationException("Patch target already exists.");
        }

        private static bool ShouldSkipExistingTargetWrite(string targetPath, IdePatchOperationKind kind, string? sourcePath, string desiredContent)
        {
            if (!System.IO.File.Exists(targetPath))
                return false;

            if ((kind == IdePatchOperationKind.Create || kind == IdePatchOperationKind.Copy)
                && string.Equals(System.IO.File.ReadAllText(targetPath), desiredContent, StringComparison.Ordinal))
            {
                return true;
            }

            return (kind == IdePatchOperationKind.Copy || kind == IdePatchOperationKind.Rename) && AreSamePath(targetPath, sourcePath);
        }

        private static void ValidateDirectoryTargetConflict(string targetPath, IReadOnlyCollection<string> targetPaths)
        {
            foreach (var existingTarget in targetPaths)
            {
                if (IsParentOrChildPath(existingTarget, targetPath))
                    throw new InvalidOperationException("Patch contains directory-level target conflicts.");
            }

            var currentDirectory = System.IO.Path.GetDirectoryName(targetPath);
            while (!string.IsNullOrWhiteSpace(currentDirectory))
            {
                if (System.IO.File.Exists(currentDirectory))
                    throw new InvalidOperationException("Patch target conflicts with an existing file in its directory chain.");

                currentDirectory = System.IO.Path.GetDirectoryName(currentDirectory);
            }
        }

        private static bool IsParentOrChildPath(string left, string right)
        {
            if (AreSamePath(left, right))
                return false;

            var normalizedLeft = EnsureTrailingSeparator(System.IO.Path.GetFullPath(left));
            var normalizedRight = EnsureTrailingSeparator(System.IO.Path.GetFullPath(right));
            return normalizedLeft.StartsWith(normalizedRight, StringComparison.OrdinalIgnoreCase)
                || normalizedRight.StartsWith(normalizedLeft, StringComparison.OrdinalIgnoreCase);
        }

        private static string EnsureTrailingSeparator(string path)
        {
            return path.EndsWith(System.IO.Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? path
                : path + System.IO.Path.DirectorySeparatorChar;
        }

        private static bool IsPathUnderRoot(string path, string rootPath)
        {
            return AreSamePath(path, rootPath)
                || System.IO.Path.GetFullPath(path).StartsWith(System.IO.Path.GetFullPath(rootPath) + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private static void ApplyModeIfSupported(string path, string? updatedMode)
        {
            if (string.IsNullOrWhiteSpace(updatedMode) || !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return;

            if (!TryGetWindowsAttributePlan(updatedMode, out var hasWriteBit, out var shouldSetArchive))
                return;

            try
            {
                ApplyWindowsFileAttributePlan(path, hasWriteBit, shouldSetArchive);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new InvalidOperationException($"Failed to apply Windows file attributes for mode '{updatedMode}' to '{path}'.", ex);
            }
            catch (System.IO.IOException ex)
            {
                throw new InvalidOperationException($"Failed to apply Windows file attributes for mode '{updatedMode}' to '{path}'.", ex);
            }
        }

        private static bool TryGetWindowsAttributePlan(string modeText, out bool hasWriteBit, out bool shouldSetArchive)
        {
            hasWriteBit = false;
            shouldSetArchive = false;
            if (!TryParseUnixMode(modeText, out var mode))
                return false;

            hasWriteBit = (mode & Convert.ToInt32("222", 8)) != 0;
            var fileTypeBits = mode & Convert.ToInt32("170000", 8);
            shouldSetArchive = fileTypeBits == 0 || fileTypeBits == Convert.ToInt32("100000", 8);
            return true;
        }

        private static void ApplyWindowsFileAttributePlan(string path, bool hasWriteBit, bool shouldSetArchive)
        {
            var attributes = System.IO.File.GetAttributes(path);
            if (hasWriteBit)
                attributes &= ~System.IO.FileAttributes.ReadOnly;
            else
                attributes |= System.IO.FileAttributes.ReadOnly;

            if (shouldSetArchive && !attributes.HasFlag(System.IO.FileAttributes.Directory))
                attributes |= System.IO.FileAttributes.Archive;

            System.IO.File.SetAttributes(path, attributes);
        }

        private static bool TryParseUnixMode(string modeText, out int mode)
        {
            try
            {
                mode = Convert.ToInt32(modeText, 8);
                return true;
            }
            catch (FormatException)
            {
                mode = 0;
                return false;
            }
            catch (OverflowException)
            {
                mode = 0;
                return false;
            }
        }

    }
}
