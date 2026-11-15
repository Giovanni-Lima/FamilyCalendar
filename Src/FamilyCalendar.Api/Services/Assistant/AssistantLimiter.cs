using FamilyCalendar.Domain;

namespace FamilyCalendar.Services.Assistant;

/// <summary>Motivo del rifiuto di <see cref="AssistantLimiter.TryAcquire"/>.</summary>
public readonly record struct LimitDenial(string Message, TimeSpan RetryAfter);

/// <summary>"Biglietto" di una domanda ammessa; <c>Counted</c> è false per gli amministratori (esenti, niente da rimborsare).</summary>
public readonly record struct LimitTicket(string User, DateTimeOffset At, bool Counted);

/// <summary>
/// Limita l'uso dell'assistente: la quota gratuita di Gemini/Groq è condivisa da tutti, quindi senza freni uno solo la
/// esaurirebbe per gli altri. Tre livelli: domande per utente all'ora, domande totali al giorno, chiamate contemporanee.
/// I contatori sono in memoria (si azzerano al riavvio): protezione dagli abusi, non contabilità. Config:
/// <c>Assistant:PerUserPerHour</c> (10), <c>Assistant:GlobalPerDay</c> (80), <c>Assistant:MaxConcurrent</c> (2).
/// Gli amministratori sono esentati dai limiti per utente e giornaliero (restano il tetto di concorrenza e la quota reale dei provider);
/// senza principal (login disattivato) si usa la chiave "anonimo", soggetta ai limiti.
/// </summary>
public sealed class AssistantLimiter
{
    public const string AnonymousKey = "anonimo";

    private readonly int _perUserPerHour;
    private readonly int _globalPerDay;
    private readonly SemaphoreSlim _slots;
    private readonly TimeProvider _clock;
    private readonly object _lock = new();
    private readonly Dictionary<string, List<DateTimeOffset>> _perUser = new(StringComparer.Ordinal);
    private readonly List<DateTimeOffset> _global = new();

    public AssistantLimiter(AssistantOptions options, TimeProvider clock)
    {
        _perUserPerHour = Math.Max(1, options.PerUserPerHour);
        _globalPerDay = Math.Max(1, options.GlobalPerDay);
        _slots = new SemaphoreSlim(Math.Max(1, options.MaxConcurrent));
        _clock = clock;
    }

    /// <summary>
    /// Registra una domanda se rientra nei limiti (il <paramref name="ticket"/> va passato a <see cref="Refund"/> se la
    /// domanda fallisce per colpa non dell'utente); altrimenti restituisce false e <paramref name="denial"/> è valorizzato.
    /// </summary>
    public bool TryAcquire(AuthService.Principal? principal, out LimitTicket ticket, out LimitDenial denial)
    {
        var user = principal?.Username ?? AnonymousKey;
        var now = _clock.GetUtcNow();
        if (principal?.Role == MemberRole.Amministratore)
        {
            ticket = new LimitTicket(user, now, Counted: false);
            denial = default;
            return true;
        }

        ticket = default;
        lock (_lock)
        {
            _global.RemoveAll(t => t <= now.AddDays(-1));
            if (_perUser.Count > 256)
                foreach (var key in _perUser.Where(kv => kv.Value.All(t => t <= now.AddHours(-1))).Select(kv => kv.Key).ToList())
                    _perUser.Remove(key);

            if (!_perUser.TryGetValue(user, out var mine))
                _perUser[user] = mine = new List<DateTimeOffset>();
            mine.RemoveAll(t => t <= now.AddHours(-1));

            if (_global.Count >= _globalPerDay)
            {
                var wait = _global.Min().AddDays(1) - now;
                denial = new LimitDenial("L'assistente ha raggiunto il limite giornaliero di domande (la quota gratuita è condivisa da tutti). Riprova più tardi.", wait);
                return false;
            }

            if (mine.Count >= _perUserPerHour)
            {
                var wait = mine.Min().AddHours(1) - now;
                var minutes = Math.Max(1, (int)Math.Ceiling(wait.TotalMinutes));
                denial = new LimitDenial($"Hai raggiunto il limite di {_perUserPerHour} domande all'ora. Riprova tra circa {minutes} minuti.", wait);
                return false;
            }

            mine.Add(now);
            _global.Add(now);
            denial = default;
            ticket = new LimitTicket(user, now, Counted: true);
            return true;
        }
    }

    /// <summary>Restituisce il biglietto: l'utente non paga un errore che non è suo.</summary>
    public void Refund(LimitTicket ticket)
    {
        if (!ticket.Counted) return;
        lock (_lock)
        {
            _global.Remove(ticket.At);
            if (_perUser.TryGetValue(ticket.User, out var mine))
                mine.Remove(ticket.At);
        }
    }

    /// <summary>Attende un posto per parlare con i modelli; <c>null</c> se non si libera entro <paramref name="timeout"/>.</summary>
    public async Task<IDisposable?> WaitSlotAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (!await _slots.WaitAsync(timeout, ct))
            return null;
        return new Slot(_slots);
    }

    private sealed class Slot : IDisposable
    {
        private SemaphoreSlim? _slots;

        public Slot(SemaphoreSlim slots) => _slots = slots;

        public void Dispose() => Interlocked.Exchange(ref _slots, null)?.Release();
    }
}
