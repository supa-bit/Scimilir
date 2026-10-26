using System.Text.Json;
using System.Text.RegularExpressions;

namespace Scimilir.Storage;

public sealed class ChatMessage
{
    /// <summary>"user" or "assistant".</summary>
    public string Role { get; set; } = "";
    public string Content { get; set; } = "";
    /// <summary>For replies: which model wrote it.</summary>
    public string? Model { get; set; }
    public DateTimeOffset Time { get; set; }
}

public sealed class Chat
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Model { get; set; }
    public DateTimeOffset Created { get; set; }
    public DateTimeOffset Updated { get; set; }
    public List<ChatMessage> Messages { get; set; } = [];
}

public sealed record ChatSummary(string Id, string Title, DateTimeOffset Updated);

/// <summary>
/// Saves each chat as its own file in data/chats, so the PC and the phone see
/// the same conversations.
/// </summary>
public sealed partial class ChatStore(AppPaths paths)
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    readonly Lock _lock = new();

    [GeneratedRegex("^[a-z0-9]{1,32}$")]
    private static partial Regex IdPattern();

    public static bool IsValidId(string id) => IdPattern().IsMatch(id);

    /// <summary>A chat's title: the start of its first message, on one line.</summary>
    public static string TitleFrom(string text)
    {
        var line = Regex.Replace(text.Trim(), @"\s+", " ");
        if (line.Length == 0) return "New chat";
        return line.Length <= 60 ? line : line[..57].TrimEnd() + "…";
    }

    public Chat Create(string title)
    {
        var now = DateTimeOffset.UtcNow;
        var chat = new Chat { Id = Guid.NewGuid().ToString("N")[..16], Title = title, Created = now, Updated = now };
        Save(chat);
        return chat;
    }

    public Chat? Get(string id)
    {
        if (!IsValidId(id)) return null;
        lock (_lock)
        {
            var file = FileFor(id);
            return File.Exists(file) ? JsonSerializer.Deserialize<Chat>(File.ReadAllText(file)) : null;
        }
    }

    public void Save(Chat chat)
    {
        if (!IsValidId(chat.Id)) throw new ArgumentException("Bad chat id", nameof(chat));
        lock (_lock)
        {
            var file = FileFor(chat.Id);
            File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(chat, Json));
            File.Move(file + ".tmp", file, overwrite: true);
        }
    }

    public bool Delete(string id)
    {
        if (!IsValidId(id)) return false;
        lock (_lock)
        {
            var file = FileFor(id);
            if (!File.Exists(file)) return false;
            File.Delete(file);
            return true;
        }
    }

    /// <summary>All chats, most recently used first.</summary>
    public List<ChatSummary> List()
    {
        var list = new List<ChatSummary>();
        lock (_lock)
        {
            foreach (var file in Directory.EnumerateFiles(paths.Chats, "*.json"))
            {
                try
                {
                    var chat = JsonSerializer.Deserialize<Chat>(File.ReadAllText(file));
                    if (chat is not null) list.Add(new ChatSummary(chat.Id, chat.Title, chat.Updated));
                }
                catch (JsonException)
                {
                    // A damaged file shouldn't hide every other chat; skip it.
                }
            }
        }
        return [.. list.OrderByDescending(c => c.Updated)];
    }

    string FileFor(string id) => Path.Combine(paths.Chats, id + ".json");
}
