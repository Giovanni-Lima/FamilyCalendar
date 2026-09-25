using System.Globalization;
using FamilyCalendar.Data;
using FamilyCalendar.Domain;
using FamilyCalendar.Filters;
using FamilyCalendar.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FamilyCalendar.Controllers;

[ApiController]
[Route("api/tasks")]
[TokenAuth]
public sealed class TasksController : ControllerBase
{
    private readonly FamilyCalendarDbContext _db;

    private readonly PushSender _push;
    private readonly Geocoder _geocoder;

    public TasksController(FamilyCalendarDbContext db, PushSender push, Geocoder geocoder)
    {
        _db = db;
        _push = push;
        _geocoder = geocoder;
    }

    /// <summary>Date in formato <c>yyyy-MM-dd</c>, Time in formato <c>HH:mm</c> (o vuoto).</summary>
    public sealed record TaskRequest(string? Title, string? Date, string? Time, string? Note, int? LabelId, string? Address,
        double? Latitude = null, double? Longitude = null);

    public sealed record TaskDto(int Id, string Title, string Date, string? Time, string? Note, bool Done,
        int? LabelId, string? LabelName, string? LabelColor, string? CreatedBy,
        string? Address, double? Latitude, double? Longitude);

    private static TaskDto ToDto(TaskItem t) => new(
        t.Id, t.Title, t.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        t.Time?.ToString("HH:mm", CultureInfo.InvariantCulture), t.Note, t.Done,
        t.LabelId, t.Label?.Name, t.Label?.Color, t.CreatedBy?.DisplayName,
        t.Address, t.Latitude, t.Longitude);

    /// <summary>Se l'indirizzo è cambiato rispetto a <paramref name="previous"/>, ricalcola (o azzera) le coordinate.</summary>
    private async Task GeocodeIfChangedAsync(TaskItem task, TaskRequest req, string? previous, CancellationToken ct)
    {
        // Luogo scelto dai suggerimenti: il client manda già le coordinate esatte, niente geocodifica.
        if (task.Address is not null && req.Latitude is >= -90 and <= 90 && req.Longitude is >= -180 and <= 180)
        {
            task.Latitude = req.Latitude;
            task.Longitude = req.Longitude;
            return;
        }

        if (string.Equals(task.Address, previous, StringComparison.Ordinal))
            return;

        task.Latitude = task.Longitude = null;
        if (task.Address is null)
            return;

        if (await _geocoder.LookupAsync(task.Address, ct) is { } pos)
        {
            task.Latitude = pos.Lat;
            task.Longitude = pos.Lon;
        }
    }

    private static string Where(TaskDto dto) => dto.Address is null ? "" : $" — {dto.Address}";

