using System.Text.Json;
using FamilyCalendar.Controllers;
using FamilyCalendar.Domain;
using FamilyCalendar.Filters;
using FamilyCalendar.Services;
using FamilyCalendar.Services.Assistant;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace FamilyCalendar.Tests;

public class AssistantControllerTests
{
    private static readonly AuthService.Principal Member = new("m.rossi", MemberRole.Membro, 2);

    private sealed record Ctx(AssistantController Controller, FakeEmbeddingClient Embed, FakeTaskSource Tasks, FakeChain Chain, AssistantLimiter Limiter);

    private static Ctx Create(AssistantOptions? options = null, AuthService.Principal? principal = null)
    {
        var embed = new FakeEmbeddingClient();
        var tasks = new FakeTaskSource();
        var chain = new FakeChain();
        var clock = new FakeClock(new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero));
        var opts = options ?? new AssistantOptions();
        var service = Make.Service(embed, new FakeVectorStore(), tasks, chain, clock, opts);
        var limiter = new AssistantLimiter(opts, clock);
        var http = new DefaultHttpContext();
        if (principal is not null) http.Items[TokenAuthAttribute.PrincipalKey] = principal;
        var controller = new AssistantController(service, limiter, opts) { ControllerContext = new ControllerContext { HttpContext = http } };
        return new Ctx(controller, embed, tasks, chain, limiter);
    }

    private static (int Status, string Message) ErrorOf(IActionResult result)
    {
        var obj = Assert.IsType<ObjectResult>(result);
        var json = JsonSerializer.Serialize(obj.Value);
        using var doc = JsonDocument.Parse(json);
        return (obj.StatusCode!.Value, doc.RootElement.GetProperty("message").GetString()!);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Ask_EmptyQuestionOrBody_Is400Json(string? q)
    {
        var c = Create(principal: Member);

        var (status, message) = ErrorOf(await c.Controller.Ask(q is null ? null : new AskRequest(q), default));

        Assert.Equal(400, status);
        Assert.Equal("Scrivi una domanda.", message);
    }

    [Fact]
    public async Task Ask_TooLong_Is400Json()
    {
        var c = Create(principal: Member);

        var (status, message) = ErrorOf(await c.Controller.Ask(new AskRequest(new string('x', 501)), default));

        Assert.Equal(400, status);
        Assert.Equal("La domanda è troppo lunga (massimo 500 caratteri).", message);
    }

    [Fact]
    public async Task Ask_NotConfigured_Is503Json_WithoutRetryAfter()
    {
        var c = Create(principal: Member);
        c.Embed.IsConfigured = false;

        var (status, message) = ErrorOf(await c.Controller.Ask(new AskRequest("ciao"), default));

        Assert.Equal(503, status);
        Assert.Equal("L'assistente non è configurato (manca la chiave Gemini).", message);
        Assert.False(c.Controller.Response.Headers.ContainsKey("Retry-After"));
    }

    [Fact]
    public async Task Ask_Success_Is200WithDto()
    {
        var c = Create(principal: Member);
        c.Tasks.Tasks.Add(Make.Task(1, "Gita", "2026-11-16", "09:00"));
        c.Chain.Result = new ChatResult("Domani c'e' la gita [1]", "m1", true);

        var ok = Assert.IsType<OkObjectResult>(await c.Controller.Ask(new AskRequest("domani?"), default));

        var dto = Assert.IsType<AssistantAnswerDto>(ok.Value);
        Assert.Equal("m1", dto.Model);
        Assert.Equal(1, dto.Retrieved);
        Assert.Equal("Gita", Assert.Single(dto.Sources).Title);
    }

    [Fact]
    public async Task Ask_RateLimited_Is429WithRetryAfter()
    {
        var c = Create(new AssistantOptions { PerUserPerHour = 1 }, Member);
        c.Tasks.Tasks.Add(Make.Task(1, "Gita", "2026-11-16"));

        Assert.IsType<OkObjectResult>(await c.Controller.Ask(new AskRequest("uno"), default));
        var (status, message) = ErrorOf(await c.Controller.Ask(new AskRequest("due"), default));

        Assert.Equal(429, status);
        Assert.Contains("1 domande all'ora", message);
        Assert.True(int.Parse(c.Controller.Response.Headers.RetryAfter.ToString()) > 0);
    }

    [Theory]
    [InlineData(502)]   // risposta troncata/vuota/bloccata, errore del modello
    [InlineData(503)]   // quota o timeout dei modelli di risposta
    public async Task Ask_WhenGenerationFails_TicketStaysConsumed(int status)
    {
        var c = Create(new AssistantOptions { PerUserPerHour = 1 }, Member);
        c.Tasks.Tasks.Add(Make.Task(1, "Gita", "2026-11-16"));
        c.Chain.Throw = new AssistantException(status, "giu");

        Assert.Equal(status, ErrorOf(await c.Controller.Ask(new AskRequest("uno"), default)).Status);
        c.Chain.Throw = null;

        // Il biglietto non e' stato rimborsato: chi ripete domande che fanno fallire la generazione consuma quota.
        Assert.Equal(429, ErrorOf(await c.Controller.Ask(new AskRequest("uno"), default)).Status);
    }

    [Fact]
    public async Task Ask_WhenEveryModelAnswered429_TicketIsRefunded_ButTimeoutsAreNot()
    {
        var c = Create(new AssistantOptions { PerUserPerHour = 1 }, Member);
        c.Tasks.Tasks.Add(Make.Task(1, "Gita", "2026-11-16"));

        c.Chain.Throw = new AssistantException(503, "saturi", isQuota: true); // tutti i modelli: 429
        Assert.Equal(503, ErrorOf(await c.Controller.Ask(new AskRequest("uno"), default)).Status);
        Assert.Equal("30", c.Controller.Response.Headers.RetryAfter.ToString());

        c.Chain.Throw = new AssistantException(503, "lento", isTimeout: true); // timeout: consumato
        Assert.Equal(503, ErrorOf(await c.Controller.Ask(new AskRequest("uno"), default)).Status);
        c.Chain.Throw = null;
        Assert.Equal(429, ErrorOf(await c.Controller.Ask(new AskRequest("uno"), default)).Status);
    }

    [Fact]
    public async Task Ask_WhenEmbeddingFails_TicketIsRefunded()
    {
        var c = Create(new AssistantOptions { PerUserPerHour = 1 }, Member);
        c.Tasks.Tasks.Add(Make.Task(1, "Gita", "2026-11-16"));
        c.Embed.QueryFailure = new AssistantException(503, "quota", isQuota: true);

        Assert.Equal(503, ErrorOf(await c.Controller.Ask(new AskRequest("uno"), default)).Status);
        c.Embed.QueryFailure = new AssistantException(502, "rete");
        Assert.Equal(502, ErrorOf(await c.Controller.Ask(new AskRequest("uno"), default)).Status);
        c.Embed.QueryFailure = null;

        Assert.IsType<OkObjectResult>(await c.Controller.Ask(new AskRequest("uno"), default)); // nessun 429: mai consumato
        Assert.Equal(1, c.Chain.Calls);
    }

    [Fact]
    public async Task Ask_WhenCancelledBeforeEmbedding_TicketIsRefunded()
    {
        var c = Create(new AssistantOptions { PerUserPerHour = 1 }, Member);
        c.Tasks.Tasks.Add(Make.Task(1, "Gita", "2026-11-16"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => c.Controller.Ask(new AskRequest("uno"), cts.Token));
        Assert.Empty(c.Embed.Queries);

        Assert.IsType<OkObjectResult>(await c.Controller.Ask(new AskRequest("uno"), default));
    }

    [Fact]
    public async Task Ask_WhenNoSlotAvailable_Is503WithShortRetryAfter_AndTicketIsRefunded()
    {
        var c = Create(new AssistantOptions { PerUserPerHour = 1, MaxConcurrent = 1, SlotWait = TimeSpan.FromMilliseconds(30) }, Member);
        c.Tasks.Tasks.Add(Make.Task(1, "Gita", "2026-11-16"));

        var held = await c.Limiter.WaitSlotAsync(TimeSpan.FromSeconds(1), default);
        var (status, message) = ErrorOf(await c.Controller.Ask(new AskRequest("uno"), default));
        held!.Dispose();

        Assert.Equal(503, status);
        Assert.Contains("occupato", message);
        Assert.Equal("5", c.Controller.Response.Headers.RetryAfter.ToString());
        Assert.IsType<OkObjectResult>(await c.Controller.Ask(new AskRequest("uno"), default)); // biglietto rimborsato
    }

    [Fact]
    public async Task Ask_ProviderSaturated_Is503WithRetryAfter()
    {
        var c = Create(principal: Member);
        c.Tasks.Tasks.Add(Make.Task(1, "Gita", "2026-11-16"));
        c.Chain.Throw = new AssistantException(503, "saturi");

        var (status, message) = ErrorOf(await c.Controller.Ask(new AskRequest("uno"), default));

        Assert.Equal(503, status);
        Assert.Equal("saturi", message);
        Assert.Equal("30", c.Controller.Response.Headers.RetryAfter.ToString());
    }

    [Fact]
    public async Task Ask_Admin_IsNotSubjectToPerUserLimit()
    {
        var c = Create(new AssistantOptions { PerUserPerHour = 1, GlobalPerDay = 1 },
            new AuthService.Principal("g.lima", MemberRole.Amministratore, 1));
        c.Tasks.Tasks.Add(Make.Task(1, "Gita", "2026-11-16"));

        for (var i = 0; i < 4; i++)
            Assert.IsType<OkObjectResult>(await c.Controller.Ask(new AskRequest("uno"), default));
    }
}
