namespace FamilyCalendar.Services.Assistant;

/// <summary>Risposta generata; <c>IsPrimary</c> = viene dal primo modello della catena.</summary>
public sealed record ChatResult(string Text, string Model, bool IsPrimary);

/// <summary>Catena di generazione con fallback tra provider.</summary>
public interface IChatChain
{
    Task<ChatResult> GenerateAsync(string system, string user, CancellationToken ct);
}

/// <summary>
/// Prova i provider nell'ordine dato (Gemini 3.5 flash-lite -> Gemini 3.1 flash-lite -> Groq 120b -> Groq 20b).
/// Si passa al successivo per QUALSIASI errore del provider (429/5xx, rete/DNS, timeout, JSON malformato, risposta
/// vuota/bloccata/troncata). Se falliscono tutti: 503 "troppo lento" se erano tutti timeout, 503 "saturi" se erano tutti
/// quota/timeout, altrimenti 502.
/// Una cancellazione chiesta dal chiamante (o dal deadline) interrompe la catena.
/// </summary>
public sealed class ChatChain : IChatChain
{
    private readonly IReadOnlyList<IChatProvider> _providers;
    private readonly ILogger<ChatChain> _log;

    public ChatChain(IReadOnlyList<IChatProvider> providers, ILogger<ChatChain> log)
    {
        _providers = providers;
        _log = log;
    }

    public async Task<ChatResult> GenerateAsync(string system, string user, CancellationToken ct)
    {
        if (_providers.Count == 0)
            throw new AssistantException(503, "Nessun modello di risposta è disponibile.");

        var failures = new List<AssistantException>();
        for (var i = 0; i < _providers.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var p = _providers[i];
            try
            {
                var text = await p.GenerateAsync(system, user, ct);
                if (string.IsNullOrWhiteSpace(text))
                    throw new AssistantException(502, ProviderHttp.ErrorMessage, $"{p.Name} {p.Model}: risposta vuota");
                return new ChatResult(text.Trim(), p.Model, i == 0);
            }
            catch (AssistantException ex)
            {
                _log.LogWarning("Assistente: {Provider} {Model} non disponibile ({Detail}), passo al successivo",
                    p.Name, p.Model, ex.Detail ?? ex.Message);
                failures.Add(ex);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _log.LogWarning("Assistente: {Provider} {Model} timeout, passo al successivo", p.Name, p.Model);
                failures.Add(new AssistantException(503, ProviderHttp.TimeoutMessage, $"{p.Name} {p.Model}: timeout", isTimeout: true));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Difensivo: un provider non deve mai far saltare la catena con un'eccezione imprevista.
                _log.LogError(ex, "Assistente: errore imprevisto da {Provider} {Model}, passo al successivo", p.Name, p.Model);
                failures.Add(new AssistantException(502, ProviderHttp.ErrorMessage, $"{p.Name} {p.Model}: {ex.GetType().Name}", inner: ex));
            }
        }

        if (failures.All(f => f.IsTimeout))
            throw new AssistantException(503, ProviderHttp.TooSlowMessage, isTimeout: true);
        if (failures.All(f => f.StatusCode == 503))
            // IsQuota solo se OGNI modello ha risposto 429 (nessun token consumato): il controller rimborsa il biglietto.
            throw new AssistantException(503, "I modelli di risposta sono momentaneamente saturi (quota gratuita). Riprova tra qualche minuto.",
                isQuota: failures.All(f => f.IsQuota));
        throw new AssistantException(502, "I modelli di risposta non sono disponibili al momento. Riprova tra poco.");
    }
}
