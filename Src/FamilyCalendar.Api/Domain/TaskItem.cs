namespace FamilyCalendar.Domain;

/// <summary>Attività del calendario familiare, condivisa da tutti i membri.</summary>
public class TaskItem
{
    public int Id { get; set; }
    public string Title { get; set; } = null!;
    public DateOnly Date { get; set; }
    public TimeOnly? Time { get; set; }
    public string? Note { get; set; }
    public bool Done { get; set; }

    /// <summary>Luogo dell'attività (testo libero) e, se la geocodifica è riuscita, le sue coordinate.</summary>
    public string? Address { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }

    public int? LabelId { get; set; }
    public Label? Label { get; set; }

    public int? CreatedByMemberId { get; set; }
    public Member? CreatedBy { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
