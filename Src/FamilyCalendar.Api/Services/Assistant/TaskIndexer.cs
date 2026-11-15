using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using FamilyCalendar.Domain;

namespace FamilyCalendar.Services.Assistant;

/// <summary>Esito di un'indicizzazione incrementale.</summary>
/// <param name="Pending">Attività da indicizzare dopo il tetto per giro (<c>Skipped</c> = rimaste fuori dal tetto, riprese al giro successivo).</param>
public sealed record IndexingReport(int Total, int Pending, int Indexed, bool StoppedByQuota, bool StoppedByError, int Skipped = 0);

/// <summary>
/// Costruisce il testo-documento di un'attività, ne calcola l'hash SHA-256 e mantiene aggiornato l'archivio vettoriale:
/// <see cref="IndexTaskAsync"/> per una singola attività, <see cref="IndexMissingAsync"/> (incrementale, a batch) per il backfill.
/// </summary>
public sealed partial class TaskIndexer
{
    private readonly ITaskSource _tasks;
    private readonly ITaskVectorStore _store;
    private readonly IEmbeddingClient _embeddings;
    private readonly TimeProvider _clock;
    private readonly ILogger<TaskIndexer> _log;

    public TaskIndexer(ITaskSource tasks, ITaskVectorStore store, IEmbeddingClient embeddings, TimeProvider clock, ILogger<TaskIndexer> log)
    {
        _clock = clock;
        _tasks = tasks;
        _store = store;
        _embeddings = embeddings;
        _log = log;
    }

    // ---- Funzioni pure (testabili) --------------------------------------------------------------------------

    /// <summary>Testo che rappresenta l'attività per l'embedding. Deterministico: stessi dati, stesso testo (e stesso hash).</summary>
    public static string BuildDocumentText(TaskItem t)
    {
        var sb = new StringBuilder();
        sb.Append("Attività: ").Append(Clean(t.Title)).Append('\n');
        sb.Append("Data: ").Append(RomeTime.LongDate(t.Date)).Append('\n');
        sb.Append("Ora: ").Append(t.Time is { } time ? RomeTime.Hhmm(time) : "tutto il giorno").Append('\n');
        if (!string.IsNullOrWhiteSpace(t.Address)) sb.Append("Luogo: ").Append(Clean(t.Address)).Append('\n');
        if (!string.IsNullOrWhiteSpace(t.Label?.Name)) sb.Append("Etichetta: ").Append(Clean(t.Label!.Name)).Append('\n');
        if (!string.IsNullOrWhiteSpace(t.CreatedBy?.DisplayName)) sb.Append("Autore: ").Append(Clean(t.CreatedBy!.DisplayName)).Append('\n');
        sb.Append("Stato: ").Append(t.Done ? "fatta" : "da fare");
        if (!string.IsNullOrWhiteSpace(t.Note)) sb.Append('\n').Append("Note: ").Append(Clean(t.Note));
        return sb.ToString();
    }

