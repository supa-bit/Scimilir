using System.Text.Json;

namespace Scimilir.Storage;

/// <summary>Everything the user can change, saved in data/settings.json.</summary>
public sealed class Settings
{
    /// <summary>The network port Scimilir listens on (takes effect after a restart).</summary>
    public int Port { get; set; } = 5080;
    public string? PasswordHash { get; set; }
    public string? OpenRouterKey { get; set; }
    /// <summary>The model picked last, used for new chats.</summary>
    public string? DefaultModel { get; set; }
    public string SystemPrompt { get; set; } = "";
    public float Temperature { get; set; } = 0.7f;
    /// <summary>The longest reply a model on this PC may write, in tokens.</summary>
    public int MaxReplyTokens { get; set; } = 2048;
    /// <summary>How much of the chat a model on this PC can keep in mind, in tokens.</summary>
    public int ContextSize { get; set; } = 8192;
    /// <summary>How many layers of a local model go on the graphics card; 999 means all of them.</summary>
    public int GpuLayers { get; set; } = 999;
    public bool OpenBrowserOnStart { get; set; } = true;
}

/// <summary>Loads and saves <see cref="Settings"/>. Safe to use from several requests at once.</summary>
public sealed class SettingsStore
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    readonly string _file;
    readonly Lock _lock = new();
    Settings _current;

    public SettingsStore(AppPaths paths)
    {
        _file = paths.SettingsFile;
        _current = Load();
    }

    /// <summary>A copy of the current settings; changing it changes nothing.</summary>
    public Settings Current
    {
        get { lock (_lock) return Clone(_current); }
    }

    /// <summary>Changes the settings and saves them straight away.</summary>
    public void Update(Action<Settings> change)
    {
        lock (_lock)
        {
            var next = Clone(_current);
            change(next);
            var temp = _file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(next, Json));
            File.Move(temp, _file, overwrite: true);
            _current = next;
        }
    }

    Settings Load()
    {
        if (!File.Exists(_file)) return new Settings();
        try
        {
            return JsonSerializer.Deserialize<Settings>(File.ReadAllText(_file)) ?? new Settings();
        }
        catch (JsonException)
        {
            // Keep the broken file so nothing is lost, and start again with defaults.
            File.Copy(_file, _file + ".broken", overwrite: true);
            return new Settings();
        }
    }

    static Settings Clone(Settings s) => JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(s))!;
}
