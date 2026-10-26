using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using LLama;
using LLama.Common;
using LLama.Exceptions;
using LLama.Native;
using LLama.Sampling;
using LLama.Transformers;
using Scimilir.Storage;

namespace Scimilir.Providers;

/// <summary>Rules for model files in data/models.</summary>
public static partial class ModelFiles
{
    // Big models come split into parts: name-00001-of-00003.gguf, name-00002-of-00003.gguf ...
    [GeneratedRegex(@"^(?<stem>.+)-(?<part>\d{5})-of-(?<total>\d{5})\.gguf$", RegexOptions.IgnoreCase)]
    public static partial Regex SplitPart();

    /// <summary>True for a plain .gguf file name with no folder parts in it.</summary>
    public static bool IsSafeName(string name) =>
        name.Length is > 5 and < 256
        && name.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)
        && name == Path.GetFileName(name)
        && !name.Contains("..")
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && name.IndexOfAny(['/', '\\', ':']) < 0;

    /// <summary>
    /// Files that hold a model you can chat with: skips the later parts of split
    /// models (the first part loads the rest) and "mmproj" image helpers.
    /// </summary>
    public static bool IsChatModel(string name)
    {
        if (name.Contains("mmproj", StringComparison.OrdinalIgnoreCase)) return false;
        var split = SplitPart().Match(name);
        return !split.Success || split.Groups["part"].Value == "00001";
    }

    /// <summary>Every file belonging to a model: just itself, or all parts of a split model.</summary>
    public static IEnumerable<string> FilesOf(string folder, string name)
    {
        var split = SplitPart().Match(name);
        if (!split.Success) return [Path.Combine(folder, name)];
        var stem = split.Groups["stem"].Value;
        var total = split.Groups["total"].Value;
        return Directory.EnumerateFiles(folder, "*.gguf")
            .Where(f => SplitPart().Match(Path.GetFileName(f)) is { Success: true } m
                        && m.Groups["stem"].Value == stem && m.Groups["total"].Value == total);
    }

    public static string DisplayName(string name)
    {
        var split = SplitPart().Match(name);
        return split.Success ? split.Groups["stem"].Value : Path.GetFileNameWithoutExtension(name);
    }
}

/// <summary>
/// Runs model files on this PC through LLamaSharp. One model is kept loaded at a
/// time, and one reply is written at a time.
/// </summary>
public sealed class LocalProvider(AppPaths paths, ILogger<LocalProvider> log) : IChatProvider, IDisposable
{
    static int _nativeConfigured;
    // What the engine said about graphics cards while loading the current model.
    static readonly List<string> DeviceLines = [];

    readonly SemaphoreSlim _gate = new(1, 1);
    LLamaWeights? _weights;
    ModelParams? _params;
    string? _loadedFile;
    (int GpuLayers, int Context) _loadedWith;
    int _contextSize;
    bool _onProcessorOnly;

    public string Prefix => "local";
    public string? LoadedModel => _loadedFile;
    public bool OnProcessorOnly => _onProcessorOnly;
    public bool Busy => _gate.CurrentCount == 0;

    /// <summary>
    /// What the engine said about graphics cards while loading the current model,
    /// like "using device Vulkan0 (NVIDIA GeForce RTX 3060)" and "offloaded 29/29 layers to GPU".
    /// </summary>
    public static IReadOnlyList<string> DeviceInfo
    {
        get { lock (DeviceLines) return [.. DeviceLines]; }
    }

    /// <summary>
    /// Picks the engine build to use, before anything else touches LLamaSharp:
    /// the graphics-card (Vulkan) one when it works, the processor one when not.
    /// </summary>
    public static void ConfigureEngine(ILogger log)
    {
        if (Interlocked.Exchange(ref _nativeConfigured, 1) == 1) return;
        NativeLibraryConfig.All
            .WithVulkan(true)
            .WithAutoFallback(true)
            .WithLogCallback((level, message) =>
            {
                var line = message.TrimEnd();
                if (line.Contains("using device", StringComparison.Ordinal) || line.Contains("layers to GPU", StringComparison.Ordinal))
                    lock (DeviceLines) if (DeviceLines.Count < 8) DeviceLines.Add(line[(line.IndexOf(": ", StringComparison.Ordinal) + 2)..]);
                if (level is LLamaLogLevel.Error) log.LogError("{Engine}", line);
                else if (level is LLamaLogLevel.Warning) log.LogWarning("{Engine}", line);
                else log.LogDebug("{Engine}", line);
            });
    }

