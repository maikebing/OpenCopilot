using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TextManager.Interop;
using OpenCopilot.Mcp;

namespace OpenCopilot.Mcp
{
    /// <summary>
    /// Registers built-in VS IDE file editing tools into an <see cref="McpService"/>.
    /// All tools use only System.IO and VS IDE APIs â€?no external processes.
    /// </summary>
    public class VsFileEditingTools
    {
        private readonly AsyncPackage _package;

        public VsFileEditingTools(AsyncPackage package)
        {
            _package = package;
        }

        public void RegisterInto(McpService mcpService)
        {
            mcpService.RegisterBuiltInTool(
                new McpTool
                {
                    Name = "vs_read_file",
                    Description = "Read a file. Parameters: path (string), startLine (int, optional, 1-based), endLine (int, optional), includeLineNumbers (bool, optional).",
                    ServerName = "BuiltIn",
                    IsBuiltIn = true
                },
                ReadFileAsync);

            mcpService.RegisterBuiltInTool(
                new McpTool
                {
                    Name = "vs_list_project_files",
                    Description = "List all files in the current solution.",
                    ServerName = "BuiltIn",
                    IsBuiltIn = true
                },
                ListProjectFilesAsync);

            mcpService.RegisterBuiltInTool(
                new McpTool
                {
                    Name = "vs_create_file",
                    Description = "Create a new file with the given content. Parameters: path (string), content (string).",
                    ServerName = "BuiltIn",
                    IsBuiltIn = true
                },
                CreateFileAsync);

            mcpService.RegisterBuiltInTool(
                new McpTool
                {
                    Name = "vs_edit_file",
                    Description = "Replace one or more occurrences of text in a file. Parameters: path (string), oldText (string), newText (string), occurrence (int, optional, default 1; 0 = all).",
                    ServerName = "BuiltIn",
                    IsBuiltIn = true
                },
                EditFileAsync);

            mcpService.RegisterBuiltInTool(
                new McpTool
                {
                    Name = "vs_replace_file_content",
                    Description = "Replace the entire content of a file. Parameters: path (string), content (string).",
                    ServerName = "BuiltIn",
                    IsBuiltIn = true
                },
                ReplaceFileContentAsync);

            mcpService.RegisterBuiltInTool(
                new McpTool
                {
                    Name = "vs_apply_patch",
                    Description = "Apply SEARCH/REPLACE patch blocks. Parameters: patch (string), path (string, optional for single-file patches). Supports multi-file patches with *** Update File: relative/or/absolute/path sections.",
                    ServerName = "BuiltIn",
                    IsBuiltIn = true
                },
                ApplyPatchAsync);

            mcpService.RegisterBuiltInTool(
                new McpTool
                {
                    Name = "vs_reencode_file",
                    Description = "Re-encode a file from one text encoding to another. Parameters: path (string), fromEncoding (string), toEncoding (string).",
                    ServerName = "BuiltIn",
                    IsBuiltIn = true
                },
                ReencodeFileAsync);

            mcpService.RegisterBuiltInTool(
                new McpTool
                {
                    Name = "vs_build_solution",
                    Description = "Build the current solution and report whether it succeeded.",
                    ServerName = "BuiltIn",
                    IsBuiltIn = true
                },
                BuildSolutionAsync);

            mcpService.RegisterBuiltInTool(
                new McpTool
                {
                    Name = "vs_get_build_errors",
                    Description = "Read the current Visual Studio build errors from the build output pane.",
                    ServerName = "BuiltIn",
                    IsBuiltIn = true
                },
                GetBuildErrorsAsync);

            mcpService.RegisterBuiltInTool(
                new McpTool
                {
                    Name = "vs_run_tests",
                    Description = "Run unit tests for the current solution or a specific test project. Parameters: path (string, optional), filter (string, optional).",
                    ServerName = "BuiltIn",
                    IsBuiltIn = true
                },
                RunTestsAsync);
        }

