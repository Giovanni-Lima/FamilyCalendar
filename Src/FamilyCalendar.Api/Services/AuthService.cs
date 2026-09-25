using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FamilyCalendar.Domain;

namespace FamilyCalendar.Services;

/// <summary>
/// Login "casereccio" come ComitatoFeste: username = <c>iniziale.cognome</c> di un membro, password
/// condivisa dalla famiglia (più una password admin che eleva il ruolo, solo per chi è admin a DB).
/// Al successo emette un token firmato HMAC (<c>username|ruolo|memberId|scadenza</c>) da rimandare
/// come <c>Authorization: Bearer</c>. Tiene fuori chi capita per sbaglio, non è sicurezza forte.
/// </summary>
public sealed class AuthService
{
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromDays(30);

    private readonly string? _password;
    private readonly string? _adminPassword;
    private readonly byte[] _secret;

    public AuthService(IConfiguration config)
    {
        _password = Normalize(Environment.GetEnvironmentVariable("FAMILYCALENDAR_AUTH_PASSWORD") ?? config["Auth:Password"]);
        _adminPassword = Normalize(Environment.GetEnvironmentVariable("FAMILYCALENDAR_AUTH_PASSWORD_ADMIN") ?? config["Auth:PasswordAdmin"]);

        // Segreto fisso in produzione, altrimenti i login decadono a ogni riavvio.
        var configured = Environment.GetEnvironmentVariable("FAMILYCALENDAR_AUTH_SECRET") ?? config["Auth:Secret"];
        _secret = string.IsNullOrWhiteSpace(configured)
            ? RandomNumberGenerator.GetBytes(32)
            : Encoding.UTF8.GetBytes(configured);
    }

    private static string? Normalize(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>Se <c>false</c> il login è disattivato: API aperta, il frontend salta la schermata.</summary>
    public bool Enabled => _password is not null;

    public bool PasswordOk(string? password) =>
        !Enabled || string.Equals(password, _password, StringComparison.Ordinal);

    public bool AdminPasswordOk(string? password) =>
        _adminPassword is not null && string.Equals(password, _adminPassword, StringComparison.Ordinal);

    /// <summary><c>"Giovanni Lima"</c> → <c>"g.lima"</c>; spazi, apostrofi e accenti rimossi.</summary>
    public static string NormalizeUsername(string displayName)
    {
        var parts = displayName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return string.Empty;

        var initial = Strip(parts[0]);
        if (initial.Length == 0)
            return string.Empty;
        var surname = Strip(string.Concat(parts[1..]));
        return $"{initial[..1]}.{surname}".ToLowerInvariant();
    }

    private static string Strip(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;
            if (char.IsLetterOrDigit(c))
                sb.Append(c);
        }
        return sb.ToString();
    }

    public sealed record Principal(string Username, MemberRole Role, int MemberId);

    public string IssueToken(string username, MemberRole role, int memberId)
    {
        var exp = DateTimeOffset.UtcNow.Add(TokenLifetime).ToUnixTimeSeconds();
        var payload = $"{username}|{role.ToString().ToLowerInvariant()}|{memberId}|{exp}";
        return $"{B64(Encoding.UTF8.GetBytes(payload))}.{B64(Sign(payload))}";
    }

    public Principal? ValidatePrincipal(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;

        var dot = token.IndexOf('.');
        if (dot <= 0 || dot == token.Length - 1)
            return null;

        try
        {
            var payload = Encoding.UTF8.GetString(UnB64(token[..dot]));
            var sig = UnB64(token[(dot + 1)..]);
            if (!CryptographicOperations.FixedTimeEquals(sig, Sign(payload)))
                return null;

            var parts = payload.Split('|');
            if (parts.Length != 4)
                return null;

            var exp = long.Parse(parts[3], CultureInfo.InvariantCulture);
            if (DateTimeOffset.FromUnixTimeSeconds(exp) < DateTimeOffset.UtcNow)
                return null;

            if (!Enum.TryParse<MemberRole>(parts[1], true, out var role))
                return null;

            return new Principal(parts[0], role, int.Parse(parts[2], CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    private byte[] Sign(string payload)
    {
        using var hmac = new HMACSHA256(_secret);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
    }

    private static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] UnB64(string s)
    {
        var t = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(t.PadRight((t.Length + 3) / 4 * 4, '='));
    }
}