    /// <summary>SHA-256 esadecimale minuscolo (64 caratteri) del testo.</summary>
    public static string ComputeHash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static string Clean(string s) => Whitespace().Replace(s, " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    // ---- Indicizzazione -------------------------------------------------------------------------------------

    /// <summary>
    /// Reindicizza una singola attività. Non fa nulla se non esiste più o se l'hash già memorizzato (per attività e modello)
    /// coincide: nessuna chiamata di embedding sprecata. Restituisce true se ha embeddato.
    /// </summary>
    public async Task<bool> IndexTaskAsync(int taskId, CancellationToken ct)
    {
        var task = await _tasks.GetAsync(taskId, ct);
        if (task is null) return false;

        var text = BuildDocumentText(task);
        var hash = ComputeHash(text);
        var model = _embeddings.ModelName;
        if (string.Equals(await _store.GetHashAsync(task.Id, model, ct), hash, StringComparison.Ordinal))
            return false;

        var vectors = await _embeddings.EmbedDocumentsAsync(new[] { text }, ct);
        await _store.UpsertAsync(task.Id, model, hash, vectors[0], ct);
        return true;
    }

    /// <summary>
    /// Ordine di indicizzazione: prima le attività da oggi in poi (le più vicine per prime, sono quelle che l'assistente
    /// usa di più), poi quelle passate, dalla più recente alla più vecchia.
    /// </summary>
    internal static IEnumerable<T> Prioritize<T>(IEnumerable<T> items, Func<T, DateOnly> date, Func<T, int> id, DateOnly today)
    {
        var list = items.ToList();
        return list.Where(i => date(i) >= today).OrderBy(date).ThenBy(id)
            .Concat(list.Where(i => date(i) < today).OrderByDescending(date).ThenBy(id));
    }

    /// <summary>
    /// Indicizza ciò che manca o è cambiato (confronto degli hash per il modello corrente), a batch di
    /// <paramref name="batchSize"/> con <paramref name="pause"/> tra un batch e l'altro, al massimo
    /// <paramref name="maxPerRun"/> attività per giro (priorità: da oggi in poi, poi il passato più recente). Alla quota
    /// esaurita (429) o a un errore del provider si ferma senza sollevare: il resto verrà ripreso al prossimo giro.
    /// </summary>
    public async Task<IndexingReport> IndexMissingAsync(int batchSize, TimeSpan pause, int maxPerRun, CancellationToken ct)
    {
        var model = _embeddings.ModelName;
        var existing = await _store.GetHashesAsync(model, ct);
        var all = await _tasks.GetAllAsync(ct);

        var allPending = new List<(int Id, DateOnly Date, string Text, string Hash)>();
        foreach (var t in all)
        {
            var text = BuildDocumentText(t);
            var hash = ComputeHash(text);
            if (!existing.TryGetValue(t.Id, out var old) || !string.Equals(old, hash, StringComparison.Ordinal))
                allPending.Add((t.Id, t.Date, text, hash));
        }

        var ordered = Prioritize(allPending, p => p.Date, p => p.Id, RomeTime.Today(_clock)).ToList();
        var skipped = Math.Max(0, ordered.Count - maxPerRun);
        var pending = ordered.Take(maxPerRun).Select(p => (p.Id, p.Text, p.Hash)).ToList();

        var indexed = 0;
        for (var offset = 0; offset < pending.Count; offset += batchSize)
        {
            var batch = pending.Skip(offset).Take(batchSize).ToList();
            try
            {
                var vectors = await _embeddings.EmbedDocumentsAsync(batch.Select(b => b.Text).ToList(), ct);
                for (var i = 0; i < batch.Count; i++)
                {
                    await _store.UpsertAsync(batch[i].Id, model, batch[i].Hash, vectors[i], ct);
                    indexed++;
                }
            }
            catch (AssistantException ex) when (ex.IsQuota)
            {
                _log.LogInformation("Indicizzazione Aesir: quota Gemini esaurita, mi fermo dopo {Indexed}/{Pending} attività", indexed, pending.Count);
                return new IndexingReport(all.Count, pending.Count, indexed, StoppedByQuota: true, StoppedByError: false, skipped);
            }
            catch (AssistantException ex)
            {
                _log.LogWarning("Indicizzazione Aesir interrotta dopo {Indexed}/{Pending} attività: {Detail}", indexed, pending.Count, ex.Detail ?? ex.Message);
                return new IndexingReport(all.Count, pending.Count, indexed, StoppedByQuota: false, StoppedByError: true, skipped);
            }

            if (offset + batchSize < pending.Count && pause > TimeSpan.Zero)
                await Task.Delay(pause, ct);
        }

        if (pending.Count > 0)
            _log.LogInformation("Indicizzazione Aesir: {Indexed} attività indicizzate su {Total} ({Skipped} rimandate al prossimo giro)", indexed, all.Count, skipped);
        return new IndexingReport(all.Count, pending.Count, indexed, false, false, skipped);
    }
}
