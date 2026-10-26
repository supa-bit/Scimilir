using System.Text.RegularExpressions;
using Scimilir.Storage;

namespace Scimilir.Providers;

/// <summary>One message as it is handed to a model.</summary>
public sealed record Turn(string Role, string Content);

/// <summary>A model the user can pick. Id looks like "local:file.gguf" or "openrouter:vendor/model".</summary>
public sealed record ModelInfo(string Id, string Name, string Group, string? Detail);

public enum PieceKind { Text, Thinking, Status }

/// <summary>A bit of a reply as it streams in: words, the model's "thinking", or a note for the user.</summary>
public sealed record Piece(PieceKind Kind, string Text);

/// <summary>A problem worth showing the user as it is, in plain words.</summary>
public sealed class UserFacingException(string message) : Exception(message);

/// <summary>Somewhere replies can come from: this PC, or a cloud service.</summary>
public interface IChatProvider
{
    /// <summary>The start of this provider's model ids, before the colon.</summary>
    string Prefix { get; }
    Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct);
    IAsyncEnumerable<Piece> StreamAsync(string model, IReadOnlyList<Turn> turns, Settings settings, CancellationToken ct);
}

public static partial class Conversation
{
    [GeneratedRegex(@"<think>[\s\S]*?(</think>|$)")]
    private static partial Regex ThinkBlock();

    /// <summary>Removes a reply's &lt;think&gt; section, which a model never needs to see again.</summary>
    public static string StripThinking(string text) => ThinkBlock().Replace(text, "").Trim();

    /// <summary>The messages to send a model: the system prompt (if any), then the chat.</summary>
    public static List<Turn> BuildTurns(Chat chat, string systemPrompt)
    {
        var turns = new List<Turn>();
        if (!string.IsNullOrWhiteSpace(systemPrompt)) turns.Add(new Turn("system", systemPrompt.Trim()));
        foreach (var m in chat.Messages)
        {
            var content = m.Role == "assistant" ? StripThinking(m.Content) : m.Content;
            if (content.Length > 0) turns.Add(new Turn(m.Role, content));
        }
        return turns;
    }
}

/// <summary>Finds the provider for a model id.</summary>
public sealed class ProviderHub(IEnumerable<IChatProvider> providers)
{
    readonly Dictionary<string, IChatProvider> _byPrefix = providers.ToDictionary(p => p.Prefix);

    public IEnumerable<IChatProvider> All => _byPrefix.Values;

    public (IChatProvider Provider, string Model)? Resolve(string? modelId)
    {
        var colon = modelId?.IndexOf(':') ?? -1;
        if (colon <= 0 || colon == modelId!.Length - 1) return null;
        return _byPrefix.TryGetValue(modelId[..colon], out var provider) ? (provider, modelId[(colon + 1)..]) : null;
    }
}
