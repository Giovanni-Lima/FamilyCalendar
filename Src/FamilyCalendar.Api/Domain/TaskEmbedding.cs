namespace FamilyCalendar.Domain;

/// <summary>
/// Embedding (vettore semantico) di un'attività, usato dall'assistente Aesir per la ricerca per similarità.
/// MySQL non ha un tipo vettoriale: il vettore è salvato come BLOB di float32 little-endian
/// (768 dimensioni = 3072 byte) e il coseno è calcolato in memoria (vedi <c>TaskVectorStore</c>).
/// Relazione 1:1 con <see cref="TaskItem"/>; si cancella a cascata con l'attività.
/// </summary>
public class TaskEmbedding
{
    /// <summary>PK e FK verso <see cref="TaskItem"/>.</summary>
    public int TaskItemId { get; set; }
    public TaskItem TaskItem { get; set; } = null!;

    /// <summary>Vettore L2-normalizzato, float32 little-endian (4 byte per dimensione).</summary>
    public byte[] Embedding { get; set; } = null!;

    /// <summary>Modello che ha prodotto il vettore (es. <c>gemini-embedding-2</c>): cambiando modello gli embedding vanno rigenerati.</summary>
    public string Model { get; set; } = null!;

    /// <summary>SHA-256 esadecimale (64 caratteri) del testo embeddato: se non cambia, non serve rigenerare.</summary>
    public string InputSha256 { get; set; } = null!;

    public DateTime EmbeddedAt { get; set; } = DateTime.UtcNow;
}
