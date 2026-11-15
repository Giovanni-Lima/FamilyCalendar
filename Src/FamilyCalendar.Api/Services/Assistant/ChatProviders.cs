using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace FamilyCalendar.Services.Assistant;

/// <summary>Un modello di chat (una voce della catena di generazione). Solleva solo <see cref="AssistantException"/> (o la cancellazione del chiamante).</summary>
public interface IChatProvider
{
    /// <summary>Nome del provider per i log ("Gemini", "Groq").</summary>
    string Name { get; }

    /// <summary>Identificativo del modello (es. <c>gemini-3.5-flash-lite</c>).</summary>
    string Model { get; }

    Task<string> GenerateAsync(string system, string user, CancellationToken ct);
}

/// <summary>
/// Gemini <c>generateContent</c> con <c>systemInstruction</c>, temperatura 0.2. Esclude le parti "thought", gestisce
/// <c>blockReason</c> e <c>finishReason</c> MAX_TOKENS. Un solo tentativo: se fallisce la catena passa al modello successivo.
/// </summary>
public sealed class GeminiChatProvider : IChatProvider
{
    public const string HttpClientName = "gemini-chat";
    private const int MaxOutputTokens = 4096; // i token di "thinking" contano nel budget di uscita

    private readonly IHttpClientFactory _factory;
    private readonly string _apiKey;

    public GeminiChatProvider(IHttpClientFactory factory, string apiKey, string model)
    {
        _factory = factory;
        _apiKey = apiKey;
        Model = model;
    }

    public string Name => "Gemini";
    public string Model { get; }

    public async Task<string> GenerateAsync(string system, string user, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(new
        {
            systemInstruction = new { parts = new[] { new { text = system } } },
            contents = new[] { new { role = "user", parts = new[] { new { text = user } } } },
            generationConfig = new { maxOutputTokens = MaxOutputTokens, temperature = 0.2 },
        });
        var label = $"Gemini {Model}";
        var http = _factory.CreateClient(HttpClientName);

        var resp = await ProviderHttp.SendWithRetryAsync(http, () =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post,
                $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(Model)}:generateContent")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            // La chiave viaggia nell'header, mai nell'URL: così non finisce nei log/eccezioni.
            req.Headers.Add("x-goog-api-key", _apiKey);
            return req;
        }, label, maxAttempts: 1, maxWait: TimeSpan.Zero, ProviderHttp.DefaultDelay, ct);

        return Parse(resp.Body, label);
    }

    internal static string Parse(string body, string label)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (!root.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
            {
                var reason = root.TryGetProperty("promptFeedback", out var fb) && fb.TryGetProperty("blockReason", out var br)
                    ? br.GetString() : "nessun candidato";
                throw ProviderHttp.BadPayload(label, $"risposta assente ({reason})");
            }

            var candidate = candidates[0];
            var finish = candidate.TryGetProperty("finishReason", out var fr) ? fr.GetString() : null;

            var text = new StringBuilder();
            if (candidate.TryGetProperty("content", out var content) && content.TryGetProperty("parts", out var parts))
            {
                foreach (var p in parts.EnumerateArray())
                {
                    if (p.TryGetProperty("thought", out var t) && t.ValueKind == JsonValueKind.True) continue;
                    if (p.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String)
                        text.Append(tx.GetString());
                }
            }

            if (finish == "MAX_TOKENS")
                throw ProviderHttp.BadPayload(label, "risposta troncata (max token raggiunto)");
            if (string.IsNullOrWhiteSpace(text.ToString()))
                throw ProviderHttp.BadPayload(label, $"risposta vuota (finishReason={finish})");
            return text.ToString().Trim();
        }
        catch (Exception ex) when (ProviderHttp.IsParseError(ex))
        {
            throw ProviderHttp.BadPayload(label, $"risposta non valida ({ex.GetType().Name}: {ex.Message})", ex);
        }
    }
}

/// <summary>Groq (API compatibile OpenAI): <c>reasoning_effort</c> "low", <c>max_completion_tokens</c>, retry su 429/5xx.</summary>
public sealed class GroqChatProvider : IChatProvider
{
    public const string HttpClientName = "groq";
    public const string PrimaryModel = "openai/gpt-oss-120b";
    public const string FallbackModel = "openai/gpt-oss-20b";
    private const string Url = "https://api.groq.com/openai/v1/chat/completions";
    private const int MaxCompletionTokens = 2000; // il ragionamento di gpt-oss conta nel budget

    private readonly IHttpClientFactory _factory;
    private readonly string _apiKey;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public GroqChatProvider(IHttpClientFactory factory, string apiKey, string model, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _factory = factory;
        _apiKey = apiKey;
        Model = model;
        _delay = delay ?? ProviderHttp.DefaultDelay;
    }

    public string Name => "Groq";
    public string Model { get; }

    public async Task<string> GenerateAsync(string system, string user, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(new
        {
            model = Model,
            temperature = 0.2,
            reasoning_effort = "low",
            max_completion_tokens = MaxCompletionTokens,
            messages = new[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user },
            },
        });
        var label = $"Groq {Model}";
        var http = _factory.CreateClient(HttpClientName);

        // Un solo ritentativo con attesa breve: se il modello è saturo meglio passare al successivo che far attendere.
        var resp = await ProviderHttp.SendWithRetryAsync(http, () =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, Url)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            return req;
        }, label, maxAttempts: 2, maxWait: TimeSpan.FromSeconds(5), _delay, ct);

        return Parse(resp.Body, label);
    }

    internal static string Parse(string body, string label)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var choice = doc.RootElement.GetProperty("choices")[0];
            var finish = choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String ? fr.GetString() : null;
            var content = choice.GetProperty("message").TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString() : null;

            if (finish == "length")
                throw ProviderHttp.BadPayload(label, "risposta troncata (max token raggiunto)");
            if (string.IsNullOrWhiteSpace(content))
                throw ProviderHttp.BadPayload(label, $"risposta vuota (finish_reason={finish})");
            return content.Trim();
        }
        catch (Exception ex) when (ProviderHttp.IsParseError(ex))
        {
            throw ProviderHttp.BadPayload(label, $"risposta non valida ({ex.GetType().Name}: {ex.Message})", ex);
        }
    }
}
