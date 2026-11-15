using System.Net;
using FamilyCalendar.Services.Assistant;
using Microsoft.Extensions.Logging.Abstractions;

namespace FamilyCalendar.Tests;

public class ProviderTests
{
    private const string Key = "TEST-KEY-123";
    private static readonly Func<TimeSpan, CancellationToken, Task> NoDelay = (_, _) => Task.CompletedTask;

    /// <summary>Catena reale (Gemini x2 + Groq x2) sopra un handler HTTP finto che decide per URL e numero di chiamata.</summary>
    private static (ChatChain Chain, FakeHandler Handler) BuildChain(Func<HttpRequestMessage, int, HttpResponseMessage> respond, bool groq = true)
    {
        var handler = new FakeHandler(respond);
        var factory = new FakeHttpClientFactory(handler);
        var providers = new List<IChatProvider>
        {
            new GeminiChatProvider(factory, Key, "gem-1"),
            new GeminiChatProvider(factory, Key, "gem-2"),
        };
        if (groq)
        {
            providers.Add(new GroqChatProvider(factory, Key, "groq-120", NoDelay));
            providers.Add(new GroqChatProvider(factory, Key, "groq-20", NoDelay));
        }
        return (new ChatChain(providers, NullLogger<ChatChain>.Instance), handler);
    }

    private static bool IsGemini(HttpRequestMessage r, string model) => r.RequestUri!.AbsoluteUri.Contains($"models/{model}:generateContent");

    private static HttpResponseMessage Status(HttpStatusCode s) => FakeHandler.Json(s, "{\"error\":{\"message\":\"boom\"}}");

