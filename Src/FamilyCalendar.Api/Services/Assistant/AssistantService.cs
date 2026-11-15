using FamilyCalendar.Domain;

namespace FamilyCalendar.Services.Assistant;

/// <summary>
/// RAG sulle attività del calendario: (1) embedda la domanda con Gemini, (2) costruisce un contesto IBRIDO — i top-K
/// semantici più le prossime attività non completate dei prossimi 60 giorni (una domanda come "prossimi eventi" non è
/// semantica e va ancorata alle date) — (3) lo passa numerato e in ordine di data alla catena di modelli, che risponde
/// citando con <c>[n]</c>; (4) restituisce solo le fonti effettivamente citate.
/// </summary>
public sealed class AssistantService
{
    public const int MaxQuestionLength = 500;
    public const int SemanticTopK = 30;
    public const int UpcomingMax = 30;
    public const int UpcomingDays = 60;

    public const string NotConfiguredMessage = "L'assistente non è configurato (manca la chiave Gemini).";
    public const string NoTasksAnswer = "Non ho trovato nessuna attività nel calendario su cui basare la risposta.";
    public const string DeadlineMessage = ProviderHttp.TooSlowMessage;

    private readonly IEmbeddingClient _embeddings;
    private readonly ITaskVectorStore _store;
    private readonly ITaskSource _tasks;
    private readonly IChatChain _chain;
    private readonly AssistantOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<AssistantService> _log;

    public AssistantService(
        IEmbeddingClient embeddings, ITaskVectorStore store, ITaskSource tasks, IChatChain chain,
        AssistantOptions options, TimeProvider clock, ILogger<AssistantService> log)
    {
        _embeddings = embeddings;
        _store = store;
        _tasks = tasks;
        _chain = chain;
        _options = options;
        _clock = clock;
        _log = log;
    }

    /// <summary>Basta la chiave Gemini (gli embedding la richiedono); Groq è solo un ripiego per la risposta.</summary>
    public bool IsConfigured => _embeddings.IsConfigured;

    /// <summary>Messaggio d'errore se la domanda non è valida (vuota o troppo lunga), altrimenti <c>null</c>.</summary>
    public static string? ValidateQuestion(string? question)
    {
        var q = question?.Trim();
        if (string.IsNullOrEmpty(q)) return "Scrivi una domanda.";
        if (q.Length > MaxQuestionLength) return $"La domanda è troppo lunga (massimo {MaxQuestionLength} caratteri).";
        return null;
    }

