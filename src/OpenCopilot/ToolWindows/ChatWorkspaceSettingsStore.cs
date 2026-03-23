using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json;

namespace OpenCopilot.ToolWindows
{
    internal sealed class ChatWorkspaceSettingsStore
    {
        private readonly string _settingsFilePath;

        public ChatWorkspaceSettingsStore(string solutionDirectory)
        {
            if (string.IsNullOrWhiteSpace(solutionDirectory))
                throw new ArgumentException("Solution directory is required.", nameof(solutionDirectory));

            _settingsFilePath = Path.Combine(solutionDirectory, ".opencopilot", "workspace-settings.json");
        }

        public async Task<ChatWorkspaceSettings> LoadAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!File.Exists(_settingsFilePath))
                        return new ChatWorkspaceSettings();

                    var json = File.ReadAllText(_settingsFilePath);
                    return JsonConvert.DeserializeObject<ChatWorkspaceSettings>(json) ?? new ChatWorkspaceSettings();
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                ActivityLog.LogWarning(nameof(ChatWorkspaceSettingsStore), $"Failed to load workspace chat settings: {ex.Message}");
                return new ChatWorkspaceSettings();
            }
            catch (UnauthorizedAccessException ex)
            {
                ActivityLog.LogWarning(nameof(ChatWorkspaceSettingsStore), $"Access denied while loading workspace chat settings: {ex.Message}");
                return new ChatWorkspaceSettings();
            }
            catch (JsonException ex)
            {
                ActivityLog.LogWarning(nameof(ChatWorkspaceSettingsStore), $"Failed to parse workspace chat settings: {ex.Message}");
                return new ChatWorkspaceSettings();
            }
        }

        public async Task SaveAsync(ChatWorkspaceSettings settings, CancellationToken cancellationToken)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));

            try
            {
                await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var directory = Path.GetDirectoryName(_settingsFilePath);
                    if (!string.IsNullOrWhiteSpace(directory))
                        Directory.CreateDirectory(directory);

                    var json = JsonConvert.SerializeObject(settings, Formatting.Indented);
                    File.WriteAllText(_settingsFilePath, json);
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                ActivityLog.LogWarning(nameof(ChatWorkspaceSettingsStore), $"Failed to save workspace chat settings: {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                ActivityLog.LogWarning(nameof(ChatWorkspaceSettingsStore), $"Access denied while saving workspace chat settings: {ex.Message}");
            }
        }
    }

    internal sealed class ChatWorkspaceSettings
    {
        public string? ProviderName { get; set; }

        public string? ModelName { get; set; }

        public ChatInteractionMode InteractionMode { get; set; } = ChatInteractionMode.Agent;
    }
}
