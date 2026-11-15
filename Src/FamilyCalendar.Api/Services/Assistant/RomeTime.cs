using System.Globalization;

namespace FamilyCalendar.Services.Assistant;

/// <summary>Fuso Europe/Rome e nomi italiani di giorni/mesi (senza dipendere dalla cultura/ICU del sistema).</summary>
public static class RomeTime
{
    private static readonly string[] Days = { "domenica", "lunedì", "martedì", "mercoledì", "giovedì", "venerdì", "sabato" };

    private static readonly string[] Months =
    {
        "gennaio", "febbraio", "marzo", "aprile", "maggio", "giugno",
        "luglio", "agosto", "settembre", "ottobre", "novembre", "dicembre",
    };

    /// <summary>Id IANA su Linux/macOS, con fallback all'id Windows; in assenza di dati di fuso si ripiega su UTC.</summary>
    public static TimeZoneInfo Zone { get; } = Resolve();

    /// <summary>True se né "Europe/Rome" né "W. Europe Standard Time" esistono (es. container senza tzdata): date e orari sono in UTC.</summary>
    public static bool IsFallbackToUtc => ReferenceEquals(Zone, TimeZoneInfo.Utc);

    private static TimeZoneInfo Resolve()
    {
        foreach (var id in new[] { "Europe/Rome", "W. Europe Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        return TimeZoneInfo.Utc;
    }

    /// <summary>Data odierna in fuso Roma.</summary>
    public static DateOnly Today(TimeProvider clock) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), Zone).DateTime);

    /// <summary>Data e ora correnti in fuso Roma.</summary>
    public static DateTime Now(TimeProvider clock) => TimeZoneInfo.ConvertTime(clock.GetUtcNow(), Zone).DateTime;

    public static string DayName(DateOnly d) => Days[(int)d.DayOfWeek];

    /// <summary>"giovedì 15 novembre 2026".</summary>
    public static string LongDate(DateOnly d) =>
        string.Create(CultureInfo.InvariantCulture, $"{DayName(d)} {d.Day} {Months[d.Month - 1]} {d.Year}");

    public static string IsoDate(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string Hhmm(TimeOnly t) => t.ToString("HH:mm", CultureInfo.InvariantCulture);
}