        private static Task<McpToolCallResult> ReadFileAsync(Dictionary<string, object?> args)
        {
            try
            {
                var path = GetString(args, "path");
                if (string.IsNullOrWhiteSpace(path))
                    return Task.FromResult(McpToolCallResult.Failure("Parameter 'path' is required."));

                var startLine = GetInt(args, "startLine", 1);
                var endLine = GetInt(args, "endLine", 0);
                var includeLineNumbers = GetBool(args, "includeLineNumbers", false);
                if (startLine <= 1 && endLine <= 0)
                    return Task.FromResult(McpToolCallResult.Success(File.ReadAllText(path)));

                var lines = File.ReadAllLines(path);
                var normalizedStart = Math.Max(1, startLine);
                var normalizedEnd = endLine <= 0 ? lines.Length : Math.Min(lines.Length, endLine);
                if (normalizedStart > normalizedEnd)
                    return Task.FromResult(McpToolCallResult.Failure("The requested line range is invalid."));

                var builder = new StringBuilder();
                for (var index = normalizedStart - 1; index < normalizedEnd; index++)
                {
                    builder.AppendLine(includeLineNumbers
                        ? $"{index + 1}: {lines[index]}"
                        : lines[index]);
                }

                return Task.FromResult(McpToolCallResult.Success(builder.ToString().TrimEnd()));
            }
            catch (Exception ex)
            {
                return Task.FromResult(McpToolCallResult.Failure(ex.Message));
            }
        }

        private async Task<McpToolCallResult> ApplyPatchAsync(Dictionary<string, object?> args)
        {
            try
            {
                var patch = GetString(args, "patch");
                var path = GetString(args, "path");

                if (string.IsNullOrWhiteSpace(patch))
                    return McpToolCallResult.Failure("Parameter 'patch' is required.");

                var operations = ParsePatchOperations(path, patch);
                var pendingWrites = new List<PatchFileWrite>();
                foreach (var operation in operations)
                {
                    var original = File.ReadAllText(operation.Path);
                    var updated = ApplySearchReplacePatch(original, operation.Blocks);
                    if (string.Equals(original, updated, StringComparison.Ordinal))
                        return McpToolCallResult.Failure($"Patch produced no changes for '{operation.Path}'.");

                    pendingWrites.Add(new PatchFileWrite(operation.Path, updated));
                }

                foreach (var write in pendingWrites)
                {
                    var appliedViaEditor = await TryReplaceOpenDocumentAsync(write.Path, write.Content);
                    if (!appliedViaEditor)
                        File.WriteAllText(write.Path, write.Content);
                }

                return McpToolCallResult.Success("Patch applied: " + string.Join(", ", pendingWrites.Select(write => write.Path)));
            }
            catch (Exception ex)
            {
                return McpToolCallResult.Failure(ex.Message);
            }
        }

        private async Task<McpToolCallResult> ListProjectFilesAsync(Dictionary<string, object?> args)
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var dte = await _package.GetServiceAsync(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                if (dte?.Solution == null)
                    return McpToolCallResult.Failure("No solution is open.");

                var files = new System.Text.StringBuilder();
                CollectProjectItems(dte.Solution.Projects, files);
                return McpToolCallResult.Success(files.ToString().TrimEnd());
            }
            catch (Exception ex)
            {
                return McpToolCallResult.Failure(ex.Message);
            }
        }

        private async Task<McpToolCallResult> RunTestsAsync(Dictionary<string, object?> args)
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var dte = await _package.GetServiceAsync(typeof(EnvDTE.DTE)) as EnvDTE80.DTE2;
                var solutionPath = dte?.Solution?.FullName;
                if (string.IsNullOrWhiteSpace(solutionPath))
                    return McpToolCallResult.Failure("No solution is open.");

                var solutionDirectory = Path.GetDirectoryName(solutionPath);
                if (string.IsNullOrWhiteSpace(solutionDirectory))
                    return McpToolCallResult.Failure("Cannot determine the solution directory.");

                var targetPath = GetString(args, "path");
                if (string.IsNullOrWhiteSpace(targetPath))
                    targetPath = DetectDefaultTestTarget(solutionDirectory, solutionPath);

                var filter = GetString(args, "filter");
                var argumentsText = new StringBuilder();
                argumentsText.Append("test ");
                argumentsText.Append(QuoteArgument(targetPath));
                argumentsText.Append(" --no-restore");
                if (!string.IsNullOrWhiteSpace(filter))
                {
                    argumentsText.Append(" --filter ");
                    argumentsText.Append(QuoteArgument(filter));
                }

