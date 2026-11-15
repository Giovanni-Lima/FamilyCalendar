using System.Net;

namespace FamilyCalendar.Services.Assistant;

internal sealed record ProviderResponse(HttpStatusCode Status, string Body, TimeSpan? RetryAfter)
{
    public bool IsSuccess => (int)Status is >= 200 and < 300;
    public bool IsRetryable => Status == HttpStatusCode.TooManyRequests || (int)Status >= 500;
}

/// <summary>
/// Invio HTTP verso i provider con TUTTI gli errori ricondotti a <see cref="AssistantException"/>:
/// rete/DNS (<see cref="HttpRequestException"/>), timeout del client (TaskCanceled non dovuto al chiamante),
/// stato HTTP non riuscito. La cancellazione richiesta dal chiamante invece si propaga come
/// <see cref="OperationCanceledException"/>.
/// </summary>
internal static class ProviderHttp
{
    public const string QuotaMessage = "Il servizio dell'assistente è momentaneamente saturo (quota gratuita). Riprova tra qualche minuto.";
    public const string ErrorMessage = "Il servizio dell'assistente ha restituito un errore. Riprova tra poco.";
    public const string TimeoutMessage = "Il servizio dell'assistente non ha risposto in tempo. Riprova tra poco.";

    /// <summary>Messaggio quando TUTTI i modelli falliscono per timeout, e quando scade il deadline complessivo.</summary>
    public const string TooSlowMessage = "L'assistente sta impiegando troppo tempo a rispondere. Riprova tra poco.";

    public static async Task<ProviderResponse> SendOnceAsync(HttpClient http, HttpRequestMessage req, string provider, CancellationToken ct)
    {
        try
        {
            using var resp = await http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            return new ProviderResponse(resp.StatusCode, body, ParseRetryAfter(resp));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AssistantException(503, TimeoutMessage, $"{provider}: timeout", isTimeout: true);
        }
        catch (HttpRequestException ex)
        {
            throw new AssistantException(502, ErrorMessage, $"{provider}: errore di rete ({ex.GetType().Name}: {ex.Message})", inner: ex);
        }
        catch (IOException ex)
        {
            throw new AssistantException(502, ErrorMessage, $"{provider}: errore di I/O ({ex.Message})", inner: ex);
        }
    }

    /// <summary>
    /// Come <see cref="SendOnceAsync"/>, ma ritenta (fino a <paramref name="maxAttempts"/>) su 429/5xx/rete/timeout
    /// rispettando <c>Retry-After</c> (limitato a <paramref name="maxWait"/>). Restituisce solo risposte 2xx;
    /// altrimenti solleva <see cref="AssistantException"/> (429 -> 503 con <c>IsQuota</c>, altro -> 502).
    /// </summary>
    public static async Task<ProviderResponse> SendWithRetryAsync(
        HttpClient http, Func<HttpRequestMessage> build, string provider, int maxAttempts, TimeSpan maxWait,
        Func<TimeSpan, CancellationToken, Task> delay, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            ProviderResponse? response = null;
            AssistantException? failure = null;
            try
            {
                using var req = build();
                response = await SendOnceAsync(http, req, provider, ct);
            }
            catch (AssistantException ex)
            {
                failure = ex;
            }

            if (response is { IsSuccess: true })
                return response;

            var retryable = failure is not null || response!.IsRetryable;
            if (!retryable || attempt >= maxAttempts)
                throw failure ?? StatusError(provider, response!);

            var wait = response?.RetryAfter ?? TimeSpan.FromSeconds(Math.Min(60, 2 * Math.Pow(2, attempt - 1)));
            if (wait > maxWait) wait = maxWait;
            if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
            await delay(wait, ct);
        }
    }

    public static AssistantException StatusError(string provider, ProviderResponse r)
    {
        var detail = $"{provider} HTTP {(int)r.Status}: {Truncate(r.Body, 300)}";
        return r.Status == HttpStatusCode.TooManyRequests
            ? new AssistantException(503, QuotaMessage, detail, isQuota: true)
            : new AssistantException(502, ErrorMessage, detail);
    }

    public static AssistantException BadPayload(string provider, string what, Exception? inner = null)
        => new(502, ErrorMessage, $"{provider}: {what}", inner: inner);

    public static Task DefaultDelay(TimeSpan t, CancellationToken ct) => Task.Delay(t, ct);

    internal static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    /// <summary>Errori che può sollevare la lettura di un JSON dall'aspetto diverso dal previsto.</summary>
    internal static bool IsParseError(Exception ex) =>
        ex is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException
            or FormatException or ArgumentException or OverflowException;

    private static TimeSpan? ParseRetryAfter(HttpResponseMessage resp)
    {
        var ra = resp.Headers.RetryAfter;
        if (ra is null) return null;
        if (ra.Delta is { } d) return d;
        if (ra.Date is { } at) return at - DateTimeOffset.UtcNow;
        return null;
    }
}
