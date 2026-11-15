using FamilyCalendar.Services.Assistant;
using Microsoft.Extensions.Logging.Abstractions;

namespace FamilyCalendar.Tests;

public class TaskIndexerTests
{
    [Fact]
    public void DocumentText_ContainsAllFieldsInItalian()
    {
        var t = Make.Task(1, "Cena dai nonni", "2026-11-20", "19:30", note: "Portare\nil dolce", address: "Via Roma 1, Milano",
            label: "Famiglia", author: "Maria Rossi");

        var text = TaskIndexer.BuildDocumentText(t);

        Assert.Contains("Attività: Cena dai nonni", text);
        Assert.Contains("Data: venerdì 20 novembre 2026", text);
        Assert.Contains("Ora: 19:30", text);
        Assert.Contains("Luogo: Via Roma 1, Milano", text);
        Assert.Contains("Etichetta: Famiglia", text);
        Assert.Contains("Autore: Maria Rossi", text);
        Assert.Contains("Stato: da fare", text);
        Assert.Contains("Note: Portare il dolce", text); // a capo normalizzati
    }

    [Fact]
    public void DocumentText_AllDayAndDone_UsesAllDayAndOmitsEmptyFields()
    {
        var text = TaskIndexer.BuildDocumentText(Make.Task(2, "Compleanno Luca", "2026-12-01", done: true));

        Assert.Contains("Ora: tutto il giorno", text);
        Assert.Contains("Data: martedì 1 dicembre 2026", text);
        Assert.Contains("Stato: fatta", text);
        Assert.DoesNotContain("Luogo:", text);
        Assert.DoesNotContain("Etichetta:", text);
        Assert.DoesNotContain("Autore:", text);
        Assert.DoesNotContain("Note:", text);
    }

    [Fact]
    public void Hash_IsStableLowercaseSha256_AndChangesWithContent()
    {
        var a = Make.Task(1, "Dentista", "2026-11-18", "10:00");
        var same = Make.Task(1, "Dentista", "2026-11-18", "10:00");
        var done = Make.Task(1, "Dentista", "2026-11-18", "10:00", done: true);

        var h1 = TaskIndexer.ComputeHash(TaskIndexer.BuildDocumentText(a));
        var h2 = TaskIndexer.ComputeHash(TaskIndexer.BuildDocumentText(same));
        var h3 = TaskIndexer.ComputeHash(TaskIndexer.BuildDocumentText(done));

        Assert.Equal(h1, h2);
        Assert.NotEqual(h1, h3);
        Assert.Equal(64, h1.Length);
        Assert.Matches("^[0-9a-f]{64}$", h1);
        // Valore noto: SHA-256 di "abc".
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", TaskIndexer.ComputeHash("abc"));
    }

