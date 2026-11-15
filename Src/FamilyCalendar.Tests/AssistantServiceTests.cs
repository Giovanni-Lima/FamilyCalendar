using FamilyCalendar.Domain;
using FamilyCalendar.Services;
using FamilyCalendar.Services.Assistant;

namespace FamilyCalendar.Tests;

public class AssistantServiceTests
{
    private static (AssistantService Svc, FakeEmbeddingClient Embed, FakeVectorStore Store, FakeTaskSource Tasks, FakeChain Chain) Setup(FakeClock? clock = null)
    {
        var embed = new FakeEmbeddingClient();
        var store = new FakeVectorStore();
        var tasks = new FakeTaskSource();
        var chain = new FakeChain();
        return (Make.Service(embed, store, tasks, chain, clock), embed, store, tasks, chain);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EmptyQuestion_Is400(string? q)
    {
        var (svc, _, _, _, chain) = Setup();

        var ex = await Assert.ThrowsAsync<AssistantException>(() => svc.AskAsync(q, null, default));

        Assert.Equal(400, ex.StatusCode);
        Assert.Equal("Scrivi una domanda.", ex.Message);
        Assert.Equal(0, chain.Calls);
    }

    [Fact]
    public async Task TooLongQuestion_Is400_ButExactlyMaxIsAccepted()
    {
        var (svc, _, _, tasks, _) = Setup();
        tasks.Tasks.Add(Make.Task(1, "Gita", "2026-11-20"));

        var ex = await Assert.ThrowsAsync<AssistantException>(() => svc.AskAsync(new string('a', 501), null, default));
        Assert.Equal(400, ex.StatusCode);
        Assert.Contains("500", ex.Message);

        var ok = await svc.AskAsync(new string('a', 500), null, default);
        Assert.NotNull(ok);
    }

    [Fact]
    public async Task NotConfigured_Is503_WithoutCallingAnything()
    {
        var (svc, embed, store, _, chain) = Setup();
        embed.IsConfigured = false;

        var ex = await Assert.ThrowsAsync<AssistantException>(() => svc.AskAsync("prossimi eventi?", null, default));

        Assert.False(svc.IsConfigured);
        Assert.Equal(503, ex.StatusCode);
        Assert.Equal("L'assistente non è configurato (manca la chiave Gemini).", ex.Message);
        Assert.Empty(embed.Queries);
        Assert.Equal(0, store.SearchCalls);
        Assert.Equal(0, chain.Calls);
    }

    [Fact]
    public async Task NoTasks_ReturnsFixedAnswer_WithoutCallingModels()
    {
        var (svc, embed, _, _, chain) = Setup();

        var answer = await svc.AskAsync("prossimi eventi?", null, default);

        Assert.Equal(AssistantService.NoTasksAnswer, answer.Answer);
        Assert.Empty(answer.Sources);
        Assert.Equal(0, answer.Retrieved);
        Assert.False(answer.ReducedModel);
        Assert.Equal(0, chain.Calls);
        Assert.Single(embed.Queries); // la domanda viene comunque embeddata per la ricerca
    }

    [Fact]
    public async Task HybridContext_MergesSemanticAndUpcoming_Deduplicated_AndOrderedByDate()
    {
        var (svc, _, store, tasks, chain) = Setup();
        var past = Make.Task(1, "Cena di agosto", "2026-08-10", "20:00", done: true);   // solo semantico (passata e fatta)
        var shared = Make.Task(2, "Dentista", "2026-11-18", "10:00");                    // sia semantico sia prossima
        var upcomingOnly = Make.Task(3, "Gita", "2026-11-17");                           // solo prossime, senza ora
        var farFuture = Make.Task(4, "Vacanze", "2027-03-01");                           // oltre i 60 giorni: fuori dalle prossime
        var doneUpcoming = Make.Task(5, "Fatta", "2026-11-16", done: true);              // completata: non e' "prossima"
        tasks.Tasks.AddRange(new[] { past, shared, upcomingOnly, farFuture, doneUpcoming });
        store.Hits = new[] { new VectorHit(past, 0.9), new VectorHit(shared, 0.8) };
        chain.Result = new ChatResult("Il prossimo e' [2] e poi [3].", "m1", true);

        var answer = await svc.AskAsync("  prossimi eventi?  ", null, default);

        // Ordine per data: past(1) < upcomingOnly(2) < shared(3)
        Assert.Equal(3, answer.Retrieved);
        Assert.Equal(new[] { 2, 3 }, answer.Sources.Select(s => s.Ref));
        Assert.Equal(new[] { 3, 2 }, answer.Sources.Select(s => s.Id));
        Assert.Equal(30, store.LastTopK);
        Assert.Equal("test-embed", store.LastModel); // la ricerca e' filtrata per il modello degli embedding correnti
        Assert.Equal(new DateOnly(2026, 11, 15), tasks.LastUpcomingQuery!.Value.From);
        Assert.Equal(new DateOnly(2027, 1, 14), tasks.LastUpcomingQuery.Value.To); // +60 giorni
        Assert.Equal(30, tasks.LastUpcomingQuery.Value.Max);
        Assert.Contains("[1] 2026-08-10", chain.LastUser);
        Assert.Contains("[2] 2026-11-17", chain.LastUser);
        Assert.Contains("[3] 2026-11-18", chain.LastUser);
        Assert.DoesNotContain("Vacanze", chain.LastUser);
        Assert.DoesNotContain("Fatta", chain.LastUser);
        Assert.Contains("Domanda: prossimi eventi?", chain.LastUser);
        Assert.False(answer.ReducedModel);
        Assert.Equal("m1", answer.Model);
    }

    [Fact]
    public async Task Context_UsesOnlyCitedSources_AndIgnoresInvalidRefs()
    {
        var (svc, _, _, tasks, chain) = Setup();
        tasks.Tasks.AddRange(new[] { Make.Task(1, "A", "2026-11-16"), Make.Task(2, "B", "2026-11-17") });
        chain.Result = new ChatResult("Vedi 【2】 e [7]", "m1", true);

        var answer = await svc.AskAsync("cosa c'e'?", null, default);

        Assert.Equal("Vedi [2] e [7]", answer.Answer); // normalizzata
        var s = Assert.Single(answer.Sources);
        Assert.Equal(2, s.Ref);
        Assert.Equal("B", s.Title);
    }

    [Fact]
    public async Task ReducedModel_IsTrue_WhenNotFromPrimaryProvider()
    {
        var (svc, _, _, tasks, chain) = Setup();
        tasks.Tasks.Add(Make.Task(1, "A", "2026-11-16"));
        chain.Result = new ChatResult("Ok [1]", "openai/gpt-oss-120b", false);

        var answer = await svc.AskAsync("cosa c'e'?", null, default);

        Assert.True(answer.ReducedModel);
        Assert.Equal("openai/gpt-oss-120b", answer.Model);
    }

    [Fact]
    public async Task Today_IsComputedInRomeTimeZone_NotUtc()
    {
        // 23:30 UTC del 15/11/2026 sono le 00:30 del 16/11 a Roma (CET, UTC+1).
        var clock = new FakeClock(new DateTimeOffset(2026, 11, 15, 23, 30, 0, TimeSpan.Zero));
        var (svc, _, _, tasks, chain) = Setup(clock);
        tasks.Tasks.Add(Make.Task(1, "A", "2026-11-16"));

        await svc.AskAsync("oggi?", null, default);

        Assert.Equal(new DateOnly(2026, 11, 16), tasks.LastUpcomingQuery!.Value.From);
        Assert.Contains("Oggi è lunedì 16 novembre 2026 (2026-11-16). Ora sono le 00:30", chain.LastUser);
        Assert.Equal(new DateOnly(2026, 11, 16), RomeTime.Today(clock));
    }

    [Fact]
    public async Task ChainFailure_PropagatesAssistantException()
    {
        var (svc, _, _, tasks, chain) = Setup();
        tasks.Tasks.Add(Make.Task(1, "A", "2026-11-16"));
        chain.Throw = new AssistantException(502, "giu");

        var ex = await Assert.ThrowsAsync<AssistantException>(() => svc.AskAsync("x", null, default));

        Assert.Equal(502, ex.StatusCode);
    }

    [Fact]
    public async Task CallerCancellation_IsNotConvertedToAssistantException()
    {
        var (svc, _, _, tasks, chain) = Setup();
        tasks.Tasks.Add(Make.Task(1, "A", "2026-11-16"));
        chain.Throw = new OperationCanceledException();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => svc.AskAsync("x", null, cts.Token));
    }

