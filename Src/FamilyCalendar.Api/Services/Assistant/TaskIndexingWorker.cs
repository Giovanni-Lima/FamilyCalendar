using System.Threading.Channels;

namespace FamilyCalendar.Services.Assistant;

/// <summary>Coda delle attività da (re)indicizzare dopo create/update: non blocca mai la richiesta dell'utente.</summary>
public interface ITaskIndexScheduler
{
    /// <summary>Accoda l'attività; no-op se l'assistente non è configurato. Non solleva mai.</summary>
    void Enqueue(int taskId);
}

/// <inheritdoc cref="ITaskIndexScheduler"/>
public sealed class TaskIndexScheduler : ITaskIndexScheduler
{
    private readonly Channel<int> _channel = Channel.CreateUnbounded<int>(new UnboundedChannelOptions { SingleReader = true });
    private readonly IEmbeddingClient _embeddings;

    public TaskIndexScheduler(IEmbeddingClient embeddings) => _embeddings = embeddings;

    internal ChannelReader<int> Reader => _channel.Reader;

    public void Enqueue(int taskId)
    {
        if (_embeddings.IsConfigured)
            _channel.Writer.TryWrite(taskId);
    }
}

/// <summary>
/// Servizio in background dell'indicizzazione: all'avvio fa il backfill (ciò che manca o è cambiato, con priorità alle
/// attività da oggi in poi e tetto per giro), poi consuma la coda delle attività create/modificate e ripete il backfill
/// quando sono trascorsi <see cref="BackfillInterval"/> dall'ultimo, anche se la coda è sempre occupata. Se Gemini risponde
/// 429 sospende ogni indicizzazione per <see cref="QuotaSuspension"/>. Termina subito e in silenzio se manca la chiave
/// Gemini. Non propaga eccezioni (altrimenti .NET fermerebbe l'host).
/// </summary>
public sealed class TaskIndexingWorker : BackgroundService
{
    internal const int BatchSize = 50;
    internal static readonly TimeSpan BatchPause = TimeSpan.FromSeconds(3);
    internal static readonly TimeSpan BackfillInterval = TimeSpan.FromHours(1);
    internal static readonly TimeSpan QuotaSuspension = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly TaskIndexScheduler _scheduler;
    private readonly IEmbeddingClient _embeddings;
    private readonly AssistantOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<TaskIndexingWorker> _log;

    private DateTimeOffset? _lastBackfill;
    private DateTimeOffset _suspendedUntil = DateTimeOffset.MinValue;

    public TaskIndexingWorker(
        IServiceScopeFactory scopes, TaskIndexScheduler scheduler, IEmbeddingClient embeddings,
        AssistantOptions options, TimeProvider clock, ILogger<TaskIndexingWorker> log)
    {
        _scopes = scopes;
        _scheduler = scheduler;
        _embeddings = embeddings;
        _options = options;
        _clock = clock;
        _log = log;
    }

