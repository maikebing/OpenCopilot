using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TextManager.Interop;
using OpenCopilot.Mcp;
using OpenCopilot.ToolWindows;

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
                    Description = "Read a file. Parameters: path (string), startLine (int, optional, 1-based), endLine (int, optional), includeLineNumbers (bool, optional), startChar (int, optional, 0-based), charLength (int, optional), targetLine (int, optional), contextLines (int, optional), anchorText (string, optional), beforeChars (int, optional), afterChars (int, optional), occurrence (int, optional).",
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
                    Description = "Apply patch blocks. Parameters: patch (string), path (string, optional for single-file patches). Supports SEARCH/REPLACE blocks, *** Update File sections, fuller unified diff headers such as diff --git / --- / +++ / @@, copy/rename headers, and mode-change metadata. Duplicate targets, directory-level conflicts, and unsafe existing destinations are rejected.",
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
                    Description = "Run unit tests for the current solution or affected test projects. Parameters: path (string, optional), filter (string, optional). When no filter is provided the tool can infer test class, namespace, and common method-based test naming filters from changed source files.",
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
                var startChar = GetInt(args, "startChar", -1);
                var charLength = GetInt(args, "charLength", 0);
                var targetLine = GetInt(args, "targetLine", 0);
                var contextLines = GetInt(args, "contextLines", 0);
                var anchorText = GetString(args, "anchorText");
                var beforeChars = GetInt(args, "beforeChars", 200);
                var afterChars = GetInt(args, "afterChars", 200);
                var occurrence = GetInt(args, "occurrence", 1);

                var text = File.ReadAllText(path);
                if (!string.IsNullOrWhiteSpace(anchorText))
                {
                    var useRegex = GetBool(args, "useRegex", false);
                    var ignoreCase = GetBool(args, "ignoreCase", false);
                    var window = IdeToolLogic.ExtractAnchorWindow(text, new IdeReadAnchorRequest(anchorText, beforeChars, afterChars, occurrence, useRegex, ignoreCase));
                    return Task.FromResult(McpToolCallResult.Success(window));
                }

                if (startChar >= 0)
                {
                    var normalizedStartChar = Math.Min(startChar, text.Length);
                    var normalizedLength = charLength <= 0 ? text.Length - normalizedStartChar : Math.Min(charLength, text.Length - normalizedStartChar);
                    return Task.FromResult(McpToolCallResult.Success(text.Substring(normalizedStartChar, normalizedLength)));
                }

                if (targetLine > 0 && contextLines >= 0)
                {
                    startLine = Math.Max(1, targetLine - contextLines);
                    endLine = targetLine + contextLines;
                }

                if (startLine <= 1 && endLine <= 0)
                    return Task.FromResult(McpToolCallResult.Success(text));

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

                var solutionDirectory = await GetSolutionDirectoryAsync().ConfigureAwait(true);
                var operations = IdeToolLogic.ParsePatchOperations(path, patch);
                var pendingWrites = IdeToolLogic.PreparePatchWrites(solutionDirectory, operations);

                foreach (var write in pendingWrites)
                {
                    if (write.Kind == IdePatchOperationKind.Delete)
                    {
                        var deletedViaIde = await TryDeleteFileViaIdeAsync(write.Path).ConfigureAwait(false);
                        if (!deletedViaIde && File.Exists(write.Path))
                            File.Delete(write.Path);
                        continue;
                    }

                    var directory = Path.GetDirectoryName(write.Path);
                    if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
                        Directory.CreateDirectory(directory);

                    if (write.Kind == IdePatchOperationKind.Rename && write.PreferMove && !string.IsNullOrWhiteSpace(write.SourcePath))
                    {
                        var renamedViaIde = await TryRenameFileViaIdeAsync(write.SourcePath, write.Path).ConfigureAwait(false);
                        if (renamedViaIde)
                        {
                            IdeToolLogic.ApplyPatchWrites(new[]
                            {
                                new IdePatchWrite(write.Path, write.Content, write.Kind, write.SourcePath, write.OriginalMode, write.UpdatedMode, shouldWriteContent: false)
                            });
                            continue;
                        }
                    }

                    var activeDocumentPath = await TryGetActiveDocumentPathAsync().ConfigureAwait(false);
                    var syncAction = IdeToolLogic.DetermineOpenDocumentSyncAction(activeDocumentPath, write);
                    var synchronizedViaDocument = false;
                    switch (syncAction)
                    {
                        case IdeOpenDocumentSyncAction.ReplaceTarget:
                            synchronizedViaDocument = await TryReplaceOpenDocumentAsync(write.Path, write.Content).ConfigureAwait(false);
                            break;
                        case IdeOpenDocumentSyncAction.CopySourceToTarget:
                            synchronizedViaDocument = !string.IsNullOrWhiteSpace(write.SourcePath)
                                && await TryCopyFromOpenDocumentAsync(write.SourcePath, write.Path).ConfigureAwait(false);
                            break;
                        case IdeOpenDocumentSyncAction.RenameSourceToTarget:
                            synchronizedViaDocument = !string.IsNullOrWhiteSpace(write.SourcePath)
                                && await TrySaveOpenDocumentAsAsync(write.SourcePath, write.Path).ConfigureAwait(false);
                            break;
                    }

                    if (synchronizedViaDocument)
                    {
                        IdeToolLogic.ApplyPatchWrites(new[]
                        {
                            new IdePatchWrite(write.Path, write.Content, write.Kind, write.SourcePath, write.OriginalMode, write.UpdatedMode, shouldWriteContent: false)
                        });
                    }
                    else
                    {
                        IdeToolLogic.ApplyPatchWrites(new[] { write });
                    }

                    if (write.Kind == IdePatchOperationKind.Copy)
                        await TryAddFileToProjectAsync(write.Path, write.SourcePath).ConfigureAwait(false);
                    else if (write.Kind == IdePatchOperationKind.Rename && !string.IsNullOrWhiteSpace(write.SourcePath))
                        await TrySyncRenameWithProjectAsync(write.SourcePath, write.Path).ConfigureAwait(false);
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
                var filter = GetString(args, "filter");
                var testRuns = string.IsNullOrWhiteSpace(targetPath)
                    ? await DetectAffectedTestRunsAsync(solutionDirectory, solutionPath).ConfigureAwait(false)
                    : new[] { new TestRunTarget(targetPath, filter) };

                var output = new StringBuilder();
                var failedTargets = new List<string>();
                foreach (var testRun in testRuns)
                {
                    var argumentsText = new StringBuilder();
                    argumentsText.Append("test ");
                    argumentsText.Append(QuoteArgument(testRun.ProjectPath));
                    argumentsText.Append(" --no-restore");
                    var effectiveFilter = string.IsNullOrWhiteSpace(filter) ? testRun.Filter : filter;
                    if (!string.IsNullOrWhiteSpace(effectiveFilter))
                    {
                        argumentsText.Append(" --filter ");
                        argumentsText.Append(QuoteArgument(effectiveFilter));
                    }

                    var processResult = await RunProcessAsync("dotnet", argumentsText.ToString(), solutionDirectory).ConfigureAwait(false);
                    output.AppendLine($"[{Path.GetFileName(testRun.ProjectPath)}]");
                    if (!string.IsNullOrWhiteSpace(effectiveFilter))
                        output.AppendLine($"Filter: {effectiveFilter}");
                    output.AppendLine(TrimToolOutput(processResult.Output));
                    output.AppendLine();
                    if (processResult.ExitCode != 0)
                        failedTargets.Add(testRun.ProjectPath);
                }

                var summary = output.ToString().TrimEnd();
                return failedTargets.Count == 0
                    ? McpToolCallResult.Success("Tests passed.\n" + summary)
                    : McpToolCallResult.Failure("Tests failed.\n" + summary);
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

        private async Task<bool> TryDeleteFileViaIdeAsync(string path)
        {
            var removedFromProject = await TryRemoveProjectItemAsync(path).ConfigureAwait(false);
            if (File.Exists(path))
                File.Delete(path);

            return removedFromProject || !File.Exists(path);
        }

        private async Task<bool> TryRenameFileViaIdeAsync(string sourcePath, string targetPath)
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var dte = await _package.GetServiceAsync(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                var projects = dte?.Solution?.Projects;
                var projectItem = FindProjectItemByPath(projects, sourcePath);
                if (projectItem == null)
                    return false;

                if (string.Equals(Path.GetDirectoryName(sourcePath), Path.GetDirectoryName(targetPath), StringComparison.OrdinalIgnoreCase))
                {
                    projectItem.Name = Path.GetFileName(targetPath);
                    return File.Exists(targetPath);
                }

                var targetItems = FindTargetProjectItems(projects, targetPath, sourcePath);
                if (targetItems == null)
                    return false;

                projectItem.Remove();
                File.Move(sourcePath, targetPath);
                if (FindProjectItemByPath(projects, targetPath) == null)
                    targetItems.AddFromFile(targetPath);

                return File.Exists(targetPath);
            }
            catch
            {
                return false;
            }
        }

        private async Task TrySyncRenameWithProjectAsync(string sourcePath, string targetPath)
        {
            await TryRemoveProjectItemAsync(sourcePath).ConfigureAwait(false);
            await TryAddFileToProjectAsync(targetPath, sourcePath).ConfigureAwait(false);
        }

        private async Task<bool> TryRemoveProjectItemAsync(string path)
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var dte = await _package.GetServiceAsync(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                var projectItem = FindProjectItemByPath(dte?.Solution?.Projects, path);
                if (projectItem == null)
                    return false;

                projectItem.Remove();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private async Task<bool> TryAddFileToProjectAsync(string path, string? relatedPath)
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var dte = await _package.GetServiceAsync(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                var projects = dte?.Solution?.Projects;
                if (FindProjectItemByPath(projects, path) != null)
                {
                    return true;
                }

                var targetItems = FindTargetProjectItems(projects, path, relatedPath);
                if (targetItems != null)
                {
                    targetItems.AddFromFile(path);
                    return true;
                }

                dte?.ItemOperations?.AddExistingItem(path);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static EnvDTE.ProjectItem? FindProjectItemByPath(EnvDTE.Projects? projects, string? path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (projects == null || string.IsNullOrWhiteSpace(path))
                return null;

            foreach (var project in EnumerateProjects(projects))
            {
                var item = FindProjectItemByPath(project.ProjectItems, path);
                if (item != null)
                    return item;
            }

            return null;
        }

        private static EnvDTE.ProjectItems? FindTargetProjectItems(EnvDTE.Projects? projects, string targetPath, string? relatedPath)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (projects == null)
                return null;

            var containerPath = ResolveTargetProjectContainerPath(projects, targetPath, relatedPath);
            if (string.IsNullOrWhiteSpace(containerPath))
                return null;

            var relatedCollection = TryGetProjectItemsFromRelatedItem(projects, containerPath, relatedPath);
            if (relatedCollection != null)
                return relatedCollection;

            var folderCollection = TryGetProjectItemsFromFolderItem(projects, containerPath);
            if (folderCollection != null)
                return folderCollection;

            return TryGetProjectItemsFromContainingProject(projects, containerPath, targetPath, relatedPath);
        }

        private static EnvDTE.Project? FindContainingProject(EnvDTE.Projects? projects, string? path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (projects == null || string.IsNullOrWhiteSpace(path))
                return null;

            foreach (var project in EnumerateProjects(projects))
            {
                try
                {
                    var projectDirectory = Path.GetDirectoryName(project.FullName);
                    if (!string.IsNullOrWhiteSpace(projectDirectory)
                        && IsPathUnderRoot(path, projectDirectory))
                    {
                        return project;
                    }
                }
                catch
                {
                }
            }

            return null;
        }

        private static EnvDTE.ProjectItem? FindProjectItemByPath(EnvDTE.ProjectItems? items, string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (items == null)
                return null;

            foreach (EnvDTE.ProjectItem item in items)
            {
                for (short i = 1; i <= item.FileCount; i++)
                {
                    try
                    {
                        if (AreSamePath(item.FileNames[i], path))
                            return item;
                    }
                    catch
                    {
                    }
                }

                if (item.SubProject != null)
                {
                    var subProjectMatch = FindProjectItemByPath(item.SubProject.ProjectItems, path);
                    if (subProjectMatch != null)
                        return subProjectMatch;
                }

                var child = FindProjectItemByPath(item.ProjectItems, path);
                if (child != null)
                    return child;
            }

            return null;
        }

        private async Task<string?> TryGetActiveDocumentPathAsync()
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var dte = await _package.GetServiceAsync(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                return dte?.ActiveDocument?.FullName;
            }
            catch
            {
                return null;
            }
        }

        private async Task<bool> TryCopyFromOpenDocumentAsync(string sourcePath, string targetPath)
        {
            var content = await TryGetOpenDocumentTextAsync(sourcePath).ConfigureAwait(false);
            if (content == null)
                return false;

            var directory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(targetPath, content);
            return true;
        }

        private async Task<bool> TrySaveOpenDocumentAsAsync(string sourcePath, string targetPath)
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var dte = await _package.GetServiceAsync(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                if (!AreSamePath(dte?.ActiveDocument?.FullName, sourcePath))
                    return false;

                var directory = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                dte.ActiveDocument.Save(targetPath);
                return File.Exists(targetPath);
            }
            catch
            {
                return false;
            }
        }

        private async Task<string?> TryGetOpenDocumentTextAsync(string path)
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var dte = await _package.GetServiceAsync(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                if (!AreSamePath(dte?.ActiveDocument?.FullName, path))
                    return null;

                var textManager = await _package.GetServiceAsync(typeof(SVsTextManager)) as IVsTextManager2;
                if (textManager == null)
                    return null;

                textManager.GetActiveView2(1, null, (uint)_VIEWFRAMETYPE.vftCodeWindow, out var activeView);
                if (activeView == null)
                    return null;

                activeView.GetBuffer(out var buffer);
                if (buffer == null)
                    return null;

                buffer.GetLastLineIndex(out var lastLine, out var lastCol);
                buffer.GetLineText(0, 0, lastLine, lastCol, out var fullText);
                return fullText;
            }
            catch
            {
                return null;
            }
        }

        private static string? ResolveTargetProjectContainerPath(EnvDTE.Projects? projects, string targetPath, string? relatedPath)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (projects == null)
                return null;

            var knownItemPaths = new List<string>();
            var projectRootPaths = new List<string>();
            CollectProjectMetadata(projects, knownItemPaths, projectRootPaths);
            return IdeToolLogic.ResolveProjectAttachmentContainerPath(targetPath, relatedPath, knownItemPaths, projectRootPaths);
        }

        private static EnvDTE.ProjectItems? TryGetProjectItemsFromRelatedItem(EnvDTE.Projects? projects, string containerPath, string? relatedPath)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var relatedItem = FindProjectItemByPath(projects, relatedPath);
            return relatedItem != null && AreSamePath(containerPath, relatedPath) && relatedItem.Collection != null
                ? relatedItem.Collection
                : null;
        }

        private static EnvDTE.ProjectItems? TryGetProjectItemsFromFolderItem(EnvDTE.Projects? projects, string containerPath)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var folderItem = FindProjectItemByPath(projects, containerPath);
            return folderItem?.ProjectItems;
        }

        private static EnvDTE.ProjectItems? TryGetProjectItemsFromContainingProject(EnvDTE.Projects? projects, string containerPath, string targetPath, string? relatedPath)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var project = FindContainingProject(projects, containerPath) ?? FindContainingProject(projects, targetPath) ?? FindContainingProject(projects, relatedPath);
            return project?.ProjectItems;
        }

        private static IEnumerable<EnvDTE.Project> EnumerateProjects(EnvDTE.Projects? projects)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (projects == null)
                yield break;

            foreach (EnvDTE.Project project in projects)
            {
                foreach (var nestedProject in EnumerateProject(project))
                    yield return nestedProject;
            }
        }

        private static IEnumerable<EnvDTE.Project> EnumerateProject(EnvDTE.Project? project)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (project == null)
                yield break;

            yield return project;

            if (project.ProjectItems == null)
                yield break;

            foreach (EnvDTE.ProjectItem item in project.ProjectItems)
            {
                if (item.SubProject == null)
                    continue;

                foreach (var nestedProject in EnumerateProject(item.SubProject))
                    yield return nestedProject;
            }
        }

        private static void CollectProjectMetadata(EnvDTE.Projects? projects, List<string> knownItemPaths, List<string> projectRootPaths)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            foreach (var project in EnumerateProjects(projects))
            {
                try
                {
                    var projectDirectory = Path.GetDirectoryName(project.FullName);
                    if (!string.IsNullOrWhiteSpace(projectDirectory))
                        projectRootPaths.Add(projectDirectory);
                }
                catch
                {
                }

                CollectItemPaths(project.ProjectItems, knownItemPaths);
            }
        }

        private static void CollectItemPaths(EnvDTE.ProjectItems? items, List<string> knownItemPaths)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (items == null)
                return;

            foreach (EnvDTE.ProjectItem item in items)
            {
                for (short i = 1; i <= item.FileCount; i++)
                {
                    try
                    {
                        knownItemPaths.Add(item.FileNames[i]);
                    }
                    catch
                    {
                    }
                }

                if (item.SubProject != null)
                    CollectItemPaths(item.SubProject.ProjectItems, knownItemPaths);

                CollectItemPaths(item.ProjectItems, knownItemPaths);
            }
        }

        private static bool IsPathUnderRoot(string path, string rootPath)
        {
            var fullPath = Path.GetFullPath(path);
            var fullRootPath = Path.GetFullPath(rootPath);
            return AreSamePath(fullPath, fullRootPath)
                || fullPath.StartsWith(fullRootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
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
            if ((patch ?? string.Empty).IndexOf("<<<<<<< SEARCH", StringComparison.Ordinal) >= 0)
                return ParseSearchReplaceBlocks(patch);

            if ((patch ?? string.Empty).IndexOf("@@", StringComparison.Ordinal) >= 0)
                return ParseUnifiedDiffBlocks(patch);

            throw new InvalidOperationException("No supported patch blocks were found.");
        }

        private static IReadOnlyList<SearchReplacePatchBlock> ParseSearchReplaceBlocks(string patch)
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

        private static IReadOnlyList<SearchReplacePatchBlock> ParseUnifiedDiffBlocks(string patch)
        {
            var blocks = new List<SearchReplacePatchBlock>();
            var normalized = (patch ?? string.Empty).Replace("\r\n", "\n");
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

                blocks.Add(new SearchReplacePatchBlock(string.Join("\n", searchLines), string.Join("\n", replaceLines)));
                index--;
            }

            if (blocks.Count == 0)
                throw new InvalidOperationException("No valid unified diff hunks were found.");

            return blocks;
        }

        private static IReadOnlyList<PatchFileOperation> ParsePatchOperations(string? explicitPath, string patch)
        {
            const string beginPatchMarker = "*** Begin Patch";
            const string updateFileMarker = "*** Update File:";
            const string endPatchMarker = "*** End Patch";

            var normalized = (patch ?? string.Empty).Replace("\r\n", "\n");
            if (normalized.IndexOf("--- ", StringComparison.Ordinal) >= 0
                && normalized.IndexOf("+++ ", StringComparison.Ordinal) >= 0
                && normalized.IndexOf("@@", StringComparison.Ordinal) >= 0)
            {
                return ParseUnifiedDiffFileOperations(normalized);
            }

            if (normalized.IndexOf(updateFileMarker, StringComparison.Ordinal) < 0)
            {
                if (string.IsNullOrWhiteSpace(explicitPath))
                    throw new InvalidOperationException("Parameter 'path' is required for single-file patches.");

                return new[] { new PatchFileOperation(explicitPath, ParsePatchBlocks(normalized), PatchOperationKind.Modify) };
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
                        operations.Add(new PatchFileOperation(currentPath, ParsePatchBlocks(currentPatch.ToString()), PatchOperationKind.Modify));

                    currentPath = line.Substring(updateFileMarker.Length).Trim();
                    currentPatch.Clear();
                    continue;
                }

                currentPatch.AppendLine(line);
            }

            if (!string.IsNullOrWhiteSpace(currentPath))
                operations.Add(new PatchFileOperation(currentPath, ParsePatchBlocks(currentPatch.ToString()), PatchOperationKind.Modify));

            if (operations.Count == 0)
                throw new InvalidOperationException("No valid file operations were found in the patch.");

            return operations;
        }

        private static IReadOnlyList<PatchFileOperation> ParseUnifiedDiffFileOperations(string patch)
        {
            var operations = new List<PatchFileOperation>();
            var normalized = (patch ?? string.Empty).Replace("\r\n", "\n");
            var lines = normalized.Split('\n');

            for (var index = 0; index < lines.Length; index++)
            {
                if (lines[index].StartsWith("diff --git ", StringComparison.Ordinal))
                    continue;

                if (!lines[index].StartsWith("--- ", StringComparison.Ordinal))
                    continue;

                var originalPath = NormalizeDiffPath(lines[index].Substring(4));
                index++;
                while (index < lines.Length && !lines[index].StartsWith("+++ ", StringComparison.Ordinal))
                    index++;

                if (index >= lines.Length)
                    throw new InvalidOperationException("Unified diff is missing a +++ file header.");

                var updatedPath = NormalizeDiffPath(lines[index].Substring(4));
                var operationPath = string.IsNullOrWhiteSpace(updatedPath) ? originalPath : updatedPath;
                if (string.IsNullOrWhiteSpace(operationPath))
                    throw new InvalidOperationException("Unified diff file headers did not contain a usable path.");

                var kind = string.IsNullOrWhiteSpace(originalPath)
                    ? PatchOperationKind.Create
                    : string.IsNullOrWhiteSpace(updatedPath)
                        ? PatchOperationKind.Delete
                        : PatchOperationKind.Modify;

                index++;
                var hunkBuilder = new StringBuilder();
                while (index < lines.Length
                    && !lines[index].StartsWith("diff --git ", StringComparison.Ordinal)
                    && !lines[index].StartsWith("--- ", StringComparison.Ordinal))
                {
                    hunkBuilder.AppendLine(lines[index]);
                    index++;
                }

                operations.Add(new PatchFileOperation(operationPath, ParseUnifiedDiffBlocks(hunkBuilder.ToString()), kind));
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

        private async Task<IReadOnlyList<TestRunTarget>> DetectAffectedTestRunsAsync(string solutionDirectory, string solutionPath)
        {
            var testProjects = Directory.EnumerateFiles(solutionDirectory, "*.Tests.csproj", SearchOption.AllDirectories).ToList();
            if (testProjects.Count <= 1)
            {
                if (testProjects.Count == 1)
                    return new[] { new TestRunTarget(testProjects[0], await InferTestFilterAsync(solutionDirectory, testProjects[0]).ConfigureAwait(false)) };

                return new[] { new TestRunTarget(solutionPath, string.Empty) };
            }

            var tracker = new GitWorkspaceChangeTracker(solutionDirectory);
            var changes = await tracker.LoadChangesAsync(Array.Empty<string>(), CancellationToken.None).ConfigureAwait(false);
            var changedProjectFiles = changes
                .Select(change => new ChangedProjectFile(change.RelativePath, FindContainingProject(solutionDirectory, change.RelativePath)))
                .Where(item => !string.IsNullOrWhiteSpace(item.ProjectPath))
                .ToList();

            if (changedProjectFiles.Count == 0)
                return testProjects.Select(project => new TestRunTarget(project, string.Empty)).ToList();

            var affected = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var changedProjectFile in changedProjectFiles)
            {
                foreach (var testProject in testProjects)
                {
                    if (!string.Equals(testProject, changedProjectFile.ProjectPath, StringComparison.OrdinalIgnoreCase)
                        && !DoesTestProjectReferenceProject(testProject, changedProjectFile.ProjectPath!)
                        && !TestProjectNameMatches(testProject, changedProjectFile.ProjectPath!))
                    {
                        continue;
                    }

                    if (!affected.TryGetValue(testProject, out var tokens))
                    {
                        tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        affected[testProject] = tokens;
                    }

                    foreach (var token in await InferTestFilterTokensAsync(solutionDirectory, changedProjectFile.RelativePath).ConfigureAwait(false))
                        tokens.Add(token);
                }
            }

            if (affected.Count == 0)
                return testProjects.Select(project => new TestRunTarget(project, string.Empty)).ToList();

            return affected
                .Select(item => new TestRunTarget(item.Key, BuildTestFilter(item.Value)))
                .ToList();
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

        private static int FindNthOccurrence(string input, string value, int occurrence)
        {
            if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(value) || occurrence <= 0)
                return -1;

            var index = -1;
            for (var current = 0; current < occurrence; current++)
            {
                index = input.IndexOf(value, index + 1, StringComparison.Ordinal);
                if (index < 0)
                    return -1;
            }

            return index;
        }

        private static int FindAnchorIndex(string input, string value, int occurrence, bool useRegex, bool ignoreCase, out int matchLength)
        {
            if (useRegex)
            {
                var options = ignoreCase ? System.Text.RegularExpressions.RegexOptions.IgnoreCase : System.Text.RegularExpressions.RegexOptions.None;
                var matches = System.Text.RegularExpressions.Regex.Matches(input ?? string.Empty, value ?? string.Empty, options);
                if (matches.Count < occurrence || occurrence <= 0)
                {
                    matchLength = 0;
                    return -1;
                }

                matchLength = matches[occurrence - 1].Length;
                return matches[occurrence - 1].Index;
            }

            matchLength = value?.Length ?? 0;
            return FindNthOccurrence(ignoreCase ? (input ?? string.Empty).ToLowerInvariant() : input, ignoreCase ? (value ?? string.Empty).ToLowerInvariant() : value, occurrence);
        }

        private static string? FindContainingProject(string solutionDirectory, string relativePath)
        {
            var currentDirectory = Path.GetDirectoryName(Path.Combine(solutionDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            while (!string.IsNullOrWhiteSpace(currentDirectory)
                && currentDirectory.StartsWith(solutionDirectory, StringComparison.OrdinalIgnoreCase))
            {
                var projectFile = Directory.EnumerateFiles(currentDirectory, "*.csproj", SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(projectFile))
                    return projectFile;

                currentDirectory = Path.GetDirectoryName(currentDirectory);
            }

            return null;
        }

        private static bool DoesTestProjectReferenceProject(string testProjectPath, string changedProjectPath)
        {
            var document = XDocument.Load(testProjectPath);
            foreach (var reference in document.Descendants().Where(element => string.Equals(element.Name.LocalName, "ProjectReference", StringComparison.OrdinalIgnoreCase)))
            {
                var include = reference.Attribute("Include")?.Value;
                if (string.IsNullOrWhiteSpace(include))
                    continue;

                var resolvedPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testProjectPath) ?? string.Empty, include));
                if (string.Equals(resolvedPath, Path.GetFullPath(changedProjectPath), StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static bool TestProjectNameMatches(string testProjectPath, string changedProjectPath)
        {
            var testProjectName = Path.GetFileNameWithoutExtension(testProjectPath);
            var changedProjectName = Path.GetFileNameWithoutExtension(changedProjectPath);
            var normalizedTestName = testProjectName.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase)
                ? testProjectName.Substring(0, testProjectName.Length - ".Tests".Length)
                : testProjectName;

            return string.Equals(normalizedTestName, changedProjectName, StringComparison.OrdinalIgnoreCase);
        }

        private async Task<IEnumerable<string>> InferTestFilterTokensAsync(string solutionDirectory, string relativePath)
        {
            var fullPath = Path.Combine(solutionDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath) || !string.Equals(Path.GetExtension(fullPath), ".cs", StringComparison.OrdinalIgnoreCase))
                return Array.Empty<string>();

            return await Task.FromResult<IEnumerable<string>>(IdeToolLogic.InferTestFilterTokens(File.ReadAllText(fullPath), fullPath)).ConfigureAwait(false);
        }

        private async Task<string> InferTestFilterAsync(string solutionDirectory, string projectPath)
        {
            var tracker = new GitWorkspaceChangeTracker(solutionDirectory);
            var changes = await tracker.LoadChangesAsync(Array.Empty<string>(), CancellationToken.None).ConfigureAwait(false);
            var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var change in changes)
            {
                var containingProject = FindContainingProject(solutionDirectory, change.RelativePath);
                if (string.Equals(containingProject, projectPath, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var token in await InferTestFilterTokensAsync(solutionDirectory, change.RelativePath).ConfigureAwait(false))
                        tokens.Add(token);
                }
            }

            return BuildTestFilter(tokens);
        }

        private static string BuildTestFilter(IEnumerable<string> tokens)
        {
            var parts = tokens
                .Where(token => !string.IsNullOrWhiteSpace(token))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(token => "FullyQualifiedName~" + token)
                .ToArray();

            return parts.Length == 0 ? string.Empty : string.Join("|", parts);
        }

        private async Task<string?> GetSolutionDirectoryAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var dte = await _package.GetServiceAsync(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
            var solutionPath = dte?.Solution?.FullName;
            return string.IsNullOrWhiteSpace(solutionPath) ? null : Path.GetDirectoryName(solutionPath);
        }

        private static string ResolvePatchPath(string path, string? solutionDirectory)
        {
            if (Path.IsPathRooted(path))
                return path;
            if (string.IsNullOrWhiteSpace(solutionDirectory))
                return Path.GetFullPath(path);

            return Path.GetFullPath(Path.Combine(solutionDirectory, path.Replace('/', Path.DirectorySeparatorChar)));
        }

        private static string BuildCreatedFileContent(IReadOnlyList<SearchReplacePatchBlock> blocks)
        {
            return string.Join(string.Empty, blocks.Select(block => block.ReplaceText));
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
            public PatchFileOperation(string path, IReadOnlyList<SearchReplacePatchBlock> blocks, PatchOperationKind kind)
            {
                Path = path;
                Blocks = blocks;
                Kind = kind;
            }

            public string Path { get; }

            public IReadOnlyList<SearchReplacePatchBlock> Blocks { get; }

            public PatchOperationKind Kind { get; }
        }

        private sealed class PatchFileWrite
        {
            public PatchFileWrite(string path, string content, IdePatchOperationKind kind, string? sourcePath = null)
            {
                Path = path;
                Content = content;
                Kind = kind;
                SourcePath = sourcePath;
            }

            public string Path { get; }

            public string Content { get; }

            public IdePatchOperationKind Kind { get; }

            public string? SourcePath { get; }
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

        private sealed class TestRunTarget
        {
            public TestRunTarget(string projectPath, string filter)
            {
                ProjectPath = projectPath;
                Filter = filter;
            }

            public string ProjectPath { get; }

            public string Filter { get; }
        }

        private sealed class ChangedProjectFile
        {
            public ChangedProjectFile(string relativePath, string? projectPath)
            {
                RelativePath = relativePath;
                ProjectPath = projectPath;
            }

            public string RelativePath { get; }

            public string? ProjectPath { get; }
        }

        private enum PatchOperationKind
        {
            Modify,
            Create,
            Delete,
        }

    }
}