    [Fact]
    public async Task Deadline_Exceeded_Is503()
    {
        var embed = new FakeEmbeddingClient();
        var svc = Make.Service(embed, new FakeVectorStore(), new FakeTaskSource(), new BlockingChain(),
            options: new AssistantOptions { Deadline = TimeSpan.FromMilliseconds(50) });
        // BlockingChain e' usata solo se c'e' contesto: aggiungo un'attivita'.
        var tasks = new FakeTaskSource();
        tasks.Tasks.Add(Make.Task(1, "A", "2026-11-16"));
        svc = Make.Service(embed, new FakeVectorStore(), tasks, new BlockingChain(),
            options: new AssistantOptions { Deadline = TimeSpan.FromMilliseconds(50) });

        var ex = await Assert.ThrowsAsync<AssistantException>(() => svc.AskAsync("x", null, default));

        Assert.Equal(503, ex.StatusCode);
        Assert.Equal(AssistantService.DeadlineMessage, ex.Message);
    }

    private sealed class BlockingChain : IChatChain
    {
        public async Task<ChatResult> GenerateAsync(string system, string user, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return null!;
        }
    }

    [Fact]
    public void BuildContext_OrdersAllDayFirstWithinDay_AndNumbersFromOne()
    {
        var a = Make.Task(1, "Sera", "2026-11-20", "21:00");
        var b = Make.Task(2, "Mattina", "2026-11-20", "08:00");
        var c = Make.Task(3, "Tutto il giorno", "2026-11-20");
        var d = Make.Task(4, "Prima", "2026-11-19", "23:00");

        var ctx = AssistantService.BuildContext(new[] { new VectorHit(a, 0.5), new VectorHit(b, 0.4) }, new[] { c, d, a });

        Assert.Equal(new[] { 4, 3, 2, 1 }, ctx.Select(e => e.Task.Id));
        Assert.Equal(new[] { 1, 2, 3, 4 }, ctx.Select(e => e.Ref));
    }

