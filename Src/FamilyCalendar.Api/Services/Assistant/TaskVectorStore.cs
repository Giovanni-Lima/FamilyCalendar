using System.Buffers.Binary;
using FamilyCalendar.Data;
using FamilyCalendar.Domain;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;

namespace FamilyCalendar.Services.Assistant;

/// <inheritdoc cref="ITaskVectorStore"/>
public sealed class TaskVectorStore : ITaskVectorStore
{
    private const int MySqlDuplicateEntry = 1062;     // ER_DUP_ENTRY: insert concorrente della stessa PK
    private const int MySqlNoReferencedRow = 1452;    // ER_NO_REFERENCED_ROW_2: attività cancellata nel frattempo (FK)

    private readonly FamilyCalendarDbContext _db;
    private readonly ILogger<TaskVectorStore> _log;

    public TaskVectorStore(FamilyCalendarDbContext db, ILogger<TaskVectorStore> log)
    {
        _db = db;
        _log = log;
    }

    public async Task UpsertAsync(int taskId, string model, string inputSha256, float[] embedding, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputSha256);
        if (embedding is null || embedding.Length == 0)
            throw new ArgumentException("Embedding vuoto.", nameof(embedding));

        var blob = ToBytes(embedding);
        var now = DateTime.UtcNow;

        if (await TryUpdateAsync(taskId, model, inputSha256, blob, now, ct)) return;

