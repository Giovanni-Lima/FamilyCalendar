namespace FamilyCalendar.Domain;

/// <summary>Etichetta colorata di un'attività (es. Famiglia, Scuola). <see cref="Color"/> è un esadecimale <c>#rrggbb</c>.</summary>
public class Label
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
    public string Color { get; set; } = null!;
}
