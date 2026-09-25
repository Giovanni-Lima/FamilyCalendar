using FamilyCalendar.Filters;
using FamilyCalendar.Services;
using Microsoft.AspNetCore.Mvc;

namespace FamilyCalendar.Controllers;

[ApiController]
[Route("api/places")]
[TokenAuth]
public sealed class PlacesController : ControllerBase
{
    private readonly PlaceSearch _places;

    public PlacesController(PlaceSearch places) => _places = places;

    /// <summary>Suggerimenti di indirizzo per il testo digitato (minimo 3 caratteri).</summary>
    [HttpGet]
    public async Task<IActionResult> Search([FromQuery] string? q, CancellationToken ct)
    {
        q = q?.Trim();
        if (string.IsNullOrEmpty(q) || q.Length < 3 || q.Length > 200)
            return Ok(Array.Empty<PlaceSuggestion>());
        return Ok(await _places.SearchAsync(q, ct));
    }
}
