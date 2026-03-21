using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenCopilot.ToolWindows
{
    internal sealed class GitWorkspaceChangeTracker
    {
        private readonly string _workingDirectory;

        public GitWorkspaceChangeTracker(string workingDirectory)
        {
            if (string.IsNullOrWhiteSpace(workingDirectory))
                throw new ArgumentException("Working directory is required.", nameof(workingDirectory));

            _workingDirectory = workingDirectory;
        }

        public async Task<IReadOnlyList<WorkspaceChangedFile>> LoadChangesAsync(IReadOnlyCollection<string> retainedPaths, CancellationToken cancellationToken)
        {
            var output = await RunGitAsync("status --short --untracked-files=all", cancellationToken).ConfigureAwait(false);
            var retained = retainedPaths ?? Array.Empty<string>();
            var items = new List<WorkspaceChangedFile>();

            foreach (var rawLine in output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (rawLine.Length < 4)
                    continue;

                var status = rawLine.Substring(0, 2).Trim();
                var relativePath = rawLine.Substring(3).Trim();
                if (relativePath.StartsWith("\"", StringComparison.Ordinal) && relativePath.EndsWith("\"", StringComparison.Ordinal))
                    relativePath = relativePath.Substring(1, relativePath.Length - 2);

                items.Add(new WorkspaceChangedFile(relativePath, status)
                {
                    IsRetained = retained.Contains(relativePath, StringComparer.OrdinalIgnoreCase)
                });
            }

            return items;
        }

        public async Task DiscardAsync(WorkspaceChangedFile file, CancellationToken cancellationToken)
        {
            if (file == null)
                throw new ArgumentNullException(nameof(file));

            var escapedPath = Quote(file.RelativePath);
            if (string.Equals(file.Status, "??", StringComparison.Ordinal))
            {
                var fullPath = Path.Combine(_workingDirectory, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (File.Exists(fullPath))
                        File.Delete(fullPath);
                }, cancellationToken).ConfigureAwait(false);
                return;
            }

            await RunGitAsync($"restore -- {escapedPath}", cancellationToken).ConfigureAwait(false);
        }

        public async Task<string?> GetHeadFileTextAsync(string relativePath, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                throw new ArgumentException("Relative path is required.", nameof(relativePath));

            try
            {
                return await RunGitAsync($"show HEAD:{QuoteRevisionPath(relativePath)}", cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        public async Task<IReadOnlyList<GitDiffHunk>> LoadHunksAsync(string relativePath, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                throw new ArgumentException("Relative path is required.", nameof(relativePath));

            var diff = await RunGitAsync($"diff --no-color --unified=3 -- {Quote(relativePath)}", cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(diff))
                return Array.Empty<GitDiffHunk>();

            var normalized = diff.Replace("\r\n", "\n");
            var lines = normalized.Split('\n');
            var headerLines = new List<string>();
            var hunks = new List<GitDiffHunk>();
            List<string>? currentHunkLines = null;
            string? currentHeader = null;

            foreach (var line in lines)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (line.StartsWith("@@ ", StringComparison.Ordinal))
                {
                    if (currentHunkLines != null && currentHeader != null)
                        hunks.Add(new GitDiffHunk(currentHeader, string.Join("\n", headerLines.Concat(currentHunkLines).Where(item => item != null))));

                    currentHeader = line;
                    currentHunkLines = new List<string> { line };
                    continue;
                }

                if (currentHunkLines != null)
                {
                    currentHunkLines.Add(line);
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(line))
                    headerLines.Add(line);
            }

            if (currentHunkLines != null && currentHeader != null)
                hunks.Add(new GitDiffHunk(currentHeader, string.Join("\n", headerLines.Concat(currentHunkLines).Where(item => item != null))));

            return hunks;
        }

        public async Task DiscardHunkAsync(string relativePath, GitDiffHunk hunk, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                throw new ArgumentException("Relative path is required.", nameof(relativePath));
            if (hunk == null)
                throw new ArgumentNullException(nameof(hunk));

            var patchFile = Path.Combine(Path.GetTempPath(), $"OpenCopilot-{Guid.NewGuid():N}.patch");
            await Task.Run(() => File.WriteAllText(patchFile, hunk.PatchText), cancellationToken).ConfigureAwait(false);
            try
            {
                await RunGitAsync($"apply -R --whitespace=nowarn {Quote(patchFile)}", cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (File.Exists(patchFile))
                    File.Delete(patchFile);
            }
        }

        private async Task<string> RunGitAsync(string arguments, CancellationToken cancellationToken)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = $"-C {Quote(_workingDirectory)} {arguments}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using (var process = new Process { StartInfo = startInfo })
            {
                process.Start();
                var standardOutput = process.StandardOutput.ReadToEndAsync();
                var standardError = process.StandardError.ReadToEndAsync();
                await Task.Run(() => process.WaitForExit(), cancellationToken).ConfigureAwait(false);

                var output = await standardOutput.ConfigureAwait(false);
                var error = await standardError.ConfigureAwait(false);
                if (process.ExitCode != 0)
                    throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "Git command failed." : error.Trim());

                return output;
            }
        }

        private static string Quote(string value)
            => "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";

        private static string QuoteRevisionPath(string value)
            => (value ?? string.Empty).Replace("\\", "/").Replace("\"", "");
    }

    internal sealed class WorkspaceChangedFile
    {
        public WorkspaceChangedFile(string relativePath, string status)
        {
            RelativePath = relativePath ?? throw new ArgumentNullException(nameof(relativePath));
            Status = string.IsNullOrWhiteSpace(status) ? "M" : status;
        }

        public string RelativePath { get; }

        public string Status { get; }

        public bool IsRetained { get; set; }
    }

    internal sealed class GitDiffHunk
    {
        public GitDiffHunk(string header, string patchText)
        {
            Header = header ?? throw new ArgumentNullException(nameof(header));
            PatchText = patchText ?? throw new ArgumentNullException(nameof(patchText));
        }

        public string Header { get; }

        public string PatchText { get; }

        public override string ToString() => Header;
    }
}
