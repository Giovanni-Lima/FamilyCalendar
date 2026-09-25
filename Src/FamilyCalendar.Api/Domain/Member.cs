namespace FamilyCalendar.Domain;

public enum MemberRole
{
    Membro = 0,
    Amministratore = 1,
}

/// <summary>Un componente della famiglia. Lo username di login deriva dal <see cref="DisplayName"/> (<c>g.lima</c>).</summary>
public class Member
{
    public int Id { get; set; }
    public string DisplayName { get; set; } = null!;
    public MemberRole Role { get; set; } = MemberRole.Membro;
}
