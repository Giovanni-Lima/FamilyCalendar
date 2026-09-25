using FamilyCalendar.Data;
using FamilyCalendar.Filters;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FamilyCalendar.Controllers;

[ApiController]
[Route("api/labels")]
[TokenAuth]
public sealed class LabelsController : ControllerBase
{
    private readonly FamilyCalendarDbContext _db;

    public LabelsController(FamilyCalendarDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) =>
        Ok(await _db.Labels.OrderBy(l => l.Id).Select(l => new { l.Id, l.Name, l.Color }).ToListAsync(ct));
}
