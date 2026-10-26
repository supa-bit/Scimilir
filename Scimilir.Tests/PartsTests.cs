using System.Net;
using System.Text.Json;
using Scimilir.Providers;
using Scimilir.Storage;

namespace Scimilir.Tests;

/// <summary>Checks the small pieces on their own.</summary>
public class PartsTests
{
    sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void Password_hash_checks_the_right_password_only()
    {
        var hash = Passwords.Hash("correct horse");
        Assert.DoesNotContain("correct horse", hash);
        Assert.True(Passwords.Verify("correct horse", hash));
        Assert.False(Passwords.Verify("Correct horse", hash));
        Assert.False(Passwords.Verify("correct horse", null));
        Assert.False(Passwords.Verify("correct horse", "rubbish$1$2"));
        Assert.False(Passwords.Verify("correct horse", "pbkdf2-sha256$10$not base64$x"));
        Assert.NotEqual(hash, Passwords.Hash("correct horse")); // a fresh salt every time
        Assert.NotEqual(Passwords.Stamp(hash), Passwords.Stamp(Passwords.Hash("other")));
    }

    [Fact]
    public void Throttle_makes_you_wait_after_five_misses_and_forgets_on_success()
    {
        var clock = new ManualClock();
        var throttle = new LoginThrottle(clock);
        for (var i = 0; i < 4; i++) throttle.Failed("phone");
        Assert.Null(throttle.WaitFor("phone"));
        throttle.Failed("phone");
        Assert.Equal(TimeSpan.FromMinutes(1), throttle.WaitFor("phone"));
        Assert.Null(throttle.WaitFor("laptop"));
        throttle.Failed("phone");
        Assert.Equal(TimeSpan.FromMinutes(2), throttle.WaitFor("phone"));
        clock.Now = clock.Now.AddMinutes(3);
        Assert.Null(throttle.WaitFor("phone"));
        throttle.Succeeded("phone");
        throttle.Failed("phone");
        Assert.Null(throttle.WaitFor("phone"));
    }

