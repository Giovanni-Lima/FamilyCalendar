using FamilyCalendar.Domain;

namespace FamilyCalendar.Services.Assistant;

/// <summary>Risultato di una ricerca per similarità: l'attività (con <c>Label</c> e <c>CreatedBy</c> caricati) e il suo punteggio coseno.</summary>
public sealed record VectorHit(TaskItem Task, double Score);

/// <summary>
/// Archivio degli embedding delle attività (tabella <c>TaskEmbeddings</c>) con ricerca per similarità coseno
/// calcolata in memoria. Pensato per poche migliaia di attività; vettori float32 normalizzati L2.
/// </summary>
public interface ITaskVectorStore
{
    /// <summary>Inserisce o sostituisce l'embedding di un'attività. Se l'attività non esiste più non fa nulla.</summary>
    Task UpsertAsync(int taskId, string model, string inputSha256, float[] embedding, CancellationToken ct);

    /// <summary>Rimuove l'embedding di un'attività (no-op se assente). Con la cascata FK è già implicito alla cancellazione dell'attività.</summary>
    Task DeleteAsync(int taskId, CancellationToken ct);

    /// <summary>taskId -> InputSha256 per gli embedding prodotti dal <paramref name="model"/> indicato (indicizzazione incrementale).</summary>
    Task<Dictionary<int, string>> GetHashesAsync(string model, CancellationToken ct);

    /// <summary>Hash memorizzato per una singola attività e modello, o <c>null</c> se non è indicizzata con quel modello.</summary>
    Task<string?> GetHashAsync(int taskId, string model, CancellationToken ct);

    /// <summary>
    /// Primi <paramref name="topK"/> risultati per similarità coseno decrescente, solo tra gli embedding prodotti dal
    /// <paramref name="model"/> indicato (vettori di modelli diversi non sono confrontabili).
    /// <paramref name="from"/>/<paramref name="to"/> filtrano sulla <c>TaskItem.Date</c> (giorni di calendario locali, estremi inclusi).
    /// Le attività completate sono INCLUSE (<c>Task.Done</c> è nel risultato).
    /// </summary>
    Task<IReadOnlyList<VectorHit>> SearchAsync(float[] queryEmbedding, string model, int topK, DateOnly? from, DateOnly? to, CancellationToken ct);
}
