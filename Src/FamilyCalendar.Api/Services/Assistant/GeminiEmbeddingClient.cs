using System.Text;
using System.Text.Json;

namespace FamilyCalendar.Services.Assistant;

/// <summary>
/// Embedding con Gemini (<c>gemini-embedding-2</c>, 768 dimensioni, vettori L2-normalizzati). Il "tipo di task" non è
/// un parametro ma un prefisso nel testo: documenti <c>title: none | text: ...</c>, domande <c>task: search result | query: ...</c>.
/// La chiave viaggia solo nell'header <c>x-goog-api-key</c>, mai nell'URL.
/// </summary>
public sealed class GeminiEmbeddingClient : IEmbeddingClient
{
    public const string Model = "gemini-embedding-2";
    public const int Dimensions = 768;
    /// <summary>HttpClient per le domande (timeout breve, 8 s per tentativo).</summary>
    public const string HttpClientName = "gemini-embed";

    /// <summary>HttpClient per i batch di documenti dell'indicizzazione in background (timeout più lungo).</summary>
    public const string BatchHttpClientName = "gemini-embed-batch";

    private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta/models/" + Model;
    private const string Provider = "Gemini embedding";

    private readonly IHttpClientFactory _factory;
    private readonly string? _apiKey;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public GeminiEmbeddingClient(IHttpClientFactory factory, AssistantKeys keys, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _factory = factory;
        _apiKey = keys.Gemini;
        _delay = delay ?? ProviderHttp.DefaultDelay;
    }

    public bool IsConfigured => _apiKey is not null;
    public string ModelName => Model;

    public static string DocumentInput(string text) => $"title: none | text: {text}";
    public static string QueryInput(string question) => $"task: search result | query: {question}";

    public async Task<float[]> EmbedQueryAsync(string question, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(new
        {
            content = new { parts = new[] { new { text = QueryInput(question) } } },
            output_dimensionality = Dimensions,
        });

        // Domanda interattiva: un solo ritentativo, con attesa breve.
        ProviderResponse resp;
        try
        {
            resp = await PostAsync(HttpClientName, $"{BaseUrl}:embedContent", json, maxAttempts: 2, maxWait: TimeSpan.FromSeconds(3), ct);
        }
        catch (AssistantException ex) when (ex.IsTimeout)
        {
            // La domanda e' interattiva: il frontend riconosce il messaggio "troppo tempo" (stesso del deadline e della catena).
            throw new AssistantException(503, ProviderHttp.TooSlowMessage, ex.Detail, isTimeout: true, inner: ex);
        }
        try
        {
            using var doc = JsonDocument.Parse(resp.Body);
            return ReadVector(doc.RootElement.GetProperty("embedding"));
        }
        catch (Exception ex) when (ProviderHttp.IsParseError(ex))
        {
            throw ProviderHttp.BadPayload(Provider, $"risposta non valida ({ex.GetType().Name}: {ex.Message})", ex);
        }
    }

    public async Task<float[][]> EmbedDocumentsAsync(IReadOnlyList<string> documents, CancellationToken ct)
    {
        if (documents.Count == 0)
            return Array.Empty<float[]>();

        var json = JsonSerializer.Serialize(new
        {
            requests = documents.Select(d => new
            {
                model = $"models/{Model}",
                content = new { parts = new[] { new { text = DocumentInput(d) } } },
                output_dimensionality = Dimensions,
            }).ToArray(),
        });

        // Indicizzazione in background: nessuno aspetta, backoff più generoso.
        var resp = await PostAsync(BatchHttpClientName, $"{BaseUrl}:batchEmbedContents", json, maxAttempts: 4, maxWait: TimeSpan.FromSeconds(60), ct);
        try
        {
            using var doc = JsonDocument.Parse(resp.Body);
            var items = doc.RootElement.GetProperty("embeddings");
            if (items.GetArrayLength() != documents.Count)
                throw new FormatException($"{items.GetArrayLength()} embedding per {documents.Count} testi.");
            return items.EnumerateArray().Select(ReadVector).ToArray();
        }
        catch (Exception ex) when (ProviderHttp.IsParseError(ex))
        {
            throw ProviderHttp.BadPayload(Provider, $"risposta non valida ({ex.GetType().Name}: {ex.Message})", ex);
        }
    }

    private Task<ProviderResponse> PostAsync(string clientName, string url, string json, int maxAttempts, TimeSpan maxWait, CancellationToken ct)
    {
        var http = _factory.CreateClient(clientName);
        return ProviderHttp.SendWithRetryAsync(http, () =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            req.Headers.Add("x-goog-api-key", _apiKey);
            return req;
        }, Provider, maxAttempts, maxWait, _delay, ct);
    }

    /// <summary>Legge <c>values</c>, controlla la dimensione e normalizza L2.</summary>
    internal static float[] ReadVector(JsonElement embedding)
    {
        var values = embedding.GetProperty("values").EnumerateArray().Select(v => v.GetSingle()).ToArray();
        if (values.Length != Dimensions)
            throw new FormatException($"Attesi {Dimensions} valori, ricevuti {values.Length}.");
        return Normalize(values);
    }

    /// <summary>Normalizzazione L2 (copia). Un vettore nullo o con valori non finiti è un payload non valido.</summary>
    internal static float[] Normalize(float[] values)
    {
        double sum = 0;
        foreach (var x in values) sum += (double)x * x;
        if (sum == 0 || double.IsNaN(sum) || double.IsInfinity(sum))
            throw new FormatException("Vettore nullo o non finito.");
        var inv = 1.0 / Math.Sqrt(sum);
        var r = new float[values.Length];
        for (var i = 0; i < r.Length; i++) r[i] = (float)(values[i] * inv);
        return r;
    }
}
