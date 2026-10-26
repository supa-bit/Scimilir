using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Scimilir.Storage;

namespace Scimilir.Providers;

/// <summary>
/// Cloud models through OpenRouter (openrouter.ai): one account and key gives
/// access to models from many companies.
/// </summary>
public sealed class OpenRouterProvider(HttpClient http, SettingsStore settings) : IChatProvider
{
    public const string BaseUrl = "https://openrouter.ai/api/v1/";
    static readonly TimeSpan CacheFor = TimeSpan.FromHours(1);

    IReadOnlyList<ModelInfo>? _cache;
    DateTimeOffset _cachedAt;

    public string Prefix => "openrouter";

    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct)
    {
        // Without a key the list would only be clutter, so show nothing.
        if (string.IsNullOrEmpty(settings.Current.OpenRouterKey)) return [];
        if (_cache is not null && DateTimeOffset.UtcNow - _cachedAt < CacheFor) return _cache;

        using var doc = JsonDocument.Parse(await http.GetStringAsync(BaseUrl + "models", ct));
        _cache = ParseModels(doc.RootElement);
        _cachedAt = DateTimeOffset.UtcNow;
        return _cache;
    }

    public static List<ModelInfo> ParseModels(JsonElement root)
    {
        var list = new List<ModelInfo>();
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return list;
        foreach (var m in data.EnumerateArray())
        {
            if (m.TryGetProperty("id", out var idProp) is false || idProp.GetString() is not { Length: > 0 } id) continue;
            // Skip models that only make pictures or sound.
            if (m.TryGetProperty("architecture", out var arch)
                && arch.TryGetProperty("output_modalities", out var outputs)
                && outputs.ValueKind == JsonValueKind.Array
                && !outputs.EnumerateArray().Any(o => o.GetString() == "text"))
                continue;
            var name = m.TryGetProperty("name", out var n) ? n.GetString() ?? id : id;
            list.Add(new ModelInfo($"openrouter:{id}", name, "OpenRouter", PriceText(m)));
        }
        return [.. list.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Price per million tokens (roughly 750,000 words), in and out.</summary>
    static string? PriceText(JsonElement model)
    {
        if (!model.TryGetProperty("pricing", out var pricing)) return null;
        static decimal? Per(JsonElement p, string key) =>
            p.TryGetProperty(key, out var v) && decimal.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                ? d * 1_000_000m : null;
        var input = Per(pricing, "prompt");
        var output = Per(pricing, "completion");
        if (input is null || output is null) return null;
        if (input == 0 && output == 0) return "Free";
        return string.Create(CultureInfo.InvariantCulture, $"${input:0.##} in · ${output:0.##} out per million tokens");
    }

    public async IAsyncEnumerable<Piece> StreamAsync(string model, IReadOnlyList<Turn> turns, Settings s,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (string.IsNullOrEmpty(s.OpenRouterKey))
            throw new UserFacingException("Add your OpenRouter key in Settings → Cloud first.");

        using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "chat/completions")
        {
            Content = JsonContent.Create(new
            {
                model,
                messages = turns.Select(t => new { role = t.Role, content = t.Content }),
                stream = true,
                temperature = s.Temperature,
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.OpenRouterKey);
        // OpenRouter shows these on its activity page, so you can see which app made a request.
        request.Headers.Add("HTTP-Referer", "https://github.com/supa-bit/scimilir");
        request.Headers.Add("X-Title", "Scimilir");

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new UserFacingException(ErrorText(response.StatusCode, await response.Content.ReadAsStringAsync(ct)));

        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            var (pieces, done) = ParseLine(line);
            foreach (var piece in pieces) yield return piece;
            if (done) yield break;
        }
    }

    /// <summary>
    /// Reads one line of OpenRouter's stream. Lines look like
    /// <c>data: {"choices":[{"delta":{"content":"Hi"}}]}</c> and end with <c>data: [DONE]</c>.
    /// </summary>
    public static (List<Piece> Pieces, bool Done) ParseLine(string line)
    {
        var pieces = new List<Piece>();
        if (!line.StartsWith("data:", StringComparison.Ordinal)) return (pieces, false); // blank or ": keep-alive"
        var payload = line[5..].Trim();
        if (payload == "[DONE]") return (pieces, true);

        JsonDocument doc;
        try { doc = JsonDocument.Parse(payload); }
        catch (JsonException) { return (pieces, false); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var error))
                throw new UserFacingException("OpenRouter stopped with an error: " + MessageOf(error));
            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array) return (pieces, false);
            foreach (var choice in choices.EnumerateArray())
            {
                if (!choice.TryGetProperty("delta", out var delta)) continue;
                if (delta.TryGetProperty("reasoning", out var r) && r.ValueKind == JsonValueKind.String && r.GetString() is { Length: > 0 } thinking)
                    pieces.Add(new Piece(PieceKind.Thinking, thinking));
                if (delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String && c.GetString() is { Length: > 0 } text)
                    pieces.Add(new Piece(PieceKind.Text, text));
            }
        }
        return (pieces, false);
    }

    public static string ErrorText(HttpStatusCode status, string body)
    {
        var detail = "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e)) detail = MessageOf(e);
        }
        catch (JsonException) { }

        return status switch
        {
            HttpStatusCode.Unauthorized => "OpenRouter didn't accept your key. Check it in Settings → Cloud.",
            HttpStatusCode.PaymentRequired => "Your OpenRouter account is out of credit. Add some at openrouter.ai, or pick a free model.",
            HttpStatusCode.TooManyRequests => "OpenRouter says there have been too many requests. Wait a minute and try again.",
            _ => $"OpenRouter error ({(int)status}){(detail.Length > 0 ? ": " + detail : "")}",
        };
    }

    static string MessageOf(JsonElement error) =>
        error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var m) ? m.GetString() ?? "" : error.ToString();
}
