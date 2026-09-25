using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace FamilyCalendar.Data;

/// <summary>Solo per <c>dotnet ef</c> (design time): la ServerVersion è fissa, così non serve un DB raggiungibile.</summary>
public class FamilyCalendarDbContextFactory : IDesignTimeDbContextFactory<FamilyCalendarDbContext>
{
    public FamilyCalendarDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("FAMILYCALENDAR_CONNECTION")
                 ?? "Server=localhost;Port=3306;Database=familycalendar;User=root;Password=root";
        var options = new DbContextOptionsBuilder<FamilyCalendarDbContext>()
            .UseMySql(cs, new MySqlServerVersion(new Version(8, 0, 36)))
            .Options;
        return new FamilyCalendarDbContext(options);
    }
}
