using FamilyCalendar.Domain;
using FamilyCalendar.Services;
using FamilyCalendar.Services.Assistant;
using Microsoft.Extensions.Configuration;

namespace FamilyCalendar.Tests;

public class AssistantLimiterTests
{
    private static readonly AuthService.Principal Mario = new("m.rossi", MemberRole.Membro, 2);
    private static readonly AuthService.Principal Anna = new("a.bianchi", MemberRole.Membro, 3);
    private static readonly AuthService.Principal Admin = new("g.lima", MemberRole.Amministratore, 1);

    private static (AssistantLimiter Limiter, FakeClock Clock) Create(int perHour = 3, int perDay = 100, int concurrent = 2)
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero));
        return (new AssistantLimiter(new AssistantOptions { PerUserPerHour = perHour, GlobalPerDay = perDay, MaxConcurrent = concurrent }, clock), clock);
    }

    [Fact]
    public void PerUserHourlyLimit_DeniesWithMessageAndRetryAfter()
    {
        var (limiter, clock) = Create(perHour: 3);
        for (var i = 0; i < 3; i++)
        {
            Assert.True(limiter.TryAcquire(Mario, out _, out _));
            clock.Advance(TimeSpan.FromMinutes(1));
        }

        var ok = limiter.TryAcquire(Mario, out _, out var denial);

        Assert.False(ok);
        Assert.Contains("3 domande all'ora", denial.Message);
        Assert.InRange(denial.RetryAfter.TotalMinutes, 56, 58); // la prima scade a 60 min dall'inizio, ne sono passati 3
    }

    [Fact]
    public void PerUserLimit_IsIndependentBetweenUsers()
    {
        var (limiter, _) = Create(perHour: 1);

        Assert.True(limiter.TryAcquire(Mario, out _, out _));
        Assert.False(limiter.TryAcquire(Mario, out _, out _));
        Assert.True(limiter.TryAcquire(Anna, out _, out _));
    }

    [Fact]
    public void HourlyWindow_SlidesWithTime()
    {
        var (limiter, clock) = Create(perHour: 1);
        Assert.True(limiter.TryAcquire(Mario, out _, out _));
        Assert.False(limiter.TryAcquire(Mario, out _, out _));

        clock.Advance(TimeSpan.FromMinutes(59));
        Assert.False(limiter.TryAcquire(Mario, out _, out _));

        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.True(limiter.TryAcquire(Mario, out _, out _));
    }

    [Fact]
    public void GlobalDailyLimit_AppliesToEveryoneAndExpiresAfterADay()
    {
        var (limiter, clock) = Create(perHour: 100, perDay: 2);
        Assert.True(limiter.TryAcquire(Mario, out _, out _));
        Assert.True(limiter.TryAcquire(Anna, out _, out _));

        Assert.False(limiter.TryAcquire(new AuthService.Principal("c.verdi", MemberRole.Membro, 4), out _, out var denial));
        Assert.Contains("giornaliero", denial.Message);

        clock.Advance(TimeSpan.FromHours(24) + TimeSpan.FromSeconds(1));
        Assert.True(limiter.TryAcquire(Mario, out _, out _));
    }

    [Fact]
    public void Refund_GivesTheTicketBack()
    {
        var (limiter, _) = Create(perHour: 1, perDay: 1);
        Assert.True(limiter.TryAcquire(Mario, out var ticket, out _));
        Assert.False(limiter.TryAcquire(Mario, out _, out _));

        limiter.Refund(ticket);

        Assert.True(limiter.TryAcquire(Mario, out _, out _));
    }

    [Fact]
    public void Admin_IsExemptFromUserAndDailyLimits()
    {
        var (limiter, _) = Create(perHour: 1, perDay: 1);

        for (var i = 0; i < 20; i++)
        {
            Assert.True(limiter.TryAcquire(Admin, out var ticket, out _));
            Assert.False(ticket.Counted);
        }

        // E non consuma la quota degli altri.
        Assert.True(limiter.TryAcquire(Mario, out _, out _));
    }

    [Fact]
    public void NullPrincipal_UsesAnonymousKeyAndIsLimited()
    {
        var (limiter, _) = Create(perHour: 2);

        Assert.True(limiter.TryAcquire(null, out var t, out _));
        Assert.Equal(AssistantLimiter.AnonymousKey, t.User);
        Assert.True(limiter.TryAcquire(null, out _, out _));
        Assert.False(limiter.TryAcquire(null, out _, out _));
    }

    [Fact]
    public async Task ConcurrencyLimit_BlocksUntilASlotIsReleased()
    {
        var (limiter, _) = Create(concurrent: 2);

        using var s1 = await limiter.WaitSlotAsync(TimeSpan.FromMilliseconds(50), default);
        var s2 = await limiter.WaitSlotAsync(TimeSpan.FromMilliseconds(50), default);
        Assert.NotNull(s1);
        Assert.NotNull(s2);

        Assert.Null(await limiter.WaitSlotAsync(TimeSpan.FromMilliseconds(50), default)); // pieno

        s2!.Dispose();
        s2.Dispose(); // doppio Dispose innocuo
        using var s3 = await limiter.WaitSlotAsync(TimeSpan.FromMilliseconds(50), default);
        Assert.NotNull(s3);
        Assert.Null(await limiter.WaitSlotAsync(TimeSpan.FromMilliseconds(50), default)); // il doppio Dispose non ha creato posti extra
    }

    [Fact]
    public void Options_ReadFromConfiguration_WithDefaults()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Assistant:PerUserPerHour"] = "7",
                ["Assistant:GeminiModels"] = "m-a, m-b",
            }).Build();

        var o = AssistantOptions.FromConfiguration(cfg);

        Assert.Equal(7, o.PerUserPerHour);
        Assert.Equal(80, o.GlobalPerDay);
        Assert.Equal(2, o.MaxConcurrent);
        Assert.Equal(new[] { "m-a", "m-b" }, o.GeminiModels);
        Assert.Equal(AssistantOptions.DefaultGeminiModels, AssistantOptions.FromConfiguration(
            new ConfigurationBuilder().Build()).GeminiModels);
    }

    [Fact]
    public void Defaults_AlignTimeoutBudget_ClientTimeoutExceedsSlotWaitPlusDeadline()
    {
        var o = new AssistantOptions();

        Assert.Equal(TimeSpan.FromSeconds(50), o.Deadline);
        Assert.Equal(TimeSpan.FromSeconds(5), o.SlotWait);
        Assert.Equal(200, o.BackfillMaxPerRun);
        Assert.Equal(24000, o.MaxContextChars);
        // Invariante: timeout del client frontend (60 s) > attesa slot + deadline, cosi' l'esito dei fallback arriva all'utente.
        Assert.True(AssistantOptions.ClientTimeout > o.SlotWait + o.Deadline);
    }

    [Fact]
    public void NewOptions_ReadFromConfiguration()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Assistant:DeadlineSeconds"] = "40",
                ["Assistant:SlotWaitSeconds"] = "3",
                ["Assistant:BackfillMaxPerRun"] = "75",
                ["Assistant:MaxContextChars"] = "12000",
            }).Build();

        var o = AssistantOptions.FromConfiguration(cfg);

        Assert.Equal(TimeSpan.FromSeconds(40), o.Deadline);
        Assert.Equal(TimeSpan.FromSeconds(3), o.SlotWait);
        Assert.Equal(75, o.BackfillMaxPerRun);
        Assert.Equal(12000, o.MaxContextChars);
    }

    [Theory]
    [InlineData("600", "5", 50)]   // troppo alto: 60 - 5 (slot) - 5 (margine)
    [InlineData("100", "20", 35)]  // slot piu' lungo: deadline ridotto di conseguenza
    [InlineData("40", "5", 40)]    // gia' entro l'invariante: invariato
    [InlineData("50", "5", 50)]    // default: invariato
    public void Deadline_IsCappedSoThatClientTimeoutExceedsSlotWaitPlusDeadline(string deadline, string slot, int expectedSeconds)
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Assistant:DeadlineSeconds"] = deadline, ["Assistant:SlotWaitSeconds"] = slot,
        }).Build();

        var o = AssistantOptions.FromConfiguration(cfg);

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), o.Deadline);
        Assert.True(AssistantOptions.ClientTimeout > o.SlotWait + o.Deadline);
        var configured = int.Parse(deadline);
        Assert.Equal(configured > expectedSeconds ? TimeSpan.FromSeconds(configured) : (TimeSpan?)null, o.DeadlineReducedFrom);
    }
}
