using FamilyCalendar.Domain;
using FamilyCalendar.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace FamilyCalendar.Filters;

/// <summary>
/// Richiede un token valido in <c>Authorization: Bearer</c>. Se il login è disattivato lascia passare
/// tutto. Il principal è disponibile in <c>HttpContext.Items["principal"]</c>.
/// </summary>
public sealed class TokenAuthAttribute : Attribute, IAuthorizationFilter
{
    public const string PrincipalKey = "principal";

    private readonly MemberRole? _requiredRole;

    public TokenAuthAttribute() { }

    public TokenAuthAttribute(MemberRole requiredRole) => _requiredRole = requiredRole;

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var auth = context.HttpContext.RequestServices.GetRequiredService<AuthService>();
        if (!auth.Enabled)
            return;

        var header = context.HttpContext.Request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer ".Length..].Trim()
            : null;

        var principal = auth.ValidatePrincipal(token);
        if (principal is null)
        {
            context.Result = new UnauthorizedObjectResult("Autenticazione richiesta.");
            return;
        }

        if (_requiredRole is { } required && principal.Role < required)
        {
            context.Result = new ObjectResult("Permessi insufficienti.") { StatusCode = StatusCodes.Status403Forbidden };
            return;
        }

        context.HttpContext.Items[PrincipalKey] = principal;
    }
}
