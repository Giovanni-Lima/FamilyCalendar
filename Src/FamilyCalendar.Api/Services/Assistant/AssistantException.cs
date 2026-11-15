namespace FamilyCalendar.Services.Assistant;

/// <summary>
/// Errore "atteso" dell'assistente con lo status HTTP da restituire (400 input non valido, 502 errore dei
/// provider, 503 servizio non configurato / saturo / scaduto). <see cref="Exception.Message"/> è pensato per
/// l'utente (italiano, senza dettagli tecnici); i dettagli per i log stanno in <see cref="Detail"/> (mai chiavi API).
/// Tutti gli errori dei provider (rete, DNS, timeout, HTTP, JSON malformato, risposta vuota/bloccata/troncata)
/// vengono ricondotti a questo tipo, così non diventano mai un 500 e la catena può passare al provider successivo.
/// </summary>
public sealed class AssistantException : Exception
{
    public int StatusCode { get; }

    /// <summary>Quota del provider esaurita (HTTP 429): chi indicizza in background si ferma senza errore.</summary>
    public bool IsQuota { get; }

    /// <summary>Il provider non ha risposto entro il timeout (client o deadline): distingue "troppo lento" da "quota esaurita".</summary>
    public bool IsTimeout { get; }

    /// <summary>Dettaglio tecnico solo per i log.</summary>
    public string? Detail { get; }

    public AssistantException(int statusCode, string message, string? detail = null, bool isQuota = false, Exception? inner = null, bool isTimeout = false)
        : base(message, inner)
    {
        StatusCode = statusCode;
        Detail = detail;
        IsQuota = isQuota;
        IsTimeout = isTimeout;
    }
}
