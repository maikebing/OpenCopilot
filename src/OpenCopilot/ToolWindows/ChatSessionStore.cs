using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OpenCopilot.ToolWindows
{
    internal sealed class ChatSessionStore
    {
        private readonly string _sessionsDirectory;

        public ChatSessionStore(string solutionDirectory)
        {
            if (string.IsNullOrWhiteSpace(solutionDirectory))
                throw new ArgumentException("Solution directory is required.", nameof(solutionDirectory));

            _sessionsDirectory = Path.Combine(solutionDirectory, ".opencopilot");
        }

        public async Task<IReadOnlyList<ChatSessionInfo>> LoadSessionsAsync(CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!Directory.Exists(_sessionsDirectory))
                    return (IReadOnlyList<ChatSessionInfo>)Array.Empty<ChatSessionInfo>();

                var sessions = new List<ChatSessionInfo>();
                foreach (var filePath in Directory.EnumerateFiles(_sessionsDirectory, "*.md", SearchOption.TopDirectoryOnly))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var document = ReadDocument(filePath);
                    sessions.Add(new ChatSessionInfo(filePath, document.Title, document.TitleSource)
                    {
                        LastUpdatedUtc = File.GetLastWriteTimeUtc(filePath)
                    });
                }

                return (IReadOnlyList<ChatSessionInfo>)sessions
                    .OrderByDescending(session => session.LastUpdatedUtc)
                    .ToList();
            }, cancellationToken).ConfigureAwait(false);
        }

        public async Task<ChatSessionInfo> CreateSessionAsync(string title, CancellationToken cancellationToken)
        {
            var session = new ChatSessionInfo(CreateSessionFilePath(), title, ChatSessionTitleSource.Generated);
            await SaveSessionAsync(session, Array.Empty<ChatMessage>(), cancellationToken).ConfigureAwait(false);
            return session;
        }

        public async Task<IReadOnlyList<ChatMessage>> LoadMessagesAsync(ChatSessionInfo session, CancellationToken cancellationToken)
        {
            if (session == null)
                throw new ArgumentNullException(nameof(session));

            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return (IReadOnlyList<ChatMessage>)ReadDocument(session.FilePath).Messages;
            }, cancellationToken).ConfigureAwait(false);
        }

        public async Task SaveSessionAsync(ChatSessionInfo session, IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken)
        {
            if (session == null)
                throw new ArgumentNullException(nameof(session));
            if (messages == null)
                throw new ArgumentNullException(nameof(messages));

            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(_sessionsDirectory);

                var title = SanitizeTitle(session.Title);
                var builder = new StringBuilder();
                builder.AppendLine("# " + title);
                builder.AppendLine();
                builder.AppendLine($"<!-- OpenCopilotSession: title-source={session.TitleSource.ToString().ToLowerInvariant()} -->");
                builder.AppendLine($"<!-- UpdatedUtc: {DateTime.UtcNow:O} -->");
                builder.AppendLine();
                builder.AppendLine("## Conversation");
                builder.AppendLine();

                foreach (var message in messages)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var sender = string.IsNullOrWhiteSpace(message.Sender) ? "OpenCopilot" : message.Sender.Trim();
                    var content = message.Content ?? string.Empty;
                    var fence = CreateFence(content);

                    builder.AppendLine("### " + sender);
                    builder.AppendLine(fence + "text");
                    builder.AppendLine(content);
                    if (!content.EndsWith("\n", StringComparison.Ordinal))
                        builder.AppendLine();
                    builder.AppendLine(fence);
                    builder.AppendLine();
                }

                File.WriteAllText(session.FilePath, builder.ToString(), new UTF8Encoding(false));
                session.Title = title;
                session.LastUpdatedUtc = File.GetLastWriteTimeUtc(session.FilePath);
            }, cancellationToken).ConfigureAwait(false);
        }

        private string CreateSessionFilePath()
        {
            Directory.CreateDirectory(_sessionsDirectory);
            var fileName = $"session-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.md";
            return Path.Combine(_sessionsDirectory, fileName);
        }

        private static string SanitizeTitle(string title)
        {
            var sanitized = string.IsNullOrWhiteSpace(title)
                ? "New chat"
                : title.Replace("\r", " ").Replace("\n", " ").Trim();

            while (sanitized.StartsWith("#", StringComparison.Ordinal))
                sanitized = sanitized.Substring(1).TrimStart();

            return sanitized.Length > 80 ? sanitized.Substring(0, 80).TrimEnd() : sanitized;
        }

        private static string CreateFence(string content)
        {
            var longestRun = 0;
            var currentRun = 0;
            foreach (var ch in content ?? string.Empty)
            {
                if (ch == '`')
                {
                    currentRun++;
                    if (currentRun > longestRun)
                        longestRun = currentRun;
                }
                else
                {
                    currentRun = 0;
                }
            }

            return new string('`', Math.Max(3, longestRun + 1));
        }

        private static ChatSessionDocument ReadDocument(string filePath)
        {
            if (!File.Exists(filePath))
                return new ChatSessionDocument("New chat", ChatSessionTitleSource.Generated, new List<ChatMessage>());

            var text = File.ReadAllText(filePath);
            var normalized = text.Replace("\r\n", "\n");
            var lines = normalized.Split('\n');

            var title = "New chat";
            var titleSource = ChatSessionTitleSource.Generated;
            var messages = new List<ChatMessage>();

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.StartsWith("# ", StringComparison.Ordinal))
                {
                    title = line.Substring(2).Trim();
                    continue;
                }

                if (line.IndexOf("title-source=ai", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    titleSource = ChatSessionTitleSource.Ai;
                    continue;
                }

                if (line.IndexOf("title-source=fallback", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    titleSource = ChatSessionTitleSource.Fallback;
                    continue;
                }

                if (!line.StartsWith("### ", StringComparison.Ordinal))
                    continue;

                var sender = line.Substring(4).Trim();
                i++;
                while (i < lines.Length && string.IsNullOrWhiteSpace(lines[i]))
                    i++;

                if (i >= lines.Length)
                    break;

                var fence = TryGetFence(lines[i]);
                if (fence == null)
                    continue;

                i++;
                var contentLines = new List<string>();
                while (i < lines.Length && !string.Equals(lines[i], fence, StringComparison.Ordinal))
                {
                    contentLines.Add(lines[i]);
                    i++;
                }

                var content = string.Join("\n", contentLines).TrimEnd('\n');
                messages.Add(new ChatMessage(sender, content));
            }

            return new ChatSessionDocument(title, titleSource, messages);
        }

        private static string TryGetFence(string line)
        {
            if (string.IsNullOrEmpty(line))
                return null;

            var fenceChar = line[0];
            if (fenceChar != '`' && fenceChar != '~')
                return null;

            var count = 0;
            while (count < line.Length && line[count] == fenceChar)
                count++;

            return count >= 3 ? new string(fenceChar, count) : null;
        }

        private sealed class ChatSessionDocument
        {
            public ChatSessionDocument(string title, ChatSessionTitleSource titleSource, List<ChatMessage> messages)
            {
                Title = SanitizeTitle(title);
                TitleSource = titleSource;
                Messages = messages ?? new List<ChatMessage>();
            }

            public string Title { get; }
            public ChatSessionTitleSource TitleSource { get; }
            public List<ChatMessage> Messages { get; }
        }
    }

    internal enum ChatSessionTitleSource
    {
        Generated,
        Fallback,
        Ai,
    }

    internal sealed class ChatSessionInfo : INotifyPropertyChanged
    {
        private string _title;

        public ChatSessionInfo(string filePath, string title, ChatSessionTitleSource titleSource)
        {
            FilePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
            _title = title ?? "New chat";
            TitleSource = titleSource;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public string FilePath { get; }

        public string Title
        {
            get => _title;
            set
            {
                if (string.Equals(_title, value, StringComparison.Ordinal))
                    return;

                _title = value;
                OnPropertyChanged();
            }
        }

        public ChatSessionTitleSource TitleSource { get; set; }

        public DateTime LastUpdatedUtc { get; set; }

        public override string ToString() => Title;

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
