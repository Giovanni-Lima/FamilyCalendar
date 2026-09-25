using System.Globalization;
using System.Text.Json;

namespace FamilyCalendar.Services;

/// <summary>
/// Da indirizzo a coordinate con Nominatim (geocoder gratuito di OpenStreetMap, nessuna chiave).
/// Policy d'uso: ~1 richiesta/s e User-Agent identificativo. Lo chiamiamo solo quando l'indirizzo
/// cambia, quindi il traffico è minimo. Qualsiasi errore o indirizzo non trovato → <c>null</c>: l'attività
/// si salva comunque, solo senza mappa.
/// </summary>
public sealed class Geocoder
{
    private readonly HttpClient _http;
    private readonly ILogger<Geocoder> _log;

    public Geocoder(HttpClient http, ILogger<Geocoder> log)
    {
        _http = http;
        _log = log;
    }

    public async Task<(double Lat, double Lon)?> LookupAsync(string address, CancellationToken ct)
    {
        try
        {
            var url = $"search?format=jsonv2&limit=1&q={Uri.EscapeDataString(address)}";
            using var res = await _http.GetAsync(url, ct);
            if (!res.IsSuccessStatusCode)
                return null;

            using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
                return null;

            var first = doc.RootElement[0];
            if (double.TryParse(first.GetProperty("lat").GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
                && double.TryParse(first.GetProperty("lon").GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var lon))
                return (lat, lon);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            _log.LogWarning(ex, "Geocodifica fallita per \"{Address}\"", address);
            return null;
        }
    }
}
