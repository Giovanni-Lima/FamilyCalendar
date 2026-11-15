using FamilyCalendar.Filters;
using FamilyCalendar.Services;
using FamilyCalendar.Services.Assistant;
using Microsoft.AspNetCore.Mvc;

namespace FamilyCalendar.Controllers;

/// <summary>
/// Assistente Aesir: risponde in italiano a domande sulle attività del calendario (RAG, vedi <see cref="AssistantService"/>).
/// Gli errori hanno SEMPRE corpo JSON <c>{"message":"..."}</c> (di proposito senza <c>[ApiController]</c>, così anche un
/// corpo mancante o malformato dà un 400 con lo stesso formato invece del ProblemDetails automatico).
/// </summary>
[Route("api/assistant")]
[TokenAuth]
public sealed class AssistantController : ControllerBase
{
    private readonly AssistantService _assistant;
    private readonly AssistantLimiter _limiter;
    private readonly AssistantOptions _options;

    public AssistantController(AssistantService assistant, AssistantLimiter limiter, AssistantOptions options)
    {
        _assistant = assistant;
        _limiter = limiter;
        _options = options;
    }

    /// <summary>Tetto del corpo della richiesta (una domanda è al massimo 500 caratteri).</summary>
    public const long MaxBodyBytes = 16_384;

    /// <summary>
    /// 200 risposta; 400 domanda non valida; 401 (filtro) token mancante/non valido; 429 limite raggiunto (Retry-After);
    /// 503 non configurato o servizio occupato/saturo (Retry-After tranne che per "non configurato"); 502 errore dei provider;
    /// 413 corpo oltre 16 KB (risposta del server, senza JSON).
    /// Il biglietto del limiter si rimborsa SOLO se non è partita la generazione (slot non ottenuto, richiesta annullata o
    /// embedding fallito) oppure se TUTTI i modelli di risposta hanno risposto 429; con altri errori della generazione
    /// (502, timeout, mix) la quota è consumata.
    /// </summary>
    [HttpPost("ask")]
    [RequestSizeLimit(MaxBodyBytes)]
    [ProducesResponseType(typeof(AssistantAnswerDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Ask([FromBody] AskRequest? request, CancellationToken ct)
    {
        if (AssistantService.ValidateQuestion(request?.Question) is { } invalid)
            return Error(StatusCodes.Status400BadRequest, invalid);

        if (!_assistant.IsConfigured)
            return Error(StatusCodes.Status503ServiceUnavailable, AssistantService.NotConfiguredMessage);

        var principal = HttpContext.Items[TokenAuthAttribute.PrincipalKey] as AuthService.Principal;
        if (!_limiter.TryAcquire(principal, out var ticket, out var denial))
        {
            Response.Headers.RetryAfter = RetryAfterSeconds(denial.RetryAfter);
            return Error(StatusCodes.Status429TooManyRequests, denial.Message);
        }

        // Il biglietto si restituisce solo se non è partita nessuna chiamata ai modelli di risposta; altrimenti chi
        // ripete domande che fanno fallire la generazione (risposta troncata/vuota/bloccata) consumerebbe quota senza contare.
        var progress = new AskProgress();
        var succeeded = false;
        var allQuota = false; // tutti i modelli hanno risposto subito 429: nessun token consumato
        try
        {
            using var slot = await _limiter.WaitSlotAsync(_options.SlotWait, ct);
            if (slot is null)
            {
                Response.Headers.RetryAfter = "5";
                return Error(StatusCodes.Status503ServiceUnavailable,
                    "L'assistente è occupato con altre domande: riprova tra qualche secondo.");
            }

            var answer = await _assistant.AskAsync(request!.Question, principal, ct, progress);
            succeeded = true;
            return Ok(answer);
        }
        catch (AssistantException ex)
        {
            allQuota = ex.IsQuota;
            if (ex.StatusCode == StatusCodes.Status503ServiceUnavailable)
                Response.Headers.RetryAfter = "30";
            return Error(ex.StatusCode, ex.Message);
        }
        finally
        {
            if (!succeeded && (!progress.GenerationStarted || allQuota))
                _limiter.Refund(ticket);
        }
    }

    private ObjectResult Error(int status, string message) => StatusCode(status, new { message });

    private static string RetryAfterSeconds(TimeSpan t) => Math.Max(1, (int)Math.Ceiling(t.TotalSeconds)).ToString();
}
