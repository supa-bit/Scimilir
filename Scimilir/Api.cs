using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.Features;
using Scimilir.Providers;
using Scimilir.Storage;

namespace Scimilir;

/// <summary>Facts about this run of Scimilir.</summary>
public sealed record RunInfo(int Port, string Version);

/// <summary>
/// The web addresses the chat page calls (all start with /api). Everything but
/// status, setup and login needs you to be logged in.
/// </summary>
public static class Api
{
    public sealed record PasswordRequest(string Password);
    public sealed record ChangePasswordRequest(string Current, string Next);
    public sealed record SendRequest(string? ChatId, string Model, string? Content, bool Regenerate);
    public sealed record RenameRequest(string Title);
    public sealed record DownloadRequest(string Repo, string File);
    public sealed record SettingsUpdate(
        string? SystemPrompt, float? Temperature, int? MaxReplyTokens, int? ContextSize,
        int? GpuLayers, bool? OpenBrowserOnStart, int? Port, string? OpenRouterKey);

    static object Msg(string message) => new { message };
    static IResult Problem(string message, int status = 400) => Results.Json(Msg(message), statusCode: status);
    static bool IsFromThisPc(HttpContext c) => c.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip);

    public static void MapScimilirApi(this WebApplication app)
    {
        var open = app.MapGroup("/api");
        open.MapGet("/status", (HttpContext c, SettingsStore s, RunInfo run) => new
        {
            setupNeeded = s.Current.PasswordHash is null,
            loggedIn = c.User.Identity?.IsAuthenticated == true,
            fromThisPc = IsFromThisPc(c),
            version = run.Version,
        });
        open.MapPost("/setup", Setup);
        open.MapPost("/login", Login);
        open.MapPost("/logout", (HttpContext c) => c.SignOutAsync());

        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapGet("/models", ListModels);

        api.MapGet("/chats", (ChatStore chats) => chats.List());
        api.MapGet("/chats/{id}", (string id, ChatStore chats) => chats.Get(id) is { } chat ? Results.Ok(chat) : Results.NotFound());
        api.MapDelete("/chats/{id}", (string id, ChatStore chats) => chats.Delete(id) ? Results.NoContent() : Results.NotFound());
        api.MapPatch("/chats/{id}", (string id, RenameRequest r, ChatStore chats) =>
        {
            if (chats.Get(id) is not { } chat) return Results.NotFound();
            chat.Title = ChatStore.TitleFrom(r.Title);
            chats.Save(chat);
            return Results.Ok(new ChatSummary(chat.Id, chat.Title, chat.Updated));
        });
        api.MapPost("/chats/send", Send);

        api.MapGet("/settings", (SettingsStore s) => SettingsView(s.Current));
        api.MapPut("/settings", UpdateSettings);
        api.MapPost("/password", ChangePassword);

        api.MapGet("/local", LocalStatus);
        api.MapDelete("/local/models/{file}", DeleteLocalModel);
        api.MapPost("/local/unload", async (LocalProvider local) =>
        {
            await local.UnloadAsync();
            return Results.NoContent();
        });

        api.MapGet("/hf/search", (string q, ModelDownloads d, CancellationToken ct) => FromHuggingFace(() => d.SearchAsync(q, ct)));
        api.MapGet("/hf/files", (string repo, ModelDownloads d, CancellationToken ct) => FromHuggingFace(() => d.ListFilesAsync(repo, ct)));
        api.MapPost("/hf/download", (DownloadRequest r, ModelDownloads d, CancellationToken ct) => FromHuggingFace(() => d.StartAsync(r.Repo, r.File, ct)));
        api.MapGet("/downloads", (ModelDownloads d) => d.Jobs);
        api.MapPost("/downloads/{id}/cancel", (string id, ModelDownloads d) => d.Cancel(id) ? Results.NoContent() : Results.NotFound());
        api.MapPost("/downloads/clear", (ModelDownloads d) =>
        {
            d.ClearFinished();
            return Results.NoContent();
        });

        api.MapGet("/connect", (RunInfo run) => new { urls = Network.LanUrls(run.Port) });
        api.MapGet("/connect/qr", (string url, RunInfo run) =>
            Network.LanUrls(run.Port).Contains(url) ? Results.Text(Network.QrSvg(url), "image/svg+xml") : Results.NotFound());
    }

    static async Task SignIn(HttpContext c, string passwordHash)
    {
        var identity = new ClaimsIdentity([new Claim("pw", Passwords.Stamp(passwordHash))], CookieAuthenticationDefaults.AuthenticationScheme);
        await c.SignInAsync(new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = true });
    }

    static async Task<IResult> Setup(PasswordRequest r, HttpContext c, SettingsStore s)
    {
        if (s.Current.PasswordHash is not null) return Problem("A password is already set.", 409);
        // Only the PC itself may choose the first password, so nobody else on
        // the Wi-Fi can get in before you do.
        if (!IsFromThisPc(c)) return Problem("Finish setting up on the PC that runs Scimilir first.", 403);
        if (r.Password.Length < Passwords.MinLength) return Problem($"Use at least {Passwords.MinLength} characters.");
        var hash = Passwords.Hash(r.Password);
        s.Update(x => x.PasswordHash = hash);
        await SignIn(c, hash);
        return Results.NoContent();
    }

    static async Task<IResult> Login(PasswordRequest r, HttpContext c, SettingsStore s, LoginThrottle throttle)
    {
        var who = c.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (throttle.WaitFor(who) is { } wait)
            return Problem($"Too many wrong passwords. Try again in {Math.Ceiling(wait.TotalMinutes)} minute(s).", 429);
        var hash = s.Current.PasswordHash;
        if (hash is null) return Problem("Scimilir isn't set up yet. Open it on the PC first.", 409);
        if (!Passwords.Verify(r.Password, hash))
        {
            throttle.Failed(who);
            return Problem("That password isn't right.", 401);
        }
        throttle.Succeeded(who);
        await SignIn(c, hash);
        return Results.NoContent();
    }

    static async Task<IResult> ListModels(ProviderHub hub, SettingsStore s, CancellationToken ct)
    {
        var models = new List<ModelInfo>();
        var problems = new List<string>();
        foreach (var provider in hub.All)
        {
            try
            {
                models.AddRange(await provider.ListModelsAsync(ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                problems.Add($"Couldn't get the {provider.Prefix} model list: {ex.Message}");
            }
        }
        return Results.Ok(new { models, problems, defaultModel = s.Current.DefaultModel });
    }

    /// <summary>
    /// Adds the message to the chat and streams the reply back as it is written,
    /// using "server-sent events": small text chunks like
    /// <c>event: text</c> / <c>data: {"text":"Hello"}</c>.
    /// If the page goes away or presses Stop, whatever was written so far is kept.
    /// </summary>
    static async Task Send(SendRequest req, HttpContext http, ChatStore chats, ProviderHub hub, SettingsStore store, ILogger<RunInfo> log)
    {
        async Task Fail(string message, int status = 400)
        {
            http.Response.StatusCode = status;
            await http.Response.WriteAsJsonAsync(Msg(message));
        }

        var settings = store.Current;
        if (hub.Resolve(req.Model) is not var (provider, model))
        {
            await Fail("Pick a model first.");
            return;
        }

        var now = DateTimeOffset.UtcNow;
        Chat chat;
        if (req.ChatId is null)
        {
            if (string.IsNullOrWhiteSpace(req.Content))
            {
                await Fail("Type a message first.");
                return;
            }
            chat = chats.Create(ChatStore.TitleFrom(req.Content));
        }
        else if (chats.Get(req.ChatId) is { } found)
        {
            chat = found;
        }
        else
        {
            await Fail("That chat doesn't exist any more.", 404);
            return;
        }

        if (req.Regenerate)
        {
            while (chat.Messages.Count > 0 && chat.Messages[^1].Role == "assistant")
                chat.Messages.RemoveAt(chat.Messages.Count - 1);
            if (chat.Messages.Count == 0)
            {
                await Fail("There's nothing to answer yet.");
                return;
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(req.Content))
            {
                await Fail("Type a message first.");
                return;
            }
            chat.Messages.Add(new ChatMessage { Role = "user", Content = req.Content, Time = now });
        }
        chat.Model = req.Model;
        chat.Updated = now;
        chats.Save(chat);
        if (settings.DefaultModel != req.Model) store.Update(s => s.DefaultModel = req.Model);

        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        var ct = http.RequestAborted;

        async Task Event(string name, object data)
        {
            try
            {
                await http.Response.WriteAsync($"event: {name}\ndata: {JsonSerializer.Serialize(data, JsonSerializerOptions.Web)}\n\n", ct);
                await http.Response.Body.FlushAsync(ct);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                // The page has gone; keep writing the reply to save it.
            }
        }

        await Event("chat", new { id = chat.Id, title = chat.Title });

        var text = new StringBuilder();
        var thinking = new StringBuilder();
        string? error = null;
        try
        {
            await foreach (var piece in provider.StreamAsync(model, Conversation.BuildTurns(chat, settings.SystemPrompt), settings, ct))
            {
                switch (piece.Kind)
                {
                    case PieceKind.Text:
                        text.Append(piece.Text);
                        await Event("text", new { text = piece.Text });
                        break;
                    case PieceKind.Thinking:
                        thinking.Append(piece.Text);
                        await Event("thinking", new { text = piece.Text });
                        break;
                    case PieceKind.Status:
                        await Event("status", new { text = piece.Text });
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (UserFacingException ex) { error = ex.Message; }
        catch (HttpRequestException ex) { error = "Couldn't reach the AI service: " + ex.Message; }
        catch (Exception ex)
        {
            log.LogError(ex, "Writing a reply failed");
            error = "Something went wrong while writing the reply: " + ex.Message;
        }

        // Cloud models send their thinking separately; save it the same way local models write it.
        var content = (thinking.Length > 0 ? $"<think>{thinking}</think>\n\n" : "") + text;
        if (content.Trim().Length > 0 && chats.Get(chat.Id) is { } latest)
        {
            latest.Messages.Add(new ChatMessage { Role = "assistant", Content = content, Model = req.Model, Time = DateTimeOffset.UtcNow });
            latest.Updated = DateTimeOffset.UtcNow;
            chats.Save(latest);
        }
        if (error is not null) await Event("error", new { message = error });
        await Event("done", new { stopped = ct.IsCancellationRequested });
    }

    static object SettingsView(Settings s) => new
    {
        s.SystemPrompt,
        s.Temperature,
        s.MaxReplyTokens,
        s.ContextSize,
        s.GpuLayers,
        s.OpenBrowserOnStart,
        s.Port,
        hasOpenRouterKey = !string.IsNullOrEmpty(s.OpenRouterKey),
        // Enough of the key to recognise it, never the whole thing.
        openRouterKeyHint = s.OpenRouterKey is { Length: > 12 } k ? $"{k[..6]}…{k[^4..]}" : null,
    };

    static IResult UpdateSettings(SettingsUpdate u, SettingsStore store)
    {
        if (u.Temperature is < 0 or > 2) return Problem("Creativity (temperature) must be between 0 and 2.");
        if (u.MaxReplyTokens is < 16 or > 65536) return Problem("Longest reply must be between 16 and 65536.");
        if (u.ContextSize is < 512 or > 262144) return Problem("Memory (context size) must be between 512 and 262144.");
        if (u.GpuLayers is < 0 or > 999) return Problem("GPU layers must be between 0 and 999.");
        if (u.Port is < 1024 or > 65535) return Problem("The port must be between 1024 and 65535.");

        store.Update(s =>
        {
            if (u.SystemPrompt is not null) s.SystemPrompt = u.SystemPrompt;
            if (u.Temperature is { } t) s.Temperature = t;
            if (u.MaxReplyTokens is { } m) s.MaxReplyTokens = m;
            if (u.ContextSize is { } c) s.ContextSize = c;
            if (u.GpuLayers is { } g) s.GpuLayers = g;
            if (u.OpenBrowserOnStart is { } b) s.OpenBrowserOnStart = b;
            if (u.Port is { } p) s.Port = p;
            // An empty key removes it.
            if (u.OpenRouterKey is not null) s.OpenRouterKey = u.OpenRouterKey.Trim() is { Length: > 0 } key ? key : null;
        });
        return Results.Ok(SettingsView(store.Current));
    }

    static async Task<IResult> ChangePassword(ChangePasswordRequest r, HttpContext c, SettingsStore s)
    {
        if (!Passwords.Verify(r.Current, s.Current.PasswordHash)) return Problem("Your current password isn't right.");
        if (r.Next.Length < Passwords.MinLength) return Problem($"Use at least {Passwords.MinLength} characters.");
        var hash = Passwords.Hash(r.Next);
        s.Update(x => x.PasswordHash = hash);
        // Signing in again keeps this browser logged in; all others are signed out.
        await SignIn(c, hash);
        return Results.NoContent();
    }

    static object LocalStatus(AppPaths paths, LocalProvider local) => new
    {
        models = Directory.EnumerateFiles(paths.Models, "*.gguf")
            .Select(Path.GetFileName).OfType<string>()
            .Where(ModelFiles.IsChatModel)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Select(n => new
            {
                file = n,
                name = ModelFiles.DisplayName(n),
                size = ModelFiles.FilesOf(paths.Models, n).Sum(f => new FileInfo(f).Length),
                loaded = local.LoadedModel == n,
            }),
        folder = paths.Models,
        freeBytes = new DriveInfo(Path.GetPathRoot(paths.Models)!).AvailableFreeSpace,
        loaded = local.LoadedModel,
        busy = local.Busy,
        onProcessorOnly = local.LoadedModel is not null && local.OnProcessorOnly,
        device = LocalProvider.DeviceInfo,
    };

    static async Task<IResult> DeleteLocalModel(string file, AppPaths paths, LocalProvider local)
    {
        if (!ModelFiles.IsSafeName(file) || !File.Exists(Path.Combine(paths.Models, file))) return Results.NotFound();
        if (local.LoadedModel == file)
        {
            if (local.Busy) return Problem("This model is writing a reply. Wait for it to finish first.", 409);
            await local.UnloadAsync();
        }
        try
        {
            foreach (var f in ModelFiles.FilesOf(paths.Models, file).ToList()) File.Delete(f);
        }
        catch (IOException ex)
        {
            return Problem("Couldn't delete the file: " + ex.Message, 409);
        }
        return Results.NoContent();
    }

    static async Task<IResult> FromHuggingFace<T>(Func<Task<T>> call)
    {
        try
        {
            return Results.Ok(await call());
        }
        catch (UserFacingException ex)
        {
            return Problem(ex.Message);
        }
        catch (HttpRequestException ex)
        {
            return Problem("Couldn't reach Hugging Face: " + ex.Message, 502);
        }
    }
}
