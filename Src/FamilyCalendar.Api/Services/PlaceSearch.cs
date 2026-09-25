using System.Text.Json;

namespace FamilyCalendar.Services;

/// <summary>Un suggerimento di luogo: <see cref="Label"/> è il testo da salvare, <see cref="Detail"/> la riga secondaria.</summary>
public sealed record PlaceSuggestion(string Label, string Detail, double Lat, double Lon);

/// <summary>
/// Autocompletamento indirizzi con Photon (geocoder gratuito basato su OpenStreetMap, pensato per
/// il "cerca mentre scrivi": Nominatim invece lo vieta). Nessuna chiave. Risultati limitati all'Italia.
/// Per passare a Google Places basta riscrivere questa classe: il resto dell'app dipende solo da
/// <see cref="SearchAsync"/>.
/// </summary>
public sealed class PlaceSearch
{
    // Rettangolo dell'Italia (minLon,minLat,maxLon,maxLat): evita omonimi all'estero.
    private const string ItalyBbox = "6.6,35.5,18.8,47.1";

    private readonly HttpClient _http;
    private readonly ILogger<PlaceSearch> _log;

    public PlaceSearch(HttpClient http, ILogger<PlaceSearch> log)
    {
        _http = http;
        _log = log;
    }

    public async Task<IReadOnlyList<PlaceSuggestion>> SearchAsync(string query, CancellationToken ct)
    {
        try
        {
            var url = $"api/?q={Uri.EscapeDataString(query)}&limit=8&bbox={ItalyBbox}";
            using var res = await _http.GetAsync(url, ct);
            if (!res.IsSuccessStatusCode)
                return Array.Empty<PlaceSuggestion>();

            using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var result = new List<PlaceSuggestion>();
            var seen = new HashSet<string>();

            foreach (var f in doc.RootElement.GetProperty("features").EnumerateArray())
            {
                var p = f.GetProperty("properties");
                var coords = f.GetProperty("geometry").GetProperty("coordinates");
                var name = Str(p, "name");
                var street = Str(p, "street");
                var number = Str(p, "housenumber");
                var city = Str(p, "city") ?? Str(p, "town") ?? Str(p, "village") ?? Str(p, "locality") ?? Str(p, "district");
                var county = Str(p, "county");
                var postcode = Str(p, "postcode");

                // Riga principale: "Via Roma 12" (indirizzo) oppure il nome del luogo ("Stadio San Siro").
                var main = street is not null
                    ? (number is null ? street : $"{street} {number}")
                    : name;
                if (main is null)
                    continue;
                if (name is not null && street is not null && name != street)
                    main = $"{name}, {main}";   // es. "Bar Sport, Via Roma 12"

                var place = string.Join(" ", new[] { postcode, city }.Where(x => x is not null));
                var detail = string.Join(", ", new[] { place.Length == 0 ? null : place, county != city ? county : null }
                    .Where(x => !string.IsNullOrEmpty(x)));
                var label = detail.Length == 0 ? main : $"{main}, {(place.Length == 0 ? county : place)}";

                if (!seen.Add(label))
                    continue;   // Photon a volte restituisce la stessa via due volte
                result.Add(new PlaceSuggestion(label, detail, coords[1].GetDouble(), coords[0].GetDouble()));
            }
            return result;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            _log.LogWarning(ex, "Ricerca luoghi fallita per \"{Query}\"", query);
            return Array.Empty<PlaceSuggestion>();
        }
    }

    private static string? Str(JsonElement p, string name) =>
        p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
}
