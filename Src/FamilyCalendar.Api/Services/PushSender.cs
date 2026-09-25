using System.Net;
using System.Text.Json;
using FamilyCalendar.Data;
using Microsoft.EntityFrameworkCore;
using WebPush;

namespace FamilyCalendar.Services;

/// <summary>
/// Web Push (VAPID). Chiavi da env <c>FAMILYCALENDAR_VAPID_PUBLIC/PRIVATE/SUBJECT</c> (o config <c>Vapid:*</c>);
/// senza chiavi <see cref="Enabled"/> è false e il frontend segnala che le notifiche non sono disponibili.
/// Genera una coppia con: <c>npx web-push generate-vapid-keys</c>.
/// </summary>
public sealed class PushSender
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<PushSender> _log;
    private readonly WebPushClient _client = new();
    private readonly VapidDetails? _vapid;

    public PushSender(IConfiguration config, IServiceScopeFactory scopes, ILogger<PushSender> log)
    {
        _scopes = scopes;
        _log = log;

        var pub = Environment.GetEnvironmentVariable("FAMILYCALENDAR_VAPID_PUBLIC") ?? config["Vapid:Public"];
        var priv = Environment.GetEnvironmentVariable("FAMILYCALENDAR_VAPID_PRIVATE") ?? config["Vapid:Private"];
        var subject = Environment.GetEnvironmentVariable("FAMILYCALENDAR_VAPID_SUBJECT") ?? config["Vapid:Subject"]
                      ?? "mailto:admin@example.com";
        if (!string.IsNullOrWhiteSpace(pub) && !string.IsNullOrWhiteSpace(priv))
            _vapid = new VapidDetails(subject, pub.Trim(), priv.Trim());
    }

    public bool Enabled => _vapid is not null;
    public string? PublicKey => _vapid?.PublicKey;

    /// <summary>Invia la notifica a tutti i dispositivi sottoscritti tranne quelli di <paramref name="excludeMemberId"/>.
    /// Pensata per essere lanciata senza attendere (fire-and-forget): non solleva eccezioni.</summary>
    public async Task NotifyAllAsync(string title, string body, int? excludeMemberId = null)
    {
        if (_vapid is null)
            return;

        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FamilyCalendarDbContext>();
            var subs = await db.PushSubscriptions
                .Where(s => excludeMemberId == null || s.MemberId != excludeMemberId)
                .ToListAsync();

            var payload = JsonSerializer.Serialize(new { title, body, url = "/" });
            var dead = new List<int>();

            foreach (var s in subs)
            {
                try
                {
                    await _client.SendNotificationAsync(new WebPush.PushSubscription(s.Endpoint, s.P256dh, s.Auth), payload, _vapid);
                }
                catch (WebPushException ex) when (ex.StatusCode is HttpStatusCode.Gone or HttpStatusCode.NotFound)
                {
                    dead.Add(s.Id);   // sottoscrizione scaduta/revocata dal browser
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Invio push fallito per la sottoscrizione {Id}", s.Id);
                }
            }

            if (dead.Count > 0)
                await db.PushSubscriptions.Where(s => dead.Contains(s.Id)).ExecuteDeleteAsync();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Invio push fallito");
        }
    }
}