    public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct)
    {
        IReadOnlyList<ModelInfo> models = [.. Directory.EnumerateFiles(paths.Models, "*.gguf")
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(ModelFiles.IsChatModel)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Select(n => new ModelInfo($"local:{n}", ModelFiles.DisplayName(n), "On this PC",
                FormatSize(ModelFiles.FilesOf(paths.Models, n).Sum(f => new FileInfo(f).Length))))];
        return Task.FromResult(models);
    }

    public async IAsyncEnumerable<Piece> StreamAsync(string model, IReadOnlyList<Turn> turns, Settings settings,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var path = Path.Combine(paths.Models, model);
        if (!ModelFiles.IsSafeName(model) || !File.Exists(path))
            throw new UserFacingException("That model file isn't in the models folder any more. Pick another model.");

        if (!_gate.Wait(0))
        {
            yield return new Piece(PieceKind.Status, "Waiting for the other reply on this PC to finish…");
            await _gate.WaitAsync(ct);
        }
        try
        {
            if (_loadedFile != model || _loadedWith != (settings.GpuLayers, settings.ContextSize))
            {
                yield return new Piece(PieceKind.Status, "Loading the model… (the first reply can take a minute)");
                if (await LoadAsync(model, settings, ct) is { } note)
                    yield return new Piece(PieceKind.Status, note);
            }

            var prompt = BuildPrompt(turns, settings, out var dropped);
            if (dropped > 0)
                yield return new Piece(PieceKind.Status,
                    $"This chat is longer than the model can keep in mind, so the {dropped} oldest message(s) were left out.");

            var executor = new StatelessExecutor(_weights!, _params!) { ApplyTemplate = false };
            var inference = new InferenceParams
            {
                MaxTokens = settings.MaxReplyTokens,
                SamplingPipeline = new DefaultSamplingPipeline { Temperature = settings.Temperature },
            };
            await foreach (var text in executor.InferAsync(prompt, inference, ct))
                if (text.Length > 0) yield return new Piece(PieceKind.Text, text);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Frees the memory the loaded model uses. Waits for a reply in progress.</summary>
    public async Task UnloadAsync()
    {
        await _gate.WaitAsync();
        try { Unload(); }
        finally { _gate.Release(); }
    }

    /// <summary>Loads a model; returns a note for the user if it had to fall back to the processor.</summary>
    async Task<string?> LoadAsync(string file, Settings settings, CancellationToken ct)
    {
        Unload();
        lock (DeviceLines) DeviceLines.Clear();
        var path = Path.Combine(paths.Models, file);
        string? note = null;
        try
        {
            _weights = await LLamaWeights.LoadFromFileAsync(Params(path, settings.ContextSize, settings.GpuLayers), ct);
            _params = Params(path, ContextFor(_weights, settings), settings.GpuLayers);
            // Making a context here checks that the chat memory fits too.
            using var _ = _weights.CreateContext(_params);
            _onProcessorOnly = settings.GpuLayers == 0;
        }
        catch (Exception ex) when (settings.GpuLayers > 0 && ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Loading {File} on the graphics card failed; trying the processor", file);
            Unload();
            _weights = await LoadOnProcessorAsync(Params(path, settings.ContextSize, 0), ct);
            _params = Params(path, ContextFor(_weights, settings), 0);
            _onProcessorOnly = true;
            note = "The model didn't fit on your graphics card, so it is running on the processor (slower). " +
                   "A smaller model, or a lower \"GPU layers\" number in Settings, will be faster.";
        }
        _contextSize = (int)_params.ContextSize!.Value;
        _loadedFile = file;
        _loadedWith = (settings.GpuLayers, settings.ContextSize);
        log.LogInformation("Loaded {File}", file);
        return note;
    }

    static ModelParams Params(string path, int contextSize, int gpuLayers) =>
        new(path) { ContextSize = (uint)contextSize, GpuLayerCount = gpuLayers };

    /// <summary>The memory setting, but no more than the model was made for.</summary>
    static int ContextFor(LLamaWeights weights, Settings settings) =>
        weights.ContextSize > 0 ? Math.Min(settings.ContextSize, weights.ContextSize) : settings.ContextSize;

    static async Task<LLamaWeights> LoadOnProcessorAsync(ModelParams p, CancellationToken ct)
    {
        try
        {
            return await LLamaWeights.LoadFromFileAsync(p, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new UserFacingException(
                $"This model couldn't be loaded ({ex.Message}). The file may be damaged or a type this version can't run; " +
                "try deleting it and downloading it again, or pick another model.");
        }
    }

    void Unload()
    {
        _weights?.Dispose();
        _weights = null;
        _params = null;
        _loadedFile = null;
    }

    /// <summary>
    /// Puts the chat into the model's own format, leaving out the oldest messages
    /// when the chat is too long for the model's memory.
    /// </summary>
    string BuildPrompt(IReadOnlyList<Turn> turns, Settings settings, out int dropped)
    {
        var list = turns.ToList();
        var budget = Math.Max(256, _contextSize - Math.Min(settings.MaxReplyTokens, _contextSize / 2));
        dropped = 0;
        while (true)
        {
            var prompt = ApplyTemplate(list);
            var count = _weights!.Tokenize(prompt, true, true, Encoding.UTF8).Length;
            if (count <= budget) return prompt;
            var oldest = list.FindIndex(t => t.Role != "system");
            if (oldest < 0 || oldest == list.Count - 1)
            {
                if (count < _contextSize - 64) return prompt;
                throw new UserFacingException(
                    "This message is too long for the model's memory. Make it shorter, or raise \"Memory (context size)\" in Settings.");
            }
            list.RemoveAt(oldest);
            dropped++;
        }
    }

    string ApplyTemplate(List<Turn> turns)
    {
        LLamaTemplate Fill(LLamaTemplate t)
        {
            foreach (var turn in turns) t.Add(turn.Role, turn.Content);
            return t;
        }

        try
        {
            return PromptTemplateTransformer.ToModelPrompt(Fill(new LLamaTemplate(_weights!, strict: false) { AddAssistant = true }));
        }
        catch (MissingTemplateException)
        {
            // The file doesn't say how it wants chats written; ChatML is the most common format.
            return PromptTemplateTransformer.ToModelPrompt(Fill(new LLamaTemplate("chatml") { AddAssistant = true }));
        }
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} MB",
        _ => $"{Math.Max(1, bytes / 1024)} KB",
    };

    public void Dispose()
    {
        Unload();
        _gate.Dispose();
    }
}
