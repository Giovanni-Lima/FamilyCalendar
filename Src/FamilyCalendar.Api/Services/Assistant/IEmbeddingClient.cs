namespace FamilyCalendar.Services.Assistant;

/// <summary>Client di embedding (testi -> vettori L2-normalizzati). Interfaccia per la testabilità.</summary>
public interface IEmbeddingClient
{
    /// <summary>Chiave Gemini presente: senza, l'assistente è "non configurato" e l'indicizzazione è disattivata.</summary>
    bool IsConfigured { get; }

    /// <summary>Modello degli embedding (va salvato con ogni vettore: cambiando modello si rigenera).</summary>
    string ModelName { get; }

    /// <summary>Embedding di una domanda (formato "query"). Solleva <see cref="AssistantException"/> per ogni errore del provider.</summary>
    Task<float[]> EmbedQueryAsync(string question, CancellationToken ct);

    /// <summary>Embedding di più documenti (formato "documento"), nello stesso ordine. Solleva <see cref="AssistantException"/>.</summary>
    Task<float[][]> EmbedDocumentsAsync(IReadOnlyList<string> documents, CancellationToken ct);
}