        try
        {
            _db.TaskEmbeddings.Add(new TaskEmbedding
            {
                TaskItemId = taskId,
                Embedding = blob,
                Model = model,
                InputSha256 = inputSha256,
                EmbeddedAt = now,
            });
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsExpectedConflict(ex))
        {
            // Insert concorrente della stessa riga (PK duplicata) oppure attività cancellata nel frattempo (FK):
            // casi attesi. Altri errori (connessione, schema, ...) NON sono ingoiati e si propagano.
            _log.LogWarning("Embedding dell'attività {TaskId}: conflitto atteso sull'insert (errore MySQL {Code}), ritento come aggiornamento",
                taskId, (ex.InnerException as MySqlException)?.Number);
            _db.ChangeTracker.Clear();
            // Se dopo il ritentativo non c'è nulla da aggiornare, l'attività non esiste più: niente da indicizzare.
            await TryUpdateAsync(taskId, model, inputSha256, blob, now, ct);
        }
    }

    private async Task<bool> TryUpdateAsync(int taskId, string model, string sha, byte[] blob, DateTime now, CancellationToken ct)
        => await _db.TaskEmbeddings
            .Where(e => e.TaskItemId == taskId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Embedding, blob)
                .SetProperty(e => e.Model, model)
                .SetProperty(e => e.InputSha256, sha)
                .SetProperty(e => e.EmbeddedAt, now), ct) > 0;

    public async Task DeleteAsync(int taskId, CancellationToken ct)
        => await _db.TaskEmbeddings.Where(e => e.TaskItemId == taskId).ExecuteDeleteAsync(ct);

    /// <summary>Duplicate key o FK mancante: gli unici errori di insert che si possono ritentare/ignorare in sicurezza.</summary>
    internal static bool IsExpectedConflict(DbUpdateException ex) =>
        ex.InnerException is MySqlException { Number: MySqlDuplicateEntry or MySqlNoReferencedRow };

    public async Task<string?> GetHashAsync(int taskId, string model, CancellationToken ct)
        => await _db.TaskEmbeddings.AsNoTracking()
            .Where(e => e.TaskItemId == taskId && e.Model == model)
            .Select(e => e.InputSha256)
            .FirstOrDefaultAsync(ct);

    public async Task<Dictionary<int, string>> GetHashesAsync(string model, CancellationToken ct)
        => await _db.TaskEmbeddings.AsNoTracking()
            .Where(e => e.Model == model)
            .ToDictionaryAsync(e => e.TaskItemId, e => e.InputSha256, ct);

    public async Task<IReadOnlyList<VectorHit>> SearchAsync(
        float[] queryEmbedding, string model, int topK, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        if (queryEmbedding is null || queryEmbedding.Length == 0)
            throw new ArgumentException("Embedding della query vuoto.", nameof(queryEmbedding));
        if (topK <= 0) return Array.Empty<VectorHit>();

        // Difensivo: anche se già normalizzato, il prodotto scalare == coseno solo con query unitaria.
        var query = Normalize(queryEmbedding);
        if (query is null) return Array.Empty<VectorHit>();

        var rows = _db.TaskEmbeddings.AsNoTracking().Where(e => e.Model == model);
        if (from is { } fromDay)
            rows = rows.Where(e => e.TaskItem.Date >= fromDay);
        if (to is { } toDay)
            rows = rows.Where(e => e.TaskItem.Date <= toDay);

        // Si caricano solo id + blob: le attività complete si leggono solo per i topK vincitori.
        var candidates = await rows.Select(e => new { e.TaskItemId, e.Embedding }).ToListAsync(ct);

        var scored = new List<(int Id, double Score)>(candidates.Count);
        foreach (var c in candidates)
        {
            // Vettori di dimensione diversa (altro modello) non sono confrontabili: scartati.
            if (c.Embedding.Length != query.Length * sizeof(float)) continue;
            scored.Add((c.TaskItemId, Dot(query, c.Embedding)));
        }

        var top = scored.OrderByDescending(s => s.Score).ThenBy(s => s.Id).Take(topK).ToList();
        if (top.Count == 0) return Array.Empty<VectorHit>();

        var ids = top.Select(t => t.Id).ToList();
        var tasks = await _db.Tasks.AsNoTracking()
            .Include(t => t.Label)
            .Include(t => t.CreatedBy)
            .Where(t => ids.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, ct);

        // Un'attività cancellata tra le due query viene semplicemente omessa.
        return top.Where(t => tasks.ContainsKey(t.Id))
                  .Select(t => new VectorHit(tasks[t.Id], t.Score))
                  .ToList();
    }

    // ---- Funzioni pure (testabili) -------------------------------------------------------------------------

    /// <summary>float[] -> byte[] float32 little-endian (4 byte per valore, indipendente dall'endianness della macchina).</summary>
    public static byte[] ToBytes(float[] vector)
    {
        ArgumentNullException.ThrowIfNull(vector);
        var bytes = new byte[vector.Length * sizeof(float)];
        for (var i = 0; i < vector.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float)), vector[i]);
        return bytes;
    }

    /// <summary>byte[] float32 little-endian -> float[]. Lancia se la lunghezza non è multipla di 4.</summary>
    public static float[] ToFloats(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length % sizeof(float) != 0)
            throw new ArgumentException("La lunghezza del blob non è multipla di 4 byte.", nameof(bytes));
        var vector = new float[bytes.Length / sizeof(float)];
        for (var i = 0; i < vector.Length; i++)
            vector[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * sizeof(float)));
        return vector;
    }

    /// <summary>Coseno completo (non assume vettori normalizzati). 0 se le lunghezze differiscono o un vettore è nullo.</summary>
    public static double CosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0;
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            na += (double)a[i] * a[i];
            nb += (double)b[i] * b[i];
        }
        return na == 0 || nb == 0 ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    /// <summary>Copia L2-normalizzata, o null se il vettore ha norma zero o contiene valori non finiti.</summary>
    internal static float[]? Normalize(float[] v)
    {
        double sum = 0;
        foreach (var x in v) sum += (double)x * x;
        if (sum == 0 || double.IsNaN(sum) || double.IsInfinity(sum)) return null;
        var inv = 1.0 / Math.Sqrt(sum);
        var r = new float[v.Length];
        for (var i = 0; i < v.Length; i++) r[i] = (float)(v[i] * inv);
        return r;
    }

    /// <summary>Prodotto scalare tra un vettore e un blob float32 LE della stessa dimensione (senza allocare).</summary>
    internal static double Dot(float[] query, byte[] blob)
    {
        double dot = 0;
        for (var i = 0; i < query.Length; i++)
            dot += (double)query[i] * BinaryPrimitives.ReadSingleLittleEndian(blob.AsSpan(i * sizeof(float)));
        return dot;
    }
}