    [Fact]
    public async Task FirstGeminiModel_Answers_IsPrimary()
    {
        var (chain, handler) = BuildChain((r, _) => FakeHandler.Json(HttpStatusCode.OK, Make.GeminiOk("Ciao [1]")));

        var res = await chain.GenerateAsync("sys", "usr", default);

        Assert.Equal("Ciao [1]", res.Text);
        Assert.Equal("gem-1", res.Model);
        Assert.True(res.IsPrimary);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Http429_FallsBackToNextGeminiModel()
    {
        var (chain, _) = BuildChain((r, _) => IsGemini(r, "gem-1") ? Status(HttpStatusCode.TooManyRequests) : FakeHandler.Json(HttpStatusCode.OK, Make.GeminiOk("ok")));

        var res = await chain.GenerateAsync("s", "u", default);

        Assert.Equal("gem-2", res.Model);
        Assert.False(res.IsPrimary);
    }

    [Fact]
    public async Task HttpRequestException_FallsBackToNextProvider()
    {
        var (chain, _) = BuildChain((r, _) => IsGemini(r, "gem-1")
            ? throw new HttpRequestException("DNS failure")
            : FakeHandler.Json(HttpStatusCode.OK, Make.GeminiOk("ok")));

        var res = await chain.GenerateAsync("s", "u", default);

        Assert.Equal("gem-2", res.Model);
    }

    [Fact]
    public async Task ClientTimeout_FallsBackToNextProvider()
    {
        // HttpClient.Timeout si manifesta come TaskCanceledException senza che il chiamante abbia annullato.
        var (chain, _) = BuildChain((r, _) => IsGemini(r, "gem-1")
            ? throw new TaskCanceledException("timeout", new TimeoutException())
            : FakeHandler.Json(HttpStatusCode.OK, Make.GeminiOk("ok")));

        var res = await chain.GenerateAsync("s", "u", default);

        Assert.Equal("gem-2", res.Model);
    }

    [Theory]
    [InlineData("<html>non e' json</html>")]
    [InlineData("{\"candidates\":\"stringa\"}")]
    [InlineData("{\"candidates\":[]}")]
    [InlineData("{\"candidates\":[{\"finishReason\":\"SAFETY\"}]}")]
    [InlineData("{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"tagliata\"}]},\"finishReason\":\"MAX_TOKENS\"}]}")]
    [InlineData("{\"promptFeedback\":{\"blockReason\":\"PROHIBITED_CONTENT\"}}")]
    [InlineData("")]
    public async Task MalformedOrUnusableGeminiPayload_FallsBackToNextModel(string badBody)
    {
        var (chain, _) = BuildChain((r, _) => IsGemini(r, "gem-1")
            ? FakeHandler.Json(HttpStatusCode.OK, badBody)
            : FakeHandler.Json(HttpStatusCode.OK, Make.GeminiOk("ok")));

        var res = await chain.GenerateAsync("s", "u", default);

        Assert.Equal("gem-2", res.Model);
    }

    [Fact]
    public async Task GeminiThoughtParts_AreExcluded()
    {
        const string body = "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"ragionamento interno\",\"thought\":true},{\"text\":\"Risposta \"},{\"text\":\"finale\"}]},\"finishReason\":\"STOP\"}]}";
        var (chain, _) = BuildChain((r, _) => FakeHandler.Json(HttpStatusCode.OK, body));

        var res = await chain.GenerateAsync("s", "u", default);

        Assert.Equal("Risposta finale", res.Text);
    }

    [Fact]
    public async Task AllGeminiFail_FallsBackToGroq_120b_thenToGroq_20b()
    {
        var groqCalls = 0;
        var (chain, handler) = BuildChain((r, _) =>
        {
            if (r.RequestUri!.Host != "api.groq.com") return Status(HttpStatusCode.ServiceUnavailable);
            // il 120b e' saturo (2 tentativi), il 20b risponde
            return ++groqCalls <= 2 ? Status(HttpStatusCode.TooManyRequests) : FakeHandler.Json(HttpStatusCode.OK, Make.GroqOk("da groq"));
        });

        var res = await chain.GenerateAsync("s", "u", default);

        Assert.Equal("groq-20", res.Model);
        Assert.False(res.IsPrimary);
        Assert.Equal("da groq", res.Text);
        Assert.Contains(handler.Bodies, b => b.Contains("\"model\":\"groq-120\""));
        Assert.Contains(handler.Bodies, b => b.Contains("\"model\":\"groq-20\"") && b.Contains("\"reasoning_effort\":\"low\"") && b.Contains("\"max_completion_tokens\""));
    }

    [Fact]
    public async Task GroqRetries429Once_BeforeMovingOn()
    {
        var calls = 0;
        var (chain, handler) = BuildChain((r, _) =>
        {
            if (r.RequestUri!.Host != "api.groq.com") return Status(HttpStatusCode.TooManyRequests);
            return ++calls == 1 ? Status(HttpStatusCode.TooManyRequests) : FakeHandler.Json(HttpStatusCode.OK, Make.GroqOk("ok"));
        });

        var res = await chain.GenerateAsync("s", "u", default);

        Assert.Equal("groq-120", res.Model); // stesso modello, secondo tentativo
        Assert.Equal(2, handler.Requests.Count(r => r.RequestUri!.Host == "api.groq.com"));
    }

    [Fact]
    public async Task AllProvidersFail_NonQuotaErrors_Gives502()
    {
        var (chain, _) = BuildChain((r, _) => r.RequestUri!.Host == "api.groq.com"
            ? FakeHandler.Json(HttpStatusCode.OK, "non json")
            : throw new HttpRequestException("rete giu"));

        var ex = await Assert.ThrowsAsync<AssistantException>(() => chain.GenerateAsync("s", "u", default));

        Assert.Equal(502, ex.StatusCode);
    }

    [Fact]
    public async Task AllProvidersFail_MixedErrors_Gives502()
    {
        var (chain, _) = BuildChain((r, i) => i % 2 == 0 ? Status(HttpStatusCode.TooManyRequests) : Status(HttpStatusCode.InternalServerError));

        var ex = await Assert.ThrowsAsync<AssistantException>(() => chain.GenerateAsync("s", "u", default));

        Assert.Equal(502, ex.StatusCode);
    }

    [Fact]
    public async Task AllProvidersFail_OnlyQuota_Gives503()
    {
        var (chain, _) = BuildChain((_, _) => Status(HttpStatusCode.TooManyRequests));

        var ex = await Assert.ThrowsAsync<AssistantException>(() => chain.GenerateAsync("s", "u", default));

        Assert.Equal(503, ex.StatusCode);
        Assert.Contains("saturi", ex.Message);
    }

    [Fact]
    public async Task CallerCancellation_StopsTheChain_WithoutFallback()
    {
        var (chain, handler) = BuildChain((_, _) => FakeHandler.Json(HttpStatusCode.OK, Make.GeminiOk("ok")));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => chain.GenerateAsync("s", "u", cts.Token));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task NoProviders_Gives503()
    {
        var chain = new ChatChain(Array.Empty<IChatProvider>(), NullLogger<ChatChain>.Instance);

        var ex = await Assert.ThrowsAsync<AssistantException>(() => chain.GenerateAsync("s", "u", default));

        Assert.Equal(503, ex.StatusCode);
    }

    [Fact]
    public async Task GeminiRequest_PutsKeyInHeaderOnly_AndSendsSystemInstructionAndTemperature()
    {
        var (chain, handler) = BuildChain((_, _) => FakeHandler.Json(HttpStatusCode.OK, Make.GeminiOk("ok")));

        await chain.GenerateAsync("SISTEMA", "UTENTE", default);

        var req = handler.Requests[0];
        Assert.DoesNotContain(Key, req.RequestUri!.ToString());
        Assert.Equal(Key, req.Headers.GetValues("x-goog-api-key").Single());
        var body = handler.Bodies[0];
        Assert.Contains("\"systemInstruction\"", body);
        Assert.Contains("SISTEMA", body);
        Assert.Contains("\"temperature\":0.2", body);
        Assert.DoesNotContain(Key, body);
    }

    [Fact]
    public async Task GroqRequest_UsesBearerAuth_AndOpenAiEndpoint()
    {
        var (chain, handler) = BuildChain((r, _) => r.RequestUri!.Host == "api.groq.com"
            ? FakeHandler.Json(HttpStatusCode.OK, Make.GroqOk("ok")) : Status(HttpStatusCode.TooManyRequests));

        await chain.GenerateAsync("s", "u", default);

        var groq = handler.Requests.First(r => r.RequestUri!.Host == "api.groq.com");
        Assert.Equal("https://api.groq.com/openai/v1/chat/completions", groq.RequestUri!.ToString());
        Assert.Equal("Bearer", groq.Headers.Authorization!.Scheme);
        Assert.DoesNotContain(Key, groq.RequestUri.ToString());
    }

    [Fact]
    public async Task GroqTruncatedOrEmpty_IsAnError_NotAnAnswer()
    {
        var (chain, _) = BuildChain((r, _) => r.RequestUri!.Host == "api.groq.com"
            ? FakeHandler.Json(HttpStatusCode.OK, "{\"choices\":[{\"message\":{\"content\":\"tagliata\"},\"finish_reason\":\"length\"}]}")
            : Status(HttpStatusCode.TooManyRequests));

        var ex = await Assert.ThrowsAsync<AssistantException>(() => chain.GenerateAsync("s", "u", default));

        Assert.Equal(502, ex.StatusCode);
    }

    // ---- Embedding ----------------------------------------------------------------------------------------

    private static string EmbeddingJson(int count, int dims = GeminiEmbeddingClient.Dimensions, float scale = 3f)
    {
        string Vec() => "{\"values\":[" + string.Join(",", Enumerable.Range(0, dims).Select(i => (i == 0 ? scale : 0f).ToString(System.Globalization.CultureInfo.InvariantCulture))) + "]}";
        return count < 0 ? "{\"embedding\":" + Vec() + "}" : "{\"embeddings\":[" + string.Join(",", Enumerable.Range(0, count).Select(_ => Vec())) + "]}";
    }

    private static GeminiEmbeddingClient Embedder(FakeHandler handler, List<TimeSpan>? delays = null) =>
        new(new FakeHttpClientFactory(handler), new AssistantKeys(Key, null), (t, _) => { delays?.Add(t); return Task.CompletedTask; });

    [Fact]
    public async Task EmbedQuery_UsesQueryPrefix_Header_Dimensions_AndNormalizes()
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json(HttpStatusCode.OK, EmbeddingJson(-1)));

