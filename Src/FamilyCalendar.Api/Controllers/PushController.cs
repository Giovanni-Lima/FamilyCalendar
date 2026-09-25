using FamilyCalendar.Data;
using FamilyCalendar.Domain;
using FamilyCalendar.Filters;
using FamilyCalendar.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FamilyCalendar.Controllers;

[ApiController]
[Route("api/push")]
[TokenAuth]
public sealed class PushController : ControllerBase
{
    private readonly FamilyCalendarDbContext _db;
    private readonly PushSender _push;

    public PushController(FamilyCalendarDbContext db, PushSender push)
    {
        _db = db;
        _push = push;
    }

    public sealed record SubscribeRequest(string? Endpoint, string? P256dh, string? Auth);
    public sealed record UnsubscribeRequest(string? Endpoint);

    /// <summary>Chiave pubblica VAPID; 503 se le notifiche non sono configurate sul server.</summary>
    [HttpGet("key")]
    public IActionResult Key() =>
        _push.Enabled ? Ok(new { publicKey = _push.PublicKey }) : StatusCode(StatusCodes.Status503ServiceUnavailable);

    /// <summary>Indica se questo dispositivo (endpoint) è già sottoscritto.</summary>
    [HttpGet("status")]
    public async Task<IActionResult> Status([FromQuery] string endpoint, CancellationToken ct) =>
        Ok(new { subscribed = await _db.PushSubscriptions.AnyAsync(s => s.Endpoint == endpoint, ct) });

    [HttpPost("subscribe")]
    public async Task<IActionResult> Subscribe([FromBody] SubscribeRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Endpoint) || req.Endpoint.Length > 512
            || string.IsNullOrWhiteSpace(req.P256dh) || req.P256dh.Length > 200
            || string.IsNullOrWhiteSpace(req.Auth) || req.Auth.Length > 100
            || !Uri.TryCreate(req.Endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback)
            return BadRequest("Sottoscrizione non valida.");

        var memberId = (HttpContext.Items[TokenAuthAttribute.PrincipalKey] as AuthService.Principal)?.MemberId;
        var sub = await _db.PushSubscriptions.FirstOrDefaultAsync(s => s.Endpoint == req.Endpoint, ct);
        if (sub is null)
            _db.PushSubscriptions.Add(sub = new PushSubscription { Endpoint = req.Endpoint });

        sub.P256dh = req.P256dh;
        sub.Auth = req.Auth;
        sub.MemberId = memberId;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPost("unsubscribe")]
    public async Task<IActionResult> Unsubscribe([FromBody] UnsubscribeRequest req, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(req.Endpoint))
            await _db.PushSubscriptions.Where(s => s.Endpoint == req.Endpoint).ExecuteDeleteAsync(ct);
        return NoContent();
    }
}