    [Fact]
    public void Chats_are_saved_listed_newest_first_and_deleted()
    {
        var dir = Directory.CreateTempSubdirectory("scimilir-parts");
        try
        {
            var store = new ChatStore(new AppPaths(dir.FullName));
            var older = store.Create("Older");
            var newer = store.Create("Newer");
            older.Updated = newer.Updated.AddMinutes(-5);
            older.Messages.Add(new ChatMessage { Role = "user", Content = "hi" });
            store.Save(older);

            Assert.Equal(["Newer", "Older"], store.List().Select(c => c.Title));
            Assert.Equal("hi", store.Get(older.Id)!.Messages.Single().Content);
            Assert.Null(store.Get("../settings"));
            Assert.True(store.Delete(newer.Id));
            Assert.False(store.Delete(newer.Id));
            Assert.Single(store.List());
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("  What is   the\ncapital of France? ", "What is the capital of France?")]
    [InlineData("", "New chat")]
    public void Chat_titles_come_from_the_first_message(string text, string title) =>
        Assert.Equal(title, ChatStore.TitleFrom(text));

    [Fact]
    public void Long_titles_are_shortened() =>
        Assert.Equal(58, ChatStore.TitleFrom(new string('a', 200)).Length);

    [Fact]
    public void Thinking_is_left_out_of_what_models_see_again()
    {
        var chat = new Chat
        {
            Messages =
            [
                new ChatMessage { Role = "user", Content = "Hi" },
                new ChatMessage { Role = "assistant", Content = "<think>They said hi.</think>\n\nHello!" },
                new ChatMessage { Role = "assistant", Content = "<think>cut off before answering" },
            ],
        };
        var turns = Conversation.BuildTurns(chat, " Be brief. ");
        Assert.Equal(
            [new Turn("system", "Be brief."), new Turn("user", "Hi"), new Turn("assistant", "Hello!")],
            turns);
        Assert.Equal(2, Conversation.BuildTurns(chat, "  ").Count);
    }

    [Theory]
    [InlineData("local:Qwen3-4B-Q4_K_M.gguf", "local", "Qwen3-4B-Q4_K_M.gguf")]
    [InlineData("openrouter:anthropic/claude-x:beta", "openrouter", "anthropic/claude-x:beta")]
    public void Model_ids_find_their_provider(string id, string prefix, string model)
    {
        var hub = new ProviderHub([new FakeProvider("local"), new FakeProvider("openrouter")]);
        var (provider, name) = hub.Resolve(id)!.Value;
        Assert.Equal(prefix, provider.Prefix);
        Assert.Equal(model, name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nocolon")]
    [InlineData("local:")]
    [InlineData("other:thing")]
    public void Unknown_model_ids_are_refused(string? id) =>
        Assert.Null(new ProviderHub([new FakeProvider("local")]).Resolve(id));

    sealed class FakeProvider(string prefix) : IChatProvider
    {
        public string Prefix => prefix;
        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ModelInfo>>([]);
        public IAsyncEnumerable<Piece> StreamAsync(string model, IReadOnlyList<Turn> turns, Settings settings, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    [Theory]
    [InlineData("model.gguf", true)]
    [InlineData("Qwen3-4B-Q4_K_M.GGUF", true)]
    [InlineData("../model.gguf", false)]
    [InlineData("..\\model.gguf", false)]
    [InlineData("C:model.gguf", false)]
    [InlineData("sub/model.gguf", false)]
    [InlineData("model.bin", false)]
    [InlineData(".gguf", false)]
    public void Only_plain_gguf_names_are_allowed(string name, bool ok) => Assert.Equal(ok, ModelFiles.IsSafeName(name));

    [Theory]
    [InlineData("big-00001-of-00003.gguf", true)]
    [InlineData("big-00002-of-00003.gguf", false)]
    [InlineData("mmproj-model-f16.gguf", false)]
    [InlineData("small.gguf", true)]
    public void Only_first_parts_and_non_image_files_are_chat_models(string name, bool ok) =>
        Assert.Equal(ok, ModelFiles.IsChatModel(name));

    [Fact]
    public void Display_names_drop_the_extension_and_part_numbers()
    {
        Assert.Equal("big", ModelFiles.DisplayName("big-00001-of-00003.gguf"));
        Assert.Equal("small", ModelFiles.DisplayName("small.gguf"));
    }

    [Fact]
    public void Hugging_Face_file_lists_join_split_parts_and_skip_other_files()
    {
        using var doc = JsonDocument.Parse("""
            [
              {"type":"file","path":"README.md","size":100},
              {"type":"file","path":"model-Q4_K_M.gguf","size":10,"lfs":{"size":2000}},
              {"type":"file","path":"mmproj-f16.gguf","size":10,"lfs":{"size":500}},
              {"type":"directory","path":"Q8_0"},
              {"type":"file","path":"Q8_0/model-Q8_0-00002-of-00002.gguf","size":10,"lfs":{"size":3000}},
              {"type":"file","path":"Q8_0/model-Q8_0-00001-of-00002.gguf","size":10,"lfs":{"size":4000}}
            ]
            """);
        var files = ModelDownloads.GroupFiles(doc.RootElement);
        Assert.Equal(2, files.Count);
        Assert.Equal("model-Q4_K_M.gguf", files[0].Name);
        Assert.Equal(2000, files[0].Size);
        Assert.Equal("model-Q8_0-00001-of-00002.gguf", files[1].Name);
        Assert.Equal(7000, files[1].Size);
        Assert.Equal(["Q8_0/model-Q8_0-00001-of-00002.gguf", "Q8_0/model-Q8_0-00002-of-00002.gguf"], files[1].Paths);
    }

    [Theory]
    [InlineData("bartowski/Qwen_Qwen3-4B-GGUF", true)]
    [InlineData("owner/name.with.dots", true)]
    [InlineData("noslash", false)]
    [InlineData("a/b/c", false)]
    [InlineData("../etc", false)]
    [InlineData("owner/..", false)]
    public void Hugging_Face_names_are_checked(string repo, bool ok) => Assert.Equal(ok, ModelDownloads.IsValidRepo(repo));

    [Fact]
    public void OpenRouter_stream_lines_become_text_and_thinking()
    {
        var (pieces, done) = OpenRouterProvider.ParseLine("""data: {"choices":[{"delta":{"reasoning":"Hmm.","content":"Hi"}}]}""");
        Assert.False(done);
        Assert.Equal([new Piece(PieceKind.Thinking, "Hmm."), new Piece(PieceKind.Text, "Hi")], pieces);

        Assert.Empty(OpenRouterProvider.ParseLine(": OPENROUTER PROCESSING").Pieces);
        Assert.Empty(OpenRouterProvider.ParseLine("").Pieces);
        Assert.Empty(OpenRouterProvider.ParseLine("""data: {"choices":[{"delta":{"content":null}}]}""").Pieces);
        Assert.True(OpenRouterProvider.ParseLine("data: [DONE]").Done);

        var error = Assert.Throws<UserFacingException>(() =>
            OpenRouterProvider.ParseLine("""data: {"error":{"message":"Provider is down","code":502}}"""));
        Assert.Contains("Provider is down", error.Message);
    }

    [Fact]
    public void OpenRouter_model_list_skips_picture_only_models_and_shows_prices()
    {
        using var doc = JsonDocument.Parse("""
            {"data":[
              {"id":"z/chat","name":"Zed Chat","pricing":{"prompt":"0.0000015","completion":"0.000006"},
               "architecture":{"output_modalities":["text"]}},
              {"id":"a/free","name":"Alpha Free","pricing":{"prompt":"0","completion":"0"}},
              {"id":"p/painter","name":"Painter","architecture":{"output_modalities":["image"]}}
            ]}
            """);
        var models = OpenRouterProvider.ParseModels(doc.RootElement);
        Assert.Equal(["openrouter:a/free", "openrouter:z/chat"], models.Select(m => m.Id));
        Assert.Equal("Free", models[0].Detail);
        Assert.Equal("$1.5 in · $6 out per million tokens", models[1].Detail);
    }

    [Fact]
    public void OpenRouter_errors_are_explained()
    {
        Assert.Contains("key", OpenRouterProvider.ErrorText(HttpStatusCode.Unauthorized, ""));
        Assert.Contains("credit", OpenRouterProvider.ErrorText(HttpStatusCode.PaymentRequired, "{}"));
        Assert.Equal("OpenRouter error (400): No such model",
            OpenRouterProvider.ErrorText(HttpStatusCode.BadRequest, """{"error":{"message":"No such model"}}"""));
    }
}
