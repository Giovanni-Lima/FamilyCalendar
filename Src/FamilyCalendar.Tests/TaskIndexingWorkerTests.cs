using FamilyCalendar.Services.Assistant;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FamilyCalendar.Tests;

public class TaskIndexingWorkerTests
{
    private sealed class Rig
    {
        public FakeEmbeddingClient Embed { get; } = new();
        public FakeVectorStore Store { get; } = new();
        public FakeTaskSource Tasks { get; } = new();
        public FakeClock Clock { get; } = new(new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero));
        public TaskIndexScheduler Scheduler { get; }
        public TaskIndexingWorker Worker { get; }

        public Rig()
        {
            var services = new ServiceCollection();
            services.AddSingleton<IEmbeddingClient>(Embed);
            services.AddSingleton<ITaskVectorStore>(Store);
            services.AddSingleton<ITaskSource>(Tasks);
            services.AddSingleton<TimeProvider>(Clock);
            services.AddLogging();
            services.AddScoped<TaskIndexer>();
            var provider = services.BuildServiceProvider();

            Scheduler = new TaskIndexScheduler(Embed);
            Worker = new TaskIndexingWorker(provider.GetRequiredService<IServiceScopeFactory>(), Scheduler, Embed,
                new AssistantOptions(), Clock, NullLogger<TaskIndexingWorker>.Instance)
            { BatchPauseOverride = TimeSpan.Zero };
        }
    }

    [Fact]
    public async Task FirstStep_RunsBackfill()
    {
        var rig = new Rig();
        rig.Tasks.Tasks.Add(Make.Task(1, "Uno", "2026-11-20"));

        Assert.True(await rig.Worker.StepAsync(default));

        Assert.Equal(rig.Clock.GetUtcNow(), rig.Worker.LastBackfill);
        Assert.Single(rig.Store.Upserts);
    }

    [Fact]
    public async Task HourlyBackfill_RunsWhenIntervalElapsed_EvenIfTheQueueIsAlwaysBusy()
    {
        var rig = new Rig();
        rig.Tasks.Tasks.Add(Make.Task(1, "Uno", "2026-11-20"));
        await rig.Worker.StepAsync(default); // backfill all'avvio: indicizza la 1

        // Dopo 30 minuti: arriva un'attivita' (2) che NON viene accodata, e una modifica accodata alla 1.
        rig.Clock.Advance(TimeSpan.FromMinutes(30));
        rig.Tasks.Tasks.Add(Make.Task(2, "Due", "2026-11-21"));
        rig.Tasks.Tasks[0].Note = "cambiata";
        rig.Scheduler.Enqueue(1);
        await rig.Worker.StepAsync(default);

        Assert.Equal(new[] { 1, 1 }, rig.Store.Upserts.Select(u => u.Id)); // solo la coda: niente backfill (la 2 manca ancora)
        Assert.Equal(new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero), rig.Worker.LastBackfill);

        // Altri 31 minuti (61 dall'ultimo backfill) con la coda di nuovo piena: il backfill parte comunque, per primo.
        rig.Clock.Advance(TimeSpan.FromMinutes(31));
        rig.Tasks.Tasks[0].Note = "cambiata ancora";
        rig.Scheduler.Enqueue(1);
        await rig.Worker.StepAsync(default);

        Assert.Contains(2, rig.Store.Upserts.Select(u => u.Id));
        Assert.Equal(rig.Clock.GetUtcNow(), rig.Worker.LastBackfill);
    }

    [Fact]
    public async Task Quota_SuspendsIndexingForAnHour_ThenBackfillResumesRightAfterTheSuspension()
    {
        var rig = new Rig();
        var t0 = rig.Clock.GetUtcNow();
        rig.Tasks.Tasks.Add(Make.Task(1, "Uno", "2026-11-20"));
        // Il backfill dura 5 minuti (l'orologio avanza DENTRO la chiamata di embedding) e finisce con un 429.
        rig.Embed.OnDocuments = i => { if (i == 0) rig.Clock.Advance(TimeSpan.FromMinutes(5)); };
        rig.Embed.FailBatch = i => i == 0 ? new AssistantException(503, "quota", isQuota: true) : null;

        await rig.Worker.StepAsync(default); // backfill: 429 alle 10:05

        Assert.Equal(t0, rig.Worker.LastBackfill);
        Assert.Equal(t0 + TimeSpan.FromMinutes(5) + TimeSpan.FromHours(1), rig.Worker.SuspendedUntil); // 11:05
        Assert.Empty(rig.Store.Upserts);
        var callsAfterQuota = rig.Embed.Batches.Count;

        // In sospensione le attivita' accodate non toccano Gemini e il backfill non parte (alle 11:04 manca ancora 1 minuto).
        rig.Clock.Advance(TimeSpan.FromMinutes(59)); // 11:04
        rig.Scheduler.Enqueue(1);
        await rig.Worker.StepAsync(default);
        Assert.Equal(callsAfterQuota, rig.Embed.Batches.Count);
        Assert.Equal(t0, rig.Worker.LastBackfill); // un backfill saltato non aggiorna l'istante dell'ultimo

        // Alle 11:05, appena finita la sospensione, il backfill parte subito (non un'ora dopo l'ultimo tentativo).
        rig.Clock.Advance(TimeSpan.FromMinutes(1));
        rig.Scheduler.Enqueue(1);
        await rig.Worker.StepAsync(default);
        Assert.Single(rig.Store.Upserts);
        Assert.Equal(rig.Clock.GetUtcNow(), rig.Worker.LastBackfill);
    }

    [Fact]
    public void Scheduler_IgnoresEnqueue_WhenNotConfigured()
    {
        var embed = new FakeEmbeddingClient { IsConfigured = false };
        var scheduler = new TaskIndexScheduler(embed);

        scheduler.Enqueue(1);

        Assert.False(scheduler.Reader.TryRead(out _));
    }
}