    /// <summary>Attività nell'intervallo [from, to] (inclusi), ordinate per giorno e ora.</summary>
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? from, [FromQuery] string? to, CancellationToken ct)
    {
        if (!TryDate(from, out var f) || !TryDate(to, out var t))
            return BadRequest("Parametri from/to non validi (yyyy-MM-dd).");
        if (t < f || t.DayNumber - f.DayNumber > 62)
            return BadRequest("Intervallo non valido (massimo 62 giorni).");

        var items = await _db.Tasks.Include(x => x.Label).Include(x => x.CreatedBy)
            .Where(x => x.Date >= f && x.Date <= t)
            .OrderBy(x => x.Date).ThenBy(x => x.Time == null).ThenBy(x => x.Time).ThenBy(x => x.Id)
            .ToListAsync(ct);
        return Ok(items.Select(ToDto));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] TaskRequest req, CancellationToken ct)
    {
        var task = new TaskItem();
        if (!Apply(task, req, out var error))
            return BadRequest(error);
        if (task.LabelId is { } lid && !await _db.Labels.AnyAsync(l => l.Id == lid, ct))
            return BadRequest("Etichetta inesistente.");

        await GeocodeIfChangedAsync(task, req, null, ct);
        task.CreatedByMemberId = (HttpContext.Items[TokenAuthAttribute.PrincipalKey] as AuthService.Principal)?.MemberId;
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync(ct);

        var dto = await Load(task.Id, ct);
        _ = _push.NotifyAllAsync("Nuova attività",
            $"{dto.CreatedBy ?? "Qualcuno"}: {dto.Title} ({When(task, dto)}){Where(dto)}", task.CreatedByMemberId);
        return Ok(dto);
    }

    private static string When(TaskItem task, TaskDto dto) =>
        task.Date.ToString("dd/MM", CultureInfo.InvariantCulture) + (dto.Time is null ? "" : $" {dto.Time}");

    private async Task<string> EditorName(int memberId, CancellationToken ct) =>
        await _db.Members.Where(m => m.Id == memberId).Select(m => m.DisplayName).FirstOrDefaultAsync(ct) ?? "Qualcuno";

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] TaskRequest req, CancellationToken ct)
    {
        var task = await _db.Tasks.FindAsync(new object[] { id }, ct);
        if (task is null)
            return NotFound();
        var previousAddress = task.Address;
        if (!Apply(task, req, out var error))
            return BadRequest(error);
        if (task.LabelId is { } lid && !await _db.Labels.AnyAsync(l => l.Id == lid, ct))
            return BadRequest("Etichetta inesistente.");

        await GeocodeIfChangedAsync(task, req, previousAddress, ct);
        await _db.SaveChangesAsync(ct);

        var dto = await Load(id, ct);
        var editor = HttpContext.Items[TokenAuthAttribute.PrincipalKey] as AuthService.Principal;
        var editorName = editor is null ? "Qualcuno" : await EditorName(editor.MemberId, ct);
        _ = _push.NotifyAllAsync("Attività modificata",
            $"{editorName}: {dto.Title} ({When(task, dto)}){Where(dto)}", editor?.MemberId);
        return Ok(dto);
    }

    [HttpPost("{id:int}/toggle")]
    public async Task<IActionResult> Toggle(int id, CancellationToken ct)
    {
        var task = await _db.Tasks.FindAsync(new object[] { id }, ct);
        if (task is null)
            return NotFound();
        task.Done = !task.Done;
        await _db.SaveChangesAsync(ct);
        return Ok(await Load(id, ct));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var n = await _db.Tasks.Where(x => x.Id == id).ExecuteDeleteAsync(ct);
        return n == 0 ? NotFound() : NoContent();
    }

    private async Task<TaskDto> Load(int id, CancellationToken ct) =>
        ToDto(await _db.Tasks.Include(x => x.Label).Include(x => x.CreatedBy).SingleAsync(x => x.Id == id, ct));

    private static bool TryDate(string? s, out DateOnly d) =>
        DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d);

    private static bool Apply(TaskItem task, TaskRequest req, out string error)
    {
        error = "";
        var title = (req.Title ?? "").Trim();
        if (title.Length == 0 || title.Length > 200)
        {
            error = "Titolo obbligatorio (max 200 caratteri).";
            return false;
        }
        if (!TryDate(req.Date, out var date))
        {
            error = "Data non valida.";
            return false;
        }

        TimeOnly? time = null;
        if (!string.IsNullOrWhiteSpace(req.Time))
        {
            if (!TimeOnly.TryParseExact(req.Time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
            {
                error = "Ora non valida.";
                return false;
            }
            time = t;
        }

        var note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim();
        if (note is { Length: > 1000 })
        {
            error = "Nota troppo lunga (max 1000 caratteri).";
            return false;
        }

        var address = string.IsNullOrWhiteSpace(req.Address) ? null : req.Address.Trim();
        if (address is { Length: > 300 })
        {
            error = "Indirizzo troppo lungo (max 300 caratteri).";
            return false;
        }

        task.Address = address;
        task.Title = title;
        task.Date = date;
        task.Time = time;
        task.Note = note;
        task.LabelId = req.LabelId;
        return true;
    }
}