                var processResult = await RunProcessAsync("dotnet", argumentsText.ToString(), solutionDirectory).ConfigureAwait(false);
                var output = TrimToolOutput(processResult.Output);
                return processResult.ExitCode == 0
                    ? McpToolCallResult.Success("Tests passed.\n" + output)
                    : McpToolCallResult.Failure("Tests failed.\n" + output);
            }
            catch (Exception ex)
            {
                return McpToolCallResult.Failure(ex.Message);
            }
        }

        private async Task<McpToolCallResult> BuildSolutionAsync(Dictionary<string, object?> args)
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var dte = await _package.GetServiceAsync(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                if (dte?.Solution?.SolutionBuild == null)
                    return McpToolCallResult.Failure("No solution is open.");

                dte.Solution.SolutionBuild.Build(true);
                var buildErrors = dte.Solution.SolutionBuild.LastBuildInfo;
                return buildErrors == 0
                    ? McpToolCallResult.Success("Build succeeded.")
                    : McpToolCallResult.Failure($"Build failed with {buildErrors} errors.");
            }
            catch (Exception ex)
            {
                return McpToolCallResult.Failure(ex.Message);
            }
        }

        private async Task<McpToolCallResult> GetBuildErrorsAsync(Dictionary<string, object?> args)
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var dte = await _package.GetServiceAsync(typeof(EnvDTE.DTE)) as EnvDTE80.DTE2;
                var buildPane = FindBuildOutputPane(dte);
                if (buildPane?.TextDocument == null)
                    return McpToolCallResult.Failure("Visual Studio build output is unavailable.");

                var editPoint = buildPane.TextDocument.StartPoint.CreateEditPoint();
                var outputText = editPoint.GetText(buildPane.TextDocument.EndPoint);
                var errorLines = new List<string>();
                foreach (var line in outputText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (line.IndexOf(": error ", StringComparison.OrdinalIgnoreCase) >= 0)
                        errorLines.Add(line.Trim());
                }

                return errorLines.Count == 0
                    ? McpToolCallResult.Success("No build errors.")
                    : McpToolCallResult.Success(string.Join(Environment.NewLine, errorLines));
            }
            catch (Exception ex)
            {
                return McpToolCallResult.Failure(ex.Message);
            }
        }

        private async Task<McpToolCallResult> CreateFileAsync(Dictionary<string, object?> args)
        {
            try
            {
                var path = GetString(args, "path");
                var content = GetString(args, "content") ?? string.Empty;

                if (string.IsNullOrWhiteSpace(path))
                    return McpToolCallResult.Failure("Parameter 'path' is required.");

                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                // Write the file
                File.WriteAllText(path, content, Encoding.UTF8);

                // Add to project if possible
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var dte = await _package.GetServiceAsync(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                dte?.ItemOperations?.AddExistingItem(path);

                return McpToolCallResult.Success($"File created: {path}");
            }
            catch (Exception ex)
            {
                return McpToolCallResult.Failure(ex.Message);
            }
        }

        private async Task<McpToolCallResult> EditFileAsync(Dictionary<string, object?> args)
        {
            try
            {
                var path = GetString(args, "path");
                var oldText = GetString(args, "oldText");
                var newText = GetString(args, "newText") ?? string.Empty;
                var occurrence = GetInt(args, "occurrence", 1);

                if (string.IsNullOrWhiteSpace(path))
                    return McpToolCallResult.Failure("Parameter 'path' is required.");
                if (oldText == null)
                    return McpToolCallResult.Failure("Parameter 'oldText' is required.");

                // Try to apply through the open editor first
                var appliedViaEditor = await TryEditOpenDocumentAsync(path, oldText, newText, occurrence);
                if (!appliedViaEditor)
                {
                    // Fall back to direct file I/O
                    var original = File.ReadAllText(path);
                    string updated;
                    if (occurrence == 0)
                    {
                        updated = original.Replace(oldText, newText);
                    }
                    else
                    {
                        updated = ReplaceNthOccurrence(original, oldText, newText, occurrence);
                    }
                    File.WriteAllText(path, updated);
                }

                return McpToolCallResult.Success($"File edited: {path}");
            }
            catch (Exception ex)
            {
                return McpToolCallResult.Failure(ex.Message);
            }
        }

        private async Task<McpToolCallResult> ReplaceFileContentAsync(Dictionary<string, object?> args)
        {
            try
            {
                var path = GetString(args, "path");
                var content = GetString(args, "content") ?? string.Empty;

                if (string.IsNullOrWhiteSpace(path))
                    return McpToolCallResult.Failure("Parameter 'path' is required.");

                // Try to apply through the open editor first
                var appliedViaEditor = await TryReplaceOpenDocumentAsync(path, content);
                if (!appliedViaEditor)
                    File.WriteAllText(path, content);

                return McpToolCallResult.Success($"File replaced: {path}");
            }
            catch (Exception ex)
            {
                return McpToolCallResult.Failure(ex.Message);
            }
        }

        private static Task<McpToolCallResult> ReencodeFileAsync(Dictionary<string, object?> args)
        {
            try
            {
                var path = GetString(args, "path");
                var fromEnc = GetString(args, "fromEncoding");
                var toEnc = GetString(args, "toEncoding");

                if (string.IsNullOrWhiteSpace(path))
                    return Task.FromResult(McpToolCallResult.Failure("Parameter 'path' is required."));
                if (string.IsNullOrWhiteSpace(fromEnc))
                    return Task.FromResult(McpToolCallResult.Failure("Parameter 'fromEncoding' is required."));
                if (string.IsNullOrWhiteSpace(toEnc))
                    return Task.FromResult(McpToolCallResult.Failure("Parameter 'toEncoding' is required."));

                var sourceEncoding = Encoding.GetEncoding(fromEnc);
                var targetEncoding = Encoding.GetEncoding(toEnc);

                var bytes = File.ReadAllBytes(path);
                var text = sourceEncoding.GetString(bytes);
                File.WriteAllText(path, text, targetEncoding);

                return Task.FromResult(McpToolCallResult.Success($"Re-encoded {path} from {fromEnc} to {toEnc}."));
            }
            catch (Exception ex)
            {
                return Task.FromResult(McpToolCallResult.Failure(ex.Message));
            }
        }

        // --- VS editor helpers ---

        private async Task<bool> TryEditOpenDocumentAsync(string path, string oldText, string newText, int occurrence)
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var dte = await _package.GetServiceAsync(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                if (!AreSamePath(dte?.ActiveDocument?.FullName, path))
                    return false;

                var textManager = await _package.GetServiceAsync(typeof(SVsTextManager)) as IVsTextManager2;
                if (textManager == null) return false;

                textManager.GetActiveView2(1, null, (uint)_VIEWFRAMETYPE.vftCodeWindow, out var activeView);
                if (activeView == null) return false;

                activeView.GetBuffer(out var buffer);
                if (buffer == null) return false;

                buffer.GetLastLineIndex(out var lastLine, out var lastCol);
                buffer.GetLineText(0, 0, lastLine, lastCol, out var fullText);

                string updatedText;
                if (occurrence == 0)
                    updatedText = fullText.Replace(oldText, newText);
                else
                    updatedText = ReplaceNthOccurrence(fullText, oldText, newText, occurrence);

                if (updatedText == fullText) return false;

                var pUpdatedText = System.Runtime.InteropServices.Marshal.StringToCoTaskMemUni(updatedText); try { buffer.ReplaceLines(0, 0, lastLine, lastCol, pUpdatedText, updatedText.Length, new TextSpan[1]); } finally { System.Runtime.InteropServices.Marshal.FreeCoTaskMem(pUpdatedText); }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private async Task<bool> TryReplaceOpenDocumentAsync(string path, string content)
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var dte = await _package.GetServiceAsync(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                if (!AreSamePath(dte?.ActiveDocument?.FullName, path))
                    return false;

                var textManager = await _package.GetServiceAsync(typeof(SVsTextManager)) as IVsTextManager2;
                if (textManager == null) return false;

                textManager.GetActiveView2(1, null, (uint)_VIEWFRAMETYPE.vftCodeWindow, out var activeView);
                if (activeView == null) return false;

                activeView.GetBuffer(out var buffer);
                if (buffer == null) return false;

                buffer.GetLastLineIndex(out var lastLine, out var lastCol);
                var pContent = System.Runtime.InteropServices.Marshal.StringToCoTaskMemUni(content); try { buffer.ReplaceLines(0, 0, lastLine, lastCol, pContent, content.Length, new TextSpan[1]); } finally { System.Runtime.InteropServices.Marshal.FreeCoTaskMem(pContent); }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void CollectProjectItems(EnvDTE.Projects projects, System.Text.StringBuilder sb)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (projects == null) return;
            foreach (EnvDTE.Project project in projects)
                CollectItems(project.ProjectItems, sb);
        }

        private static void CollectItems(EnvDTE.ProjectItems? items, System.Text.StringBuilder sb)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (items == null) return;
            foreach (EnvDTE.ProjectItem item in items)
            {
                for (short i = 1; i <= item.FileCount; i++)
                {
                    try { sb.AppendLine(item.FileNames[i]); } catch { /* skip */ }
                }
                CollectItems(item.ProjectItems, sb);
            }
        }

        // --- Utility helpers ---

        private static string? GetString(Dictionary<string, object?> args, string key)
            => args.TryGetValue(key, out var val) ? val?.ToString() : null;

        private static int GetInt(Dictionary<string, object?> args, string key, int defaultValue)
        {
            if (!args.TryGetValue(key, out var val) || val == null) return defaultValue;
            return int.TryParse(val.ToString(), out var result) ? result : defaultValue;
        }

        private static bool GetBool(Dictionary<string, object?> args, string key, bool defaultValue)
        {
            if (!args.TryGetValue(key, out var val) || val == null)
                return defaultValue;

            return bool.TryParse(val.ToString(), out var result) ? result : defaultValue;
        }

        private static string ReplaceNthOccurrence(string input, string oldText, string newText, int n)
        {
            var index = -1;
            for (var i = 0; i < n; i++)
            {
                index = input.IndexOf(oldText, index + 1, StringComparison.Ordinal);
                if (index < 0) return input;
            }
            return input.Substring(0, index) + newText + input.Substring(index + oldText.Length);
        }

        private static string ApplySearchReplacePatch(string content, IReadOnlyList<SearchReplacePatchBlock> blocks)
        {
            var updated = content;
            foreach (var block in blocks)
            {
                var index = FindUniqueOccurrence(updated, block.SearchText);
                if (index < 0)
                    throw new InvalidOperationException("Patch SEARCH block was not found in the target file.");

                updated = updated.Substring(0, index) + block.ReplaceText + updated.Substring(index + block.SearchText.Length);
            }

            return updated;
        }

        private static IReadOnlyList<SearchReplacePatchBlock> ParsePatchBlocks(string patch)
        {
            const string searchMarker = "<<<<<<< SEARCH";
            const string dividerMarker = "=======";
            const string replaceMarker = ">>>>>>> REPLACE";

            var blocks = new List<SearchReplacePatchBlock>();
            var normalized = (patch ?? string.Empty).Replace("\r\n", "\n");
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

                blocks.Add(new SearchReplacePatchBlock(searchText, string.Join("\n", replaceLines)));
            }

            if (blocks.Count == 0)
                throw new InvalidOperationException("No valid SEARCH/REPLACE patch blocks were found.");

            return blocks;
        }

        private static IReadOnlyList<PatchFileOperation> ParsePatchOperations(string? explicitPath, string patch)
        {
            const string beginPatchMarker = "*** Begin Patch";
            const string updateFileMarker = "*** Update File:";
            const string endPatchMarker = "*** End Patch";

            var normalized = (patch ?? string.Empty).Replace("\r\n", "\n");
            if (normalized.IndexOf(updateFileMarker, StringComparison.Ordinal) < 0)
            {
                if (string.IsNullOrWhiteSpace(explicitPath))
                    throw new InvalidOperationException("Parameter 'path' is required for single-file patches.");

                return new[] { new PatchFileOperation(explicitPath, ParsePatchBlocks(normalized)) };
            }

            var operations = new List<PatchFileOperation>();
            string? currentPath = null;
            var currentPatch = new StringBuilder();
            foreach (var line in normalized.Split('\n'))
            {
                if (string.Equals(line, beginPatchMarker, StringComparison.Ordinal) || string.Equals(line, endPatchMarker, StringComparison.Ordinal))
                    continue;

                if (line.StartsWith(updateFileMarker, StringComparison.Ordinal))
                {
                    if (!string.IsNullOrWhiteSpace(currentPath))
                        operations.Add(new PatchFileOperation(currentPath, ParsePatchBlocks(currentPatch.ToString())));

                    currentPath = line.Substring(updateFileMarker.Length).Trim();
                    currentPatch.Clear();
                    continue;
                }

                currentPatch.AppendLine(line);
            }

            if (!string.IsNullOrWhiteSpace(currentPath))
                operations.Add(new PatchFileOperation(currentPath, ParsePatchBlocks(currentPatch.ToString())));

            if (operations.Count == 0)
                throw new InvalidOperationException("No valid file operations were found in the patch.");

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

        private static bool AreSamePath(string? left, string? right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
                return false;

            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }

        private static EnvDTE.OutputWindowPane? FindBuildOutputPane(EnvDTE80.DTE2? dte)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var panes = dte?.ToolWindows?.OutputWindow?.OutputWindowPanes;
            if (panes == null)
                return null;

            foreach (EnvDTE.OutputWindowPane pane in panes)
            {
                var name = pane.Name ?? string.Empty;
                if (name.IndexOf("build", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("Éú³É", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return pane;
                }
            }

            return null;
        }

        private static string DetectDefaultTestTarget(string solutionDirectory, string solutionPath)
        {
            var testProjects = Directory.EnumerateFiles(solutionDirectory, "*.Tests.csproj", SearchOption.AllDirectories).ToList();
            return testProjects.Count == 1 ? testProjects[0] : solutionPath;
        }

        private static async Task<ProcessResult> RunProcessAsync(string fileName, string arguments, string workingDirectory)
        {
            return await Task.Run(() =>
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    WorkingDirectory = workingDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                using (var process = new Process { StartInfo = startInfo })
                {
                    process.Start();
                    var output = process.StandardOutput.ReadToEnd();
                    var error = process.StandardError.ReadToEnd();
                    if (!process.WaitForExit((int)TimeSpan.FromMinutes(3).TotalMilliseconds))
                    {
                        try { process.Kill(); } catch { }
                        return new ProcessResult(-1, "Test process timed out.");
                    }

                    return new ProcessResult(process.ExitCode, string.Join(Environment.NewLine, new[] { output, error }).Trim());
                }
            }).ConfigureAwait(false);
        }

        private static string QuoteArgument(string value)
            => '"' + (value ?? string.Empty).Replace("\"", "\\\"") + '"';

        private static string TrimToolOutput(string output)
        {
            if (string.IsNullOrWhiteSpace(output))
                return string.Empty;

            const int maxLength = 4000;
            return output.Length <= maxLength
                ? output.Trim()
                : output.Substring(output.Length - maxLength, maxLength).Trim();
        }

        private sealed class SearchReplacePatchBlock
        {
            public SearchReplacePatchBlock(string searchText, string replaceText)
            {
                SearchText = searchText;
                ReplaceText = replaceText;
            }

            public string SearchText { get; }

            public string ReplaceText { get; }
        }

        private sealed class PatchFileOperation
        {
            public PatchFileOperation(string path, IReadOnlyList<SearchReplacePatchBlock> blocks)
            {
                Path = path;
                Blocks = blocks;
            }

            public string Path { get; }

            public IReadOnlyList<SearchReplacePatchBlock> Blocks { get; }
        }

        private sealed class PatchFileWrite
        {
            public PatchFileWrite(string path, string content)
            {
                Path = path;
                Content = content;
            }

            public string Path { get; }

            public string Content { get; }
        }

        private sealed class ProcessResult
        {
            public ProcessResult(int exitCode, string output)
            {
                ExitCode = exitCode;
                Output = output;
            }

            public int ExitCode { get; }

            public string Output { get; }
        }
    }
}