    // "Oggi" a Roma = 2026-11-15
    private static readonly FakeClock Clock = new(new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero));

    private static (TaskIndexer Indexer, FakeEmbeddingClient Embed, FakeVectorStore Store, FakeTaskSource Tasks) Setup()
    {
        var embed = new FakeEmbeddingClient();
        var store = new FakeVectorStore();
        var tasks = new FakeTaskSource();
        return (new TaskIndexer(tasks, store, embed, Clock, NullLogger<TaskIndexer>.Instance), embed, store, tasks);
    }

    [Fact]
    public async Task IndexMissing_IsIncremental_SkipsUnchangedAndReindexesChanged()
    {
        var (indexer, embed, store, tasks) = Setup();
        tasks.Tasks.AddRange(new[]
        {
            Make.Task(1, "Uno", "2026-11-20"), Make.Task(2, "Due", "2026-11-21"), Make.Task(3, "Tre", "2026-11-22"),
        });

        var first = await indexer.IndexMissingAsync(50, TimeSpan.Zero, 1000, default);
        Assert.Equal(3, first.Indexed);
        Assert.Single(embed.Batches); // un solo batch da 3

        // Nessun cambiamento: niente da fare.
        var second = await indexer.IndexMissingAsync(50, TimeSpan.Zero, 1000, default);
        Assert.Equal(0, second.Pending);
        Assert.Single(embed.Batches);

        // Cambia solo la 2 (e ne arriva una nuova, la 4).
        tasks.Tasks[1].Note = "portare la torta";
        tasks.Tasks.Add(Make.Task(4, "Quattro", "2026-11-23"));
        var third = await indexer.IndexMissingAsync(50, TimeSpan.Zero, 1000, default);
        Assert.Equal(2, third.Indexed);
        Assert.Equal(new[] { 2, 4 }, store.Upserts.Skip(3).Select(u => u.Id).OrderBy(i => i));
        Assert.All(store.Upserts, u => Assert.Equal("test-embed", u.Model));
    }

    [Fact]
    public async Task IndexMissing_SplitsInBatches()
    {
        var (indexer, embed, _, tasks) = Setup();
        for (var i = 1; i <= 7; i++) tasks.Tasks.Add(Make.Task(i, $"T{i}", "2026-11-20"));

        var report = await indexer.IndexMissingAsync(3, TimeSpan.Zero, 1000, default);

        Assert.Equal(7, report.Indexed);
        Assert.Equal(new[] { 3, 3, 1 }, embed.Batches.Select(b => b.Count));
    }

    [Fact]
    public async Task IndexMissing_StopsQuietlyOnQuota()
    {
        var (indexer, embed, store, tasks) = Setup();
        for (var i = 1; i <= 6; i++) tasks.Tasks.Add(Make.Task(i, $"T{i}", "2026-11-20"));
        embed.FailBatch = idx => idx == 1 ? new AssistantException(503, "quota", isQuota: true) : null;

        var report = await indexer.IndexMissingAsync(2, TimeSpan.Zero, 1000, default); // non solleva

        Assert.True(report.StoppedByQuota);
        Assert.Equal(2, report.Indexed); // solo il primo batch
        Assert.Equal(2, store.Upserts.Count);
        Assert.Equal(2, embed.Batches.Count); // si ferma: non prova il terzo batch
    }

    [Fact]
    public async Task IndexTask_EmbedsAndUpsertsSingleTask_AndIgnoresMissing()
    {
        var (indexer, _, store, tasks) = Setup();
        tasks.Tasks.Add(Make.Task(5, "Riunione", "2026-11-19", "09:00"));

        await indexer.IndexTaskAsync(5, default);
        await indexer.IndexTaskAsync(99, default); // inesistente: no-op

        var up = Assert.Single(store.Upserts);
        Assert.Equal(5, up.Id);
        Assert.Equal(TaskIndexer.ComputeHash(TaskIndexer.BuildDocumentText(tasks.Tasks[0])), up.Hash);
    }

    [Fact]
    public async Task IndexTask_SkipsEmbedding_WhenStoredHashIsUpToDate()
    {
        var (indexer, embed, store, tasks) = Setup();
        tasks.Tasks.Add(Make.Task(5, "Riunione", "2026-11-19", "09:00"));

        Assert.True(await indexer.IndexTaskAsync(5, default));   // prima volta: embedda
        Assert.False(await indexer.IndexTaskAsync(5, default));  // invariata: niente chiamata
        Assert.Single(embed.Batches);
        Assert.Single(store.Upserts);

        tasks.Tasks[0].Done = true;
        Assert.True(await indexer.IndexTaskAsync(5, default));   // cambiata (stato): reindicizza
        Assert.Equal(2, embed.Batches.Count);
    }

    [Fact]
    public async Task IndexMissing_PrioritizesFutureThenRecentPast_AndHonorsPerRunCap()
    {
        var (indexer, embed, store, tasks) = Setup();
        tasks.Tasks.AddRange(new[]
        {
            Make.Task(1, "passato lontano", "2026-01-10"),
            Make.Task(2, "futuro lontano", "2026-12-24"),
            Make.Task(3, "passato recente", "2026-11-10"),
            Make.Task(4, "oggi", "2026-11-15"),
            Make.Task(5, "domani", "2026-11-16"),
        });

        var report = await indexer.IndexMissingAsync(2, TimeSpan.Zero, 4, default);

        // Ordine: oggi(4), domani(5), futuro lontano(2), poi il passato piu' recente(3); il passato lontano(1) resta fuori dal tetto.
        Assert.Equal(new[] { 4, 5, 2, 3 }, store.Upserts.Select(u => u.Id));
        Assert.Equal(4, report.Indexed);
        Assert.Equal(1, report.Skipped);
        Assert.Equal(2, embed.Batches.Count);

        // Il giro successivo riprende da dove si era fermato.
        var next = await indexer.IndexMissingAsync(50, TimeSpan.Zero, 4, default);
        Assert.Equal(1, next.Indexed);
        Assert.Equal(1, store.Upserts.Last().Id);
    }
}