    /// <param name="progress">Se passato, viene aggiornato con le fasi raggiunte (il controller lo usa per decidere il rimborso del biglietto).</param>
    public async Task<AssistantAnswerDto> AskAsync(string? question, AuthService.Principal? principal, CancellationToken ct, AskProgress? progress = null)
    {
        if (ValidateQuestion(question) is { } invalid)
            throw new AssistantException(400, invalid);
        if (!IsConfigured)
            throw new AssistantException(503, NotConfiguredMessage);

        // Tetto complessivo: embedding + eventuali fallback dei modelli non possono superare il deadline.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_options.Deadline);
        try
        {
            return await AskCoreAsync(question!.Trim(), principal, progress ?? new AskProgress(), deadline.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            _log.LogWarning("Assistente: deadline di {Seconds:0} s superato", _options.Deadline.TotalSeconds);
            throw new AssistantException(503, DeadlineMessage);
        }
    }

    private async Task<AssistantAnswerDto> AskCoreAsync(string question, AuthService.Principal? principal, AskProgress progress, CancellationToken ct)
    {
        var now = RomeTime.Now(_clock);
        var today = DateOnly.FromDateTime(now);

        var queryVector = await _embeddings.EmbedQueryAsync(question, ct);
        var semantic = await _store.SearchAsync(queryVector, _embeddings.ModelName, SemanticTopK, null, null, ct);
        var upcoming = await _tasks.GetUpcomingAsync(today, today.AddDays(UpcomingDays), UpcomingMax, ct);

        var entries = BuildContext(semantic, upcoming, _options.MaxContextChars);
        if (entries.Count == 0)
            return new AssistantAnswerDto(NoTasksAnswer, new List<AssistantSourceDto>(), string.Empty, false, 0);

        var userPrompt = AssistantPrompt.BuildUserPrompt(today, TimeOnly.FromDateTime(now), question, entries);
        progress.GenerationStarted = true; // da qui in poi si consumano le quote dei modelli di risposta: il biglietto non si rimborsa
        var result = await _chain.GenerateAsync(AssistantPrompt.SystemPrompt, userPrompt, ct);

        var answer = AssistantPrompt.NormalizeCitations(result.Text);
        var cited = AssistantPrompt.ExtractCitations(answer, entries.Count);
        var sources = AssistantPrompt.BuildSources(entries, cited);

        _log.LogInformation("Assistente: risposta da {Model} per {User} ({Retrieved} attività, {Cited} citate)",
            result.Model, principal?.Username ?? AssistantLimiter.AnonymousKey, entries.Count, sources.Count);
        return new AssistantAnswerDto(answer, sources, result.Model, !result.IsPrimary, entries.Count);
    }

    /// <summary>
    /// Unione deduplicata (per Id) di risultati semantici e prossime attività, ordinata per data e ora (le attività senza
    /// ora prima) e numerata da 1. Se il testo supera <paramref name="maxChars"/> si scartano prima le voci solo-semantiche
    /// col punteggio più basso, poi (tra le "prossime") le più lontane nel futuro; l'ordine cronologico resta invariato.
    /// </summary>
    public static List<ContextEntry> BuildContext(IEnumerable<VectorHit> semantic, IEnumerable<TaskItem> upcoming, int maxChars = int.MaxValue)
    {
        var candidates = new Dictionary<int, (TaskItem Task, double Score, bool IsUpcoming)>();
        foreach (var h in semantic)
            if (!candidates.ContainsKey(h.Task.Id)) candidates[h.Task.Id] = (h.Task, h.Score, false);
        foreach (var t in upcoming)
            candidates[t.Id] = candidates.TryGetValue(t.Id, out var c) ? (c.Task, c.Score, true) : (t, double.NegativeInfinity, true);

        var cost = candidates.ToDictionary(kv => kv.Key, kv => AssistantPrompt.FormatLine(new ContextEntry(99, kv.Value.Task)).Length + 1);
        var total = cost.Values.Sum();
        if (total > maxChars)
        {
            // Ordine di scarto: 1) solo-semantiche, punteggio crescente; 2) il resto, dalle più lontane nel futuro.
            var discard = candidates.Values.Where(c => !c.IsUpcoming).OrderBy(c => c.Score).ThenByDescending(c => c.Task.Date).ThenByDescending(c => c.Task.Id)
                .Concat(candidates.Values.Where(c => c.IsUpcoming).OrderByDescending(c => c.Task.Date)
                    .ThenByDescending(c => c.Task.Time).ThenByDescending(c => c.Task.Id))
                .ToList();
            foreach (var c in discard)
            {
                if (total <= maxChars || candidates.Count <= 1) break;
                candidates.Remove(c.Task.Id);
                total -= cost[c.Task.Id];
            }
        }

        return candidates.Values.Select(c => c.Task)
            .OrderBy(t => t.Date).ThenBy(t => t.Time.HasValue ? 1 : 0).ThenBy(t => t.Time).ThenBy(t => t.Id)
            .Select((t, i) => new ContextEntry(i + 1, t))
            .ToList();
    }
}

/// <summary>Fasi raggiunte da una domanda, per decidere se il biglietto del limiter va rimborsato.</summary>
public sealed class AskProgress
{
    /// <summary>La catena di generazione sta per essere (o è stata) chiamata: le quote dei modelli sono consumate.</summary>
    public bool GenerationStarted { get; set; }
}
