using FamilyCalendar.Data;
using FamilyCalendar.Domain;
using FamilyCalendar.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FamilyCalendar.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly FamilyCalendarDbContext _db;
    private readonly AuthService _auth;

    public AuthController(FamilyCalendarDbContext db, AuthService auth)
    {
        _db = db;
        _auth = auth;
    }

    public sealed record LoginRequest(string? Username, string? Password);
    public sealed record LoginResponse(string Token, string Username, int MemberId, string DisplayName, string Role);

    /// <summary>Il frontend lo chiama all'avvio: se <c>enabled</c> è false salta la schermata di login.</summary>
    [HttpGet("status")]
    public IActionResult Status() => Ok(new { enabled = _auth.Enabled });

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest req, CancellationToken ct)
    {
        var username = (req.Username ?? string.Empty).Trim().ToLowerInvariant();
        if (username.Length == 0)
            return BadRequest("Username mancante.");

        var members = await _db.Members.ToListAsync(ct);
        var match = members.FirstOrDefault(m =>
            string.Equals(AuthService.NormalizeUsername(m.DisplayName), username, StringComparison.Ordinal));
        if (match is null)
            return Unauthorized("Credenziali non valide.");

        MemberRole role;
        if (_auth.AdminPasswordOk(req.Password))
        {
            if (match.Role != MemberRole.Amministratore)
                return Unauthorized("Credenziali non valide.");
            role = MemberRole.Amministratore;
        }
        else if (_auth.PasswordOk(req.Password))
        {
            role = MemberRole.Membro;
        }
        else
        {
            return Unauthorized("Credenziali non valide.");
        }

        return Ok(new LoginResponse(_auth.IssueToken(username, role, match.Id), username, match.Id,
            match.DisplayName, role.ToString().ToLowerInvariant()));
    }
}