        var v = await Embedder(handler).EmbedQueryAsync("prossimi eventi?", default);

        Assert.Equal(768, v.Length);
        Assert.Equal(1f, v[0], 5); // (3,0,...) normalizzato
        var req = handler.Requests[0];
        Assert.EndsWith("models/gemini-embedding-2:embedContent", req.RequestUri!.AbsolutePath);
        Assert.Equal("", req.RequestUri.Query);
        Assert.Equal(Key, req.Headers.GetValues("x-goog-api-key").Single());
        Assert.Contains("task: search result | query: prossimi eventi?", handler.Bodies[0]);
        Assert.Contains("\"output_dimensionality\":768", handler.Bodies[0]);
    }

    [Fact]
    public async Task EmbedDocuments_UsesBatchEndpoint_AndDocumentPrefix()
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json(HttpStatusCode.OK, EmbeddingJson(2)));

        var vs = await Embedder(handler).EmbedDocumentsAsync(new[] { "uno", "due" }, default);

        Assert.Equal(2, vs.Length);
        Assert.EndsWith(":batchEmbedContents", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Contains("title: none | text: uno", handler.Bodies[0]);
        Assert.Contains("title: none | text: due", handler.Bodies[0]);
        Assert.Contains("\"model\":\"models/gemini-embedding-2\"", handler.Bodies[0]);
    }

    [Fact]
    public async Task Embed_RetriesOn429WithRetryAfter_ThenSucceeds()
    {
        var delays = new List<TimeSpan>();
        var handler = new FakeHandler((_, i) =>
        {
            if (i > 0) return FakeHandler.Json(HttpStatusCode.OK, EmbeddingJson(1));
            var r = Status(HttpStatusCode.TooManyRequests);
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
            return r;
        });

        var vs = await Embedder(handler, delays).EmbedDocumentsAsync(new[] { "uno" }, default);

        Assert.Single(vs);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(TimeSpan.FromSeconds(7), Assert.Single(delays));
    }

    [Fact]
    public async Task Embed_PersistentQuota_ThrowsQuotaException503()
    {
        var handler = new FakeHandler((_, _) => Status(HttpStatusCode.TooManyRequests));

        var ex = await Assert.ThrowsAsync<AssistantException>(() => Embedder(handler).EmbedDocumentsAsync(new[] { "uno" }, default));

        Assert.True(ex.IsQuota);
        Assert.Equal(503, ex.StatusCode);
        Assert.Equal(4, handler.Requests.Count); // maxAttempts per i documenti
    }

    [Theory]
    [InlineData("rete")]
    [InlineData("json")]
    [InlineData("dimensioni")]
    [InlineData("cardinalita")]
    public async Task Embed_ProviderProblems_AreAssistantExceptions_NotRawExceptions(string kind)
    {
        var handler = new FakeHandler((_, _) => kind switch
        {
            "rete" => throw new HttpRequestException("DNS"),
            "json" => FakeHandler.Json(HttpStatusCode.OK, "{non json"),
            "dimensioni" => FakeHandler.Json(HttpStatusCode.OK, EmbeddingJson(1, dims: 10)),
            _ => FakeHandler.Json(HttpStatusCode.OK, EmbeddingJson(3)),
        });

        var ex = await Assert.ThrowsAsync<AssistantException>(() => Embedder(handler).EmbedDocumentsAsync(new[] { "uno" }, default));

        Assert.Equal(502, ex.StatusCode);
        Assert.DoesNotContain(Key, ex.Message + ex.Detail);
    }

    [Fact]
    public void Keys_FromConfig_AreTrimmedAndBlankMeansMissing()
    {
        var keys = new AssistantKeys("  abc  ", "   ");

        Assert.Equal("abc", keys.Gemini);
        Assert.Null(keys.Groq);
        Assert.False(new GeminiEmbeddingClient(new FakeHttpClientFactory(new FakeHandler((_, _) => throw new InvalidOperationException())),
            new AssistantKeys(null, "g")).IsConfigured);
    }

    [Fact]
    public async Task AllProvidersTimeOut_Gives503TooSlowMessage_WithIsTimeout()
    {
        var (chain, _) = BuildChain((_, _) => throw new TaskCanceledException("timeout", new TimeoutException()));

        var ex = await Assert.ThrowsAsync<AssistantException>(() => chain.GenerateAsync("s", "u", default));

        Assert.Equal(503, ex.StatusCode);
        Assert.True(ex.IsTimeout);
        Assert.Equal("L'assistente sta impiegando troppo tempo a rispondere. Riprova tra poco.", ex.Message);
    }

    [Fact]
    public async Task MixOfQuotaAndTimeouts_Gives503SaturatedMessage_NotTheTimeoutOne()
    {
        var (chain, _) = BuildChain((r, _) => r.RequestUri!.Host == "api.groq.com"
            ? throw new TaskCanceledException("timeout", new TimeoutException())
            : Status(HttpStatusCode.TooManyRequests));

        var ex = await Assert.ThrowsAsync<AssistantException>(() => chain.GenerateAsync("s", "u", default));

        Assert.Equal(503, ex.StatusCode);
        Assert.False(ex.IsTimeout);
        Assert.Contains("saturi", ex.Message);
    }

    [Fact]
    public async Task OnlyTheNotConfiguredMessageContainsTheWordConfigurato()
    {
        var (chain, _) = BuildChain((_, _) => Status(HttpStatusCode.TooManyRequests));
        var messages = new List<string>
        {
            (await Assert.ThrowsAsync<AssistantException>(() => chain.GenerateAsync("s", "u", default))).Message,
            (await Assert.ThrowsAsync<AssistantException>(() => new ChatChain(Array.Empty<IChatProvider>(), NullLogger<ChatChain>.Instance)
                .GenerateAsync("s", "u", default))).Message,
            ProviderHttp.QuotaMessage, ProviderHttp.ErrorMessage, ProviderHttp.TimeoutMessage, ProviderHttp.TooSlowMessage,
            AssistantService.DeadlineMessage, AssistantService.NoTasksAnswer,
        };

        Assert.All(messages, m => Assert.DoesNotContain("configurato", m));
        Assert.Contains("configurato", AssistantService.NotConfiguredMessage);
    }

    [Fact]
    public async Task EmbeddingQuery_UsesShortClient_AndBatchUsesTheLongOne()
    {
        var names = new List<string>();
        var handler = new FakeHandler((_, _) => FakeHandler.Json(HttpStatusCode.OK, EmbeddingJson(-1)));
        var embedder = new GeminiEmbeddingClient(new NamedFactory(handler, names), new AssistantKeys(Key, null), (_, _) => Task.CompletedTask);

        await embedder.EmbedQueryAsync("q", default);
        handler = new FakeHandler((_, _) => FakeHandler.Json(HttpStatusCode.OK, EmbeddingJson(1)));
        embedder = new GeminiEmbeddingClient(new NamedFactory(handler, names), new AssistantKeys(Key, null), (_, _) => Task.CompletedTask);
        await embedder.EmbedDocumentsAsync(new[] { "d" }, default);

        Assert.Equal(new[] { GeminiEmbeddingClient.HttpClientName, GeminiEmbeddingClient.BatchHttpClientName }, names);
    }

    [Fact]
    public async Task EmbeddingQueryTimeout_MapsToTooSlowMessage503_ButDocumentsKeepTheProviderTimeoutMessage()
    {
        var handler = new FakeHandler((_, _) => throw new TaskCanceledException("timeout", new TimeoutException()));
        var embedder = Embedder(handler);

        var q = await Assert.ThrowsAsync<AssistantException>(() => embedder.EmbedQueryAsync("domanda", default));
        Assert.Equal(503, q.StatusCode);
        Assert.True(q.IsTimeout);
        Assert.Equal("L'assistente sta impiegando troppo tempo a rispondere. Riprova tra poco.", q.Message);
        Assert.Equal(2, handler.Requests.Count); // 2 tentativi, poi si arrende

        var d = await Assert.ThrowsAsync<AssistantException>(() => embedder.EmbedDocumentsAsync(new[] { "x" }, default));
        Assert.Equal(ProviderHttp.TimeoutMessage, d.Message);
    }

    [Fact]
    public async Task EmbeddingQueryNetworkAndQuotaErrors_KeepTheirOwnMessages()
    {
        var net = Embedder(new FakeHandler((_, _) => throw new HttpRequestException("DNS")));
        var e1 = await Assert.ThrowsAsync<AssistantException>(() => net.EmbedQueryAsync("q", default));
        Assert.Equal(502, e1.StatusCode);
        Assert.Equal(ProviderHttp.ErrorMessage, e1.Message);

        var quota = Embedder(new FakeHandler((_, _) => Status(HttpStatusCode.TooManyRequests)));
        var e2 = await Assert.ThrowsAsync<AssistantException>(() => quota.EmbedQueryAsync("q", default));
        Assert.Equal(503, e2.StatusCode);
        Assert.Equal(ProviderHttp.QuotaMessage, e2.Message);
    }

    [Fact]
    public async Task ChainExceptionIsQuota_OnlyWhenEveryProviderAnswered429()
    {
        var (allQuota, _) = BuildChain((_, _) => Status(HttpStatusCode.TooManyRequests));
        Assert.True((await Assert.ThrowsAsync<AssistantException>(() => allQuota.GenerateAsync("s", "u", default))).IsQuota);

        var (mixed, _) = BuildChain((r, _) => r.RequestUri!.Host == "api.groq.com"
            ? throw new TaskCanceledException("timeout", new TimeoutException())
            : Status(HttpStatusCode.TooManyRequests));
        Assert.False((await Assert.ThrowsAsync<AssistantException>(() => mixed.GenerateAsync("s", "u", default))).IsQuota);

        var (errors, _) = BuildChain((_, _) => Status(HttpStatusCode.InternalServerError));
        var ex = await Assert.ThrowsAsync<AssistantException>(() => errors.GenerateAsync("s", "u", default));
        Assert.Equal(502, ex.StatusCode);
        Assert.False(ex.IsQuota);

        var (timeouts, _) = BuildChain((_, _) => throw new TaskCanceledException("timeout", new TimeoutException()));
        Assert.False((await Assert.ThrowsAsync<AssistantException>(() => timeouts.GenerateAsync("s", "u", default))).IsQuota);
    }

    private sealed class NamedFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        private readonly List<string> _names;
        public NamedFactory(HttpMessageHandler handler, List<string> names) { _handler = handler; _names = names; }
        public HttpClient CreateClient(string name) { _names.Add(name); return new HttpClient(_handler, disposeHandler: false); }
    }
}