    [Fact]
    public void BuildContext_OverBudget_DropsLowestScoringSemanticOnlyFirst_ThenFarthestUpcoming()
    {
        var sem1 = Make.Task(1, "semantica alta", "2026-11-20");
        var sem2 = Make.Task(2, "semantica bassa", "2026-11-21");
        var sem3 = Make.Task(3, "semantica media", "2026-11-22");
        var near = Make.Task(4, "prossima vicina", "2026-11-16");
        var far = Make.Task(5, "prossima lontana", "2027-01-10");
        var hits = new[] { new VectorHit(sem1, 0.9), new VectorHit(sem2, 0.1), new VectorHit(sem3, 0.5) };
        var upcoming = new[] { near, far };

        int Len(TaskItem t) => AssistantPrompt.FormatLine(new ContextEntry(99, t)).Length + 1;
        var all = new[] { sem1, sem2, sem3, near, far };

        // Budget per 4 voci: salta solo la semantica col punteggio piu' basso.
        var four = AssistantService.BuildContext(hits, upcoming, all.Sum(Len) - Len(sem2));
        Assert.Equal(new[] { 4, 1, 3, 5 }, four.Select(e => e.Task.Id)); // ordine cronologico, numerazione ricalcolata
        Assert.Equal(new[] { 1, 2, 3, 4 }, four.Select(e => e.Ref));

        // Budget per 2 voci: via tutte le solo-semantiche; resta la piu' vicina delle "prossime" e poi... entrambe le prossime
        // (2 voci), quindi la lontana non viene scartata finche' c'e' spazio.
        var two = AssistantService.BuildContext(hits, upcoming, Len(near) + Len(far));
        Assert.Equal(new[] { 4, 5 }, two.Select(e => e.Task.Id));

        // Budget per 1 voce: si scarta la prossima piu' lontana nel futuro.
        var one = AssistantService.BuildContext(hits, upcoming, Math.Max(Len(near), Len(far)));
        Assert.Equal(new[] { 4 }, one.Select(e => e.Task.Id));
    }

    [Fact]
    public async Task ContextBudget_FromOptions_LimitsPromptSize()
    {
        var embed = new FakeEmbeddingClient();
        var tasks = new FakeTaskSource();
        for (var i = 1; i <= 30; i++) tasks.Tasks.Add(Make.Task(i, $"Attivita numero {i}", "2026-11-20", note: new string('n', 250)));
        var chain = new FakeChain();
        var svc = Make.Service(embed, new FakeVectorStore(), tasks, chain, options: new AssistantOptions { MaxContextChars = 2000 });

        var answer = await svc.AskAsync("prossimi eventi?", null, default);

        Assert.InRange(answer.Retrieved, 1, 8);
        var block = chain.LastUser![chain.LastUser!.IndexOf("<attivita>", StringComparison.Ordinal)..];
        Assert.True(block.Length < 2000 + 40);
    }

    [Fact]
    public async Task Progress_GenerationStarted_OnlyAfterEmbeddingAndContext()
    {
        var (svc, embed, _, tasks, chain) = Setup();
        tasks.Tasks.Add(Make.Task(1, "A", "2026-11-16"));

        // embedding fallito: la generazione non parte
        embed.QueryFailure = new AssistantException(503, "quota", isQuota: true);
        var p1 = new AskProgress();
        await Assert.ThrowsAsync<AssistantException>(() => svc.AskAsync("x", null, default, p1));
        Assert.False(p1.GenerationStarted);

        // embedding ok ma la catena fallisce: la generazione e' partita
        embed.QueryFailure = null;
        chain.Throw = new AssistantException(502, "giu");
        var p2 = new AskProgress();
        await Assert.ThrowsAsync<AssistantException>(() => svc.AskAsync("x", null, default, p2));
        Assert.True(p2.GenerationStarted);
    }
}
