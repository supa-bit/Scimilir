using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Scimilir.Storage;

namespace Scimilir.Providers;

public sealed record HfRepo(string Id, long Downloads, long Likes);

/// <summary>A model you can download. Big ones are split, so Paths may hold several parts.</summary>
public sealed record HfFile(string Name, long Size, IReadOnlyList<string> Paths);

/// <summary>One download, as the settings page shows it.</summary>
public sealed class DownloadJob
{
    public string Id { get; init; } = "";
    public string Repo { get; init; } = "";
    public string Name { get; init; } = "";
    public long Total { get; init; }
    public long Done { get; set; }
    /// <summary>"downloading", "done", "failed" or "cancelled".</summary>
    public string State { get; set; } = "downloading";
    public string? Error { get; set; }
    internal CancellationTokenSource Cancel { get; } = new();
}

/// <summary>
/// Finds and downloads model files from Hugging Face (huggingface.co), the main
/// free library of AI models, into data/models.
/// </summary>
public sealed partial class ModelDownloads(HttpClient http, AppPaths paths, ILogger<ModelDownloads> log)
{
    const string Site = "https://huggingface.co/";
    readonly ConcurrentDictionary<string, DownloadJob> _jobs = new();

    [GeneratedRegex(@"^[A-Za-z0-9][\w.-]*/[\w.-]+$")]
    private static partial Regex RepoPattern();

    public static bool IsValidRepo(string repo) => RepoPattern().IsMatch(repo) && !repo.Contains("..");

    public IEnumerable<DownloadJob> Jobs => _jobs.Values.OrderBy(j => j.Id);

    /// <summary>Model collections on Hugging Face that contain .gguf files, most downloaded first.</summary>
    public async Task<List<HfRepo>> SearchAsync(string query, CancellationToken ct)
    {
        var url = $"{Site}api/models?filter=gguf&sort=downloads&direction=-1&limit=30&search={Uri.EscapeDataString(query.Trim())}";
        using var doc = JsonDocument.Parse(await http.GetStringAsync(url, ct));
        return [.. doc.RootElement.EnumerateArray()
            .Select(m => new HfRepo(
                m.GetProperty("id").GetString() ?? "",
                m.TryGetProperty("downloads", out var d) && d.TryGetInt64(out var dl) ? dl : 0,
                m.TryGetProperty("likes", out var l) && l.TryGetInt64(out var lk) ? lk : 0))
            .Where(r => IsValidRepo(r.Id))];
    }

