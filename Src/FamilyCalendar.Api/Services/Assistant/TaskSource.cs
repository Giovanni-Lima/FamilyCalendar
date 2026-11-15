using FamilyCalendar.Data;
using FamilyCalendar.Domain;
using Microsoft.EntityFrameworkCore;

namespace FamilyCalendar.Services.Assistant;

/// <summary>Lettura delle attività (con <c>Label</c> e <c>CreatedBy</c>) per l'indicizzazione e per il contesto dell'assistente.</summary>
public interface ITaskSource
{
    Task<TaskItem?> GetAsync(int id, CancellationToken ct);

    Task<IReadOnlyList<TaskItem>> GetAllAsync(CancellationToken ct);

    /// <summary>Attività NON completate con data in [<paramref name="from"/>, <paramref name="to"/>] (estremi inclusi), per data e ora (senza ora per prime), al massimo <paramref name="max"/>.</summary>
    Task<IReadOnlyList<TaskItem>> GetUpcomingAsync(DateOnly from, DateOnly to, int max, CancellationToken ct);
}

/// <inheritdoc cref="ITaskSource"/>
public sealed class EfTaskSource : ITaskSource
{
    private readonly FamilyCalendarDbContext _db;

    public EfTaskSource(FamilyCalendarDbContext db) => _db = db;

    private IQueryable<TaskItem> Query() => _db.Tasks.AsNoTracking().Include(t => t.Label).Include(t => t.CreatedBy);

    public Task<TaskItem?> GetAsync(int id, CancellationToken ct) => Query().FirstOrDefaultAsync(t => t.Id == id, ct);

    public async Task<IReadOnlyList<TaskItem>> GetAllAsync(CancellationToken ct) => await Query().ToListAsync(ct);

    public async Task<IReadOnlyList<TaskItem>> GetUpcomingAsync(DateOnly from, DateOnly to, int max, CancellationToken ct)
        => await Query()
            .Where(t => !t.Done && t.Date >= from && t.Date <= to)
            .OrderBy(t => t.Date).ThenBy(t => t.Time == null ? 0 : 1).ThenBy(t => t.Time).ThenBy(t => t.Id)
            .Take(max)
            .ToListAsync(ct);
}
