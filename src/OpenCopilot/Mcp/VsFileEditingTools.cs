using System;
using System.Collections.Generic;
using System.IO;
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
                    Description = "Read the full text content of a file.",
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
                    Name = "vs_reencode_file",
                    Description = "Re-encode a file from one text encoding to another. Parameters: path (string), fromEncoding (string), toEncoding (string).",
                    ServerName = "BuiltIn",
                    IsBuiltIn = true
                },
                ReencodeFileAsync);
        }

        private static Task<McpToolCallResult> ReadFileAsync(Dictionary<string, object?> args)
        {
            try
            {
                var path = GetString(args, "path");
                if (string.IsNullOrWhiteSpace(path))
                    return Task.FromResult(McpToolCallResult.Failure("Parameter 'path' is required."));

                var text = File.ReadAllText(path);
                return Task.FromResult(McpToolCallResult.Success(text));
            }
            catch (Exception ex)
            {
                return Task.FromResult(McpToolCallResult.Failure(ex.Message));
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
    }
}
