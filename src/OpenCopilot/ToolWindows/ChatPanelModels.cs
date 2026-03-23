using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace OpenCopilot.ToolWindows
{
    internal sealed class ChatAttachment : INotifyPropertyChanged
    {
        private string _displayName;

        public ChatAttachment(string displayName, string promptContent, string kind, string? sourcePath = null)
        {
            _displayName = displayName ?? throw new ArgumentNullException(nameof(displayName));
            PromptContent = promptContent ?? string.Empty;
            Kind = kind ?? "context";
            SourcePath = sourcePath;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public string DisplayName
        {
            get => _displayName;
            set
            {
                if (string.Equals(_displayName, value, StringComparison.Ordinal))
                    return;

                _displayName = value;
                OnPropertyChanged();
            }
        }

        public string PromptContent { get; }

        public string Kind { get; }

        public string? SourcePath { get; }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    internal sealed class ChatPlanItem
    {
        public ChatPlanItem(string title, string status)
        {
            Title = title ?? throw new ArgumentNullException(nameof(title));
            Status = string.IsNullOrWhiteSpace(status) ? "pending" : status;
        }

        public string Title { get; }

        public string Status { get; set; }

        public bool IsCompleted => string.Equals(Status, "completed", StringComparison.OrdinalIgnoreCase);

        public bool IsInProgress => string.Equals(Status, "in-progress", StringComparison.OrdinalIgnoreCase);
    }

    internal enum ChatInteractionMode
    {
        Ask,
        Agent,
    }

    internal sealed class ChatInteractionModeOption
    {
        public ChatInteractionModeOption(ChatInteractionMode mode, string displayName)
        {
            Mode = mode;
            DisplayName = displayName ?? throw new ArgumentNullException(nameof(displayName));
        }

        public ChatInteractionMode Mode { get; }

        public string DisplayName { get; }
    }
}