    internal DateTimeOffset? LastBackfill => _lastBackfill;
    internal DateTimeOffset SuspendedUntil => _suspendedUntil;
    internal TimeSpan BatchPauseOverride { get; set; } = BatchPause;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.DeadlineReducedFrom is { } configured)
            _log.LogWarning("Aesir: Assistant:DeadlineSeconds={Configured:0} ridotto a {Effective:0} s perche' il timeout del client ({Client:0} s) deve superare attesa slot + deadline",
                configured.TotalSeconds, _options.Deadline.TotalSeconds, AssistantOptions.ClientTimeout.TotalSeconds);

        if (RomeTime.IsFallbackToUtc)
            _log.LogWarning("Aesir: fuso Europe/Rome non disponibile sul sistema, uso UTC: date e orari dell'assistente possono essere sfasati di 1-2 ore");

        if (!_embeddings.IsConfigured)
        {
            _log.LogInformation("Aesir: chiave Gemini assente, indicizzazione disattivata");
            return;
        }

        try
        {
            await Task.Yield(); // l'avvio dell'host non deve aspettare il backfill
            while (!stoppingToken.IsCancellationRequested)
            {
                if (!await StepAsync(stoppingToken)) break;
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // arresto dell'applicazione
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Aesir: il servizio di indicizzazione si è fermato per un errore");
        }
    }

    /// <summary>
    /// Un passo del ciclo: se è ora del backfill (mai fatto, o ultimo da almeno <see cref="BackfillInterval"/>) lo esegue;
    /// altrimenti attende lavoro in coda al più fino alla scadenza dell'intervallo e indicizza le attività accodate.
    /// Restituisce false se la coda è stata chiusa.
    /// </summary>
    internal async Task<bool> StepAsync(CancellationToken stop)
    {
        var now = _clock.GetUtcNow();
        // Prossimo backfill: un'ora dopo l'ultimo, ma mai prima della fine di un'eventuale sospensione per quota.
        var due = _lastBackfill is { } last ? last + BackfillInterval : now;
        if (_suspendedUntil > due) due = _suspendedUntil;
        var remaining = due - now;
        if (remaining <= TimeSpan.Zero)
        {
            await BackfillAsync(stop);
            return true;
        }

        using var wait = CancellationTokenSource.CreateLinkedTokenSource(stop);
        wait.CancelAfter(remaining);
        bool hasWork;
        try
        {
            hasWork = await _scheduler.Reader.WaitToReadAsync(wait.Token);
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        {
            return true; // intervallo scaduto: al prossimo passo tocca al backfill
        }
        if (!hasWork) return false;

        var ids = new HashSet<int>();
        while (_scheduler.Reader.TryRead(out var id)) ids.Add(id);
        foreach (var id in ids)
            await IndexOneAsync(id, stop);
        return true;
    }

    private async Task BackfillAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        if (now < _suspendedUntil)
        {
            // Difensivo (StepAsync non dovrebbe arrivarci): un backfill saltato NON aggiorna _lastBackfill.
            _log.LogInformation("Aesir: backfill sospeso fino alle {Until:HH:mm} UTC (quota Gemini)", _suspendedUntil);
            return;
        }
        _lastBackfill = now;

        try
        {
            using var scope = _scopes.CreateScope();
            var indexer = scope.ServiceProvider.GetRequiredService<TaskIndexer>();
            var report = await indexer.IndexMissingAsync(BatchSize, BatchPauseOverride, _options.BackfillMaxPerRun, ct);
            if (report.StoppedByQuota)
                Suspend();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Aesir: backfill dell'indicizzazione non riuscito");
        }
    }

    private async Task IndexOneAsync(int taskId, CancellationToken ct)
    {
        if (_clock.GetUtcNow() < _suspendedUntil)
            return; // quota esaurita: ci penserà il backfill (confronto degli hash) dopo la sospensione

        try
        {
            using var scope = _scopes.CreateScope();
            var indexer = scope.ServiceProvider.GetRequiredService<TaskIndexer>();
            await indexer.IndexTaskAsync(taskId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (AssistantException ex) when (ex.IsQuota)
        {
            _log.LogInformation("Aesir: quota Gemini esaurita durante l'indicizzazione dell'attività {TaskId}", taskId);
            Suspend();
        }
        catch (AssistantException ex)
        {
            _log.LogWarning("Aesir: indicizzazione dell'attività {TaskId} non riuscita: {Detail}", taskId, ex.Detail ?? ex.Message);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Aesir: indicizzazione dell'attività {TaskId} non riuscita", taskId);
        }
    }

    private void Suspend()
    {
        _suspendedUntil = _clock.GetUtcNow() + QuotaSuspension;
        _log.LogInformation("Aesir: indicizzazione sospesa per {Minutes:0} minuti (quota Gemini esaurita)", QuotaSuspension.TotalMinutes);
    }
}