    /// <summary>The model files in one Hugging Face collection.</summary>
    public async Task<List<HfFile>> ListFilesAsync(string repo, CancellationToken ct)
    {
        if (!IsValidRepo(repo)) throw new UserFacingException("That doesn't look like a Hugging Face model name (it should be like \"owner/name\").");
        string json;
        try
        {
            json = await http.GetStringAsync($"{Site}api/models/{repo}/tree/main?recursive=true", ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Unauthorized)
        {
            throw new UserFacingException($"Hugging Face has no public model called \"{repo}\".");
        }
        using var doc = JsonDocument.Parse(json);
        return GroupFiles(doc.RootElement);
    }

    /// <summary>Turns Hugging Face's file list into downloadable models, joining split parts into one.</summary>
    public static List<HfFile> GroupFiles(JsonElement tree)
    {
        var files = new List<(string Path, long Size)>();
        foreach (var e in tree.EnumerateArray())
        {
            if (e.TryGetProperty("type", out var t) && t.GetString() != "file") continue;
            var path = e.GetProperty("path").GetString() ?? "";
            var name = path[(path.LastIndexOf('/') + 1)..];
            if (!ModelFiles.IsSafeName(name) || name.Contains("mmproj", StringComparison.OrdinalIgnoreCase)) continue;
            var size = e.TryGetProperty("lfs", out var lfs) && lfs.TryGetProperty("size", out var ls) ? ls.GetInt64()
                : e.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
            files.Add((path, size));
        }

        var result = new List<HfFile>();
        foreach (var group in files.GroupBy(f =>
                 {
                     var m = ModelFiles.SplitPart().Match(f.Path[(f.Path.LastIndexOf('/') + 1)..]);
                     return m.Success ? $"{f.Path[..(f.Path.LastIndexOf('/') + 1)]}{m.Groups["stem"].Value}|{m.Groups["total"].Value}" : f.Path;
                 }))
        {
            var parts = group.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
            var first = parts[0].Path;
            result.Add(new HfFile(first[(first.LastIndexOf('/') + 1)..], parts.Sum(p => p.Size), [.. parts.Select(p => p.Path)]));
        }
        return [.. result.OrderBy(f => f.Size)];
    }

    /// <summary>Starts downloading a model in the background.</summary>
    public async Task<DownloadJob> StartAsync(string repo, string name, CancellationToken ct)
    {
        // Look the file up again rather than trusting the request, so only real files are fetched.
        var file = (await ListFilesAsync(repo, ct)).FirstOrDefault(f => f.Name == name)
                   ?? throw new UserFacingException("That file isn't in this model's list any more.");
        if (_jobs.Values.Any(j => j.Name == file.Name && j.State == "downloading"))
            throw new UserFacingException("That model is already downloading.");
        if (File.Exists(Path.Combine(paths.Models, file.Name)))
            throw new UserFacingException("You already have this model.");

        var free = new DriveInfo(Path.GetPathRoot(paths.Models)!).AvailableFreeSpace;
        if (free < file.Size + (1L << 30))
            throw new UserFacingException(
                $"Not enough free disk space: this model needs {LocalProvider.FormatSize(file.Size)} and the drive has {LocalProvider.FormatSize(free)} free.");

        var job = new DownloadJob { Id = DateTime.UtcNow.Ticks.ToString(), Repo = repo, Name = file.Name, Total = file.Size };
        _jobs[job.Id] = job;
        _ = Task.Run(() => RunAsync(job, file));
        return job;
    }

    public bool Cancel(string id)
    {
        if (!_jobs.TryGetValue(id, out var job) || job.State != "downloading") return false;
        job.Cancel.Cancel();
        return true;
    }

    /// <summary>Clears finished, failed and cancelled downloads from the list.</summary>
    public void ClearFinished()
    {
        foreach (var job in _jobs.Values.Where(j => j.State != "downloading"))
            _jobs.TryRemove(job.Id, out _);
    }

    async Task RunAsync(DownloadJob job, HfFile file)
    {
        var finished = new List<string>();
        try
        {
            foreach (var part in file.Paths)
            {
                var target = Path.Combine(paths.Models, part[(part.LastIndexOf('/') + 1)..]);
                var temp = target + ".part";
                var url = $"{Site}{job.Repo}/resolve/main/{string.Join('/', part.Split('/').Select(Uri.EscapeDataString))}";
                using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, job.Cancel.Token))
                {
                    response.EnsureSuccessStatusCode();
                    await using var source = await response.Content.ReadAsStreamAsync(job.Cancel.Token);
                    await using var output = File.Create(temp);
                    var buffer = new byte[1 << 20];
                    int read;
                    while ((read = await source.ReadAsync(buffer, job.Cancel.Token)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read), job.Cancel.Token);
                        job.Done += read;
                    }
                }
                File.Move(temp, target, overwrite: true);
                finished.Add(target);
            }
            job.State = "done";
            log.LogInformation("Downloaded {Name}", job.Name);
        }
        catch (Exception ex)
        {
            job.State = job.Cancel.IsCancellationRequested ? "cancelled" : "failed";
            job.Error = job.State == "failed" ? ex.Message : null;
            if (job.State == "failed") log.LogWarning(ex, "Download of {Name} failed", job.Name);
            // Don't leave half a model behind: remove the unfinished file and any parts already done.
            foreach (var part in file.Paths)
            {
                var target = Path.Combine(paths.Models, part[(part.LastIndexOf('/') + 1)..]);
                TryDelete(target + ".part");
                if (finished.Contains(target)) TryDelete(target);
            }
        }
    }

    static void TryDelete(string file)
    {
        try { File.Delete(file); }
        catch (IOException) { }
    }
}
