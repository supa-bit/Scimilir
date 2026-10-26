using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http;

namespace Scimilir.Tests;

/// <summary>Runs the whole app (with a fresh data folder each time) and uses it like the page does.</summary>
public sealed class AppTests : IDisposable
{
    const string Password = "secret123";
    readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    /// <summary>The app, with requests seeming to come from the PC itself, or from another device.</summary>
    sealed class TestApp : WebApplicationFactory<Program>
    {
        public string Data { get; } = Directory.CreateTempSubdirectory("scimilir-test").FullName;
        public bool FromThisPc { get; set; } = true;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("DataFolder", Data);
            builder.ConfigureServices(s => s.AddSingleton<IStartupFilter>(new RemoteAddress(this)));
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { Directory.Delete(Data, recursive: true); }
            catch (IOException) { }
        }

        sealed class RemoteAddress(TestApp app) : IStartupFilter
        {
            public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => builder =>
            {
                builder.Use((HttpContext context, RequestDelegate n) =>
                {
                    context.Connection.RemoteIpAddress = app.FromThisPc ? IPAddress.Loopback : IPAddress.Parse("192.168.1.50");
                    return n(context);
                });
                next(builder);
            };
        }
    }

    async Task<HttpClient> LoggedIn()
    {
        var client = _app.CreateClient();
        var status = await client.GetFromJsonAsync<JsonElement>("/api/status");
        var path = status.GetProperty("setupNeeded").GetBoolean() ? "/api/setup" : "/api/login";
        (await client.PostAsJsonAsync(path, new { password = Password })).EnsureSuccessStatusCode();
        return client;
    }

    static async Task<string> Send(HttpClient client, object body)
    {
        var response = await client.PostAsJsonAsync("/api/chats/send", body);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    static string ChatIdFrom(string events)
    {
        var line = events.Split('\n').SkipWhile(l => l != "event: chat").Skip(1).First();
        return JsonDocument.Parse(line["data: ".Length..]).RootElement.GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task First_visit_from_this_PC_sets_the_password()
    {
        var client = _app.CreateClient();
        var status = await client.GetFromJsonAsync<JsonElement>("/api/status");
        Assert.True(status.GetProperty("setupNeeded").GetBoolean());

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/setup", new { password = "123" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/setup", new { password = Password })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/setup", new { password = "takeover!" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/chats")).StatusCode);
        Assert.DoesNotContain(Password, File.ReadAllText(Path.Combine(_app.Data, "settings.json")));
    }

    [Fact]
    public async Task Another_device_cannot_set_the_first_password()
    {
        _app.FromThisPc = false;
        var client = _app.CreateClient();
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/setup", new { password = Password })).StatusCode);
    }

    [Fact]
    public async Task Chats_and_settings_need_a_login()
    {
        await LoggedIn();
        var stranger = _app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await stranger.GetAsync("/api/chats")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await stranger.GetAsync("/api/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await stranger.PostAsJsonAsync("/api/chats/send", new { model = "local:x.gguf", content = "hi" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await stranger.GetAsync("/")).StatusCode); // the page itself loads, to show the login
    }

    [Fact]
    public async Task Wrong_passwords_are_refused_then_slowed_down()
    {
        await LoggedIn();
        var guesser = _app.CreateClient();
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await guesser.PostAsJsonAsync("/api/login", new { password = "guess" + i })).StatusCode);
        // Even the right password must wait now.
        Assert.Equal(HttpStatusCode.TooManyRequests, (await guesser.PostAsJsonAsync("/api/login", new { password = Password })).StatusCode);
    }

    [Fact]
    public async Task Changing_the_password_logs_out_other_devices()
    {
        var pc = await LoggedIn();
        var phone = await LoggedIn();
        Assert.Equal(HttpStatusCode.BadRequest,
            (await pc.PostAsJsonAsync("/api/password", new { current = "nope", next = "newsecret" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await pc.PostAsJsonAsync("/api/password", new { current = Password, next = "newsecret" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await pc.GetAsync("/api/chats")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.GetAsync("/api/chats")).StatusCode);
    }

    [Fact]
    public async Task Settings_are_checked_and_the_cloud_key_is_never_sent_back()
    {
        var client = await LoggedIn();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/settings", new { temperature = 5 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/settings", new { gpuLayers = -1 })).StatusCode);

        const string key = "sk-or-v1-0123456789abcdef";
        var saved = await (await client.PutAsJsonAsync("/api/settings", new { openRouterKey = key, temperature = 0.3 }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(saved.GetProperty("hasOpenRouterKey").GetBoolean());
        Assert.Equal("sk-or-…cdef", saved.GetProperty("openRouterKeyHint").GetString());
        Assert.Equal(0.3f, saved.GetProperty("temperature").GetSingle());
        Assert.DoesNotContain(key, await client.GetStringAsync("/api/settings"));

        var removed = await (await client.PutAsJsonAsync("/api/settings", new { openRouterKey = "" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(removed.GetProperty("hasOpenRouterKey").GetBoolean());
    }

    [Fact]
    public async Task A_model_on_this_PC_answers_and_the_chat_is_saved()
    {
        var client = await LoggedIn();
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Assets", "tiny-chat.gguf"), Path.Combine(_app.Data, "models", "tiny-chat.gguf"));
        await client.PutAsJsonAsync("/api/settings", new { maxReplyTokens = 24, gpuLayers = 0, systemPrompt = "Be brief." });

        var models = await client.GetFromJsonAsync<JsonElement>("/api/models");
        Assert.Contains(models.GetProperty("models").EnumerateArray(), m => m.GetProperty("id").GetString() == "local:tiny-chat.gguf");

        var events = await Send(client, new { model = "local:tiny-chat.gguf", content = "Hello there" });
        Assert.Contains("event: chat", events);
        Assert.Contains("event: status", events); // "Loading the model…"
        Assert.Contains("event: text", events);
        Assert.DoesNotContain("event: error", events);
        Assert.EndsWith("event: done\ndata: {\"stopped\":false}\n\n", events);

        var id = ChatIdFrom(events);
        var chat = await client.GetFromJsonAsync<JsonElement>($"/api/chats/{id}");
        Assert.Equal("Hello there", chat.GetProperty("title").GetString());
        var messages = chat.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(["user", "assistant"], messages.Select(m => m.GetProperty("role").GetString()));
        Assert.NotEmpty(messages[1].GetProperty("content").GetString()!);
        Assert.Equal("local:tiny-chat.gguf", messages[1].GetProperty("model").GetString());

        // A second message continues the same chat; "try again" replaces only the last reply.
        await Send(client, new { chatId = id, model = "local:tiny-chat.gguf", content = "And again" });
        await Send(client, new { chatId = id, model = "local:tiny-chat.gguf", regenerate = true });
        chat = await client.GetFromJsonAsync<JsonElement>($"/api/chats/{id}");
        Assert.Equal(["user", "assistant", "user", "assistant"],
            chat.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("role").GetString()));

        var local = await client.GetFromJsonAsync<JsonElement>("/api/local");
        Assert.Equal("tiny-chat.gguf", local.GetProperty("loaded").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync("/api/local/models/..%2Fsettings.json")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/local/models/tiny-chat.gguf")).StatusCode);
        Assert.False(File.Exists(Path.Combine(_app.Data, "models", "tiny-chat.gguf")));
    }

    [Fact]
    public async Task Problems_are_explained_instead_of_crashing()
    {
        var client = await LoggedIn();
        var noModel = await client.PostAsJsonAsync("/api/chats/send", new { model = "nonsense", content = "hi" });
        Assert.Equal(HttpStatusCode.BadRequest, noModel.StatusCode);
        Assert.Contains("Pick a model", await noModel.Content.ReadAsStringAsync());

        // A model file that isn't there: the chat is kept, and the reason is sent as an error event.
        var events = await Send(client, new { model = "local:missing.gguf", content = "hi" });
        Assert.Contains("event: error", events);
        Assert.Contains("in the models folder", events);

        // The cloud without a key.
        events = await Send(client, new { model = "openrouter:some/model", content = "hi" });
        Assert.Contains("Add your OpenRouter key", events);

        var gone = await client.PostAsJsonAsync("/api/chats/send", new { chatId = "abc123", model = "local:x.gguf", content = "hi" });
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }
}
