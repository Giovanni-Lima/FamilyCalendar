namespace FamilyCalendar.Domain;

/// <summary>Sottoscrizione Web Push di un dispositivo (una per endpoint del browser).</summary>
public class PushSubscription
{
    public int Id { get; set; }
    public int? MemberId { get; set; }
    public Member? Member { get; set; }

    public string Endpoint { get; set; } = null!;
    public string P256dh { get; set; } = null!;
    public string Auth { get; set; } = null!;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
