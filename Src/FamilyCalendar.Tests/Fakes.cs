using System.Net;
using System.Text;
using FamilyCalendar.Domain;
using FamilyCalendar.Services.Assistant;
using Microsoft.Extensions.Logging.Abstractions;

namespace FamilyCalendar.Tests;

/// <summary>Orologio controllabile.</summary>
public sealed class FakeClock : TimeProvider
{
    private DateTimeOffset _now;

    public FakeClock(DateTimeOffset now) => _now = now;

    public void Advance(TimeSpan by) => _now += by;

    public override DateTimeOffset GetUtcNow() => _now;
}

/// <summary>Handler HTTP che risponde con una funzione e registra le richieste.</summary>
public sealed class FakeHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, int, HttpResponseMessage> _respond;
    public List<HttpRequestMessage> Requests { get; } = new();
    public List<string> Bodies { get; } = new();

    public FakeHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) => _respond = respond;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
        return _respond(request, Requests.Count - 1);
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

public sealed class FakeHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;

    public FakeHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

    public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
}

public sealed class FakeEmbeddingClient : IEmbeddingClient
{
    public bool IsConfigured { get; set; } = true;
    public string ModelName => "test-embed";
    public List<string> Queries { get; } = new();
    public List<IReadOnlyList<string>> Batches { get; } = new();

    /// <summary>Se impostata, viene sollevata da EmbedDocumentsAsync al batch con questo indice (0-based).</summary>
    public Func<int, AssistantException?>? FailBatch { get; set; }

    /// <summary>Eseguita all'inizio di ogni EmbedDocumentsAsync (es. per far avanzare un orologio finto mentre la chiamata e' in corso).</summary>
    public Action<int>? OnDocuments { get; set; }

    /// <summary>Se impostata, EmbedQueryAsync la solleva (dopo aver registrato la domanda).</summary>
    public Exception? QueryFailure { get; set; }

    public Task<float[]> EmbedQueryAsync(string question, CancellationToken ct)
    {
        Queries.Add(question);
        if (QueryFailure is not null) throw QueryFailure;
        return Task.FromResult(new float[] { 1, 0, 0 });
    }

    public Task<float[][]> EmbedDocumentsAsync(IReadOnlyList<string> documents, CancellationToken ct)
    {
        var index = Batches.Count;
        Batches.Add(documents);
        OnDocuments?.Invoke(index);
        if (FailBatch?.Invoke(index) is { } ex) throw ex;
        return Task.FromResult(documents.Select(_ => new float[] { 0, 1, 0 }).ToArray());
    }
}

public sealed class FakeVectorStore : ITaskVectorStore
{
    public Dictionary<int, string> Hashes { get; } = new();
    public List<(int Id, string Model, string Hash)> Upserts { get; } = new();
    public IReadOnlyList<VectorHit> Hits { get; set; } = Array.Empty<VectorHit>();
    public int SearchCalls { get; private set; }
    public int LastTopK { get; private set; }
    public string? LastModel { get; private set; }

    public Task UpsertAsync(int taskId, string model, string inputSha256, float[] embedding, CancellationToken ct)
    {
        Upserts.Add((taskId, model, inputSha256));
        Hashes[taskId] = inputSha256;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(int taskId, CancellationToken ct) => Task.CompletedTask;

    public Task<string?> GetHashAsync(int taskId, string model, CancellationToken ct)
        => Task.FromResult(Hashes.TryGetValue(taskId, out var h) ? h : null);

    public Task<Dictionary<int, string>> GetHashesAsync(string model, CancellationToken ct) => Task.FromResult(new Dictionary<int, string>(Hashes));

    public Task<IReadOnlyList<VectorHit>> SearchAsync(float[] queryEmbedding, string model, int topK, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        SearchCalls++;
        LastTopK = topK;
        LastModel = model;
        return Task.FromResult(Hits);
    }
}

public sealed class FakeTaskSource : ITaskSource
{
    public List<TaskItem> Tasks { get; } = new();
    public (DateOnly From, DateOnly To, int Max)? LastUpcomingQuery { get; private set; }

    public Task<TaskItem?> GetAsync(int id, CancellationToken ct) => Task.FromResult(Tasks.FirstOrDefault(t => t.Id == id));

    public Task<IReadOnlyList<TaskItem>> GetAllAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<TaskItem>>(Tasks.ToList());

    /// <summary>Come l'implementazione EF: non completate, nell'intervallo, per data/ora, al massimo <c>max</c>.</summary>
    public Task<IReadOnlyList<TaskItem>> GetUpcomingAsync(DateOnly from, DateOnly to, int max, CancellationToken ct)
    {
        LastUpcomingQuery = (from, to, max);
        return Task.FromResult<IReadOnlyList<TaskItem>>(Tasks
            .Where(t => !t.Done && t.Date >= from && t.Date <= to)
            .OrderBy(t => t.Date).ThenBy(t => t.Time).ThenBy(t => t.Id).Take(max).ToList());
    }
}

public sealed class FakeChain : IChatChain
{
    public ChatResult Result { get; set; } = new("Risposta [1]", "fake-model", true);
    public Exception? Throw { get; set; }
    public int Calls { get; private set; }
    public string? LastSystem { get; private set; }
    public string? LastUser { get; private set; }

    public Task<ChatResult> GenerateAsync(string system, string user, CancellationToken ct)
    {
        Calls++;
        LastSystem = system;
        LastUser = user;
        if (Throw is not null) throw Throw;
        return Task.FromResult(Result);
    }
}

public static class Make
{
    public static TaskItem Task(int id, string title, string date, string? time = null, bool done = false,
        string? note = null, string? address = null, string? label = null, string? author = null) => new()
    {
        Id = id,
        Title = title,
        Date = DateOnly.ParseExact(date, "yyyy-MM-dd"),
        Time = time is null ? null : TimeOnly.ParseExact(time, "HH:mm"),
        Done = done,
        Note = note,
        Address = address,
        Label = label is null ? null : new Label { Id = 1, Name = label, Color = "#000000" },
        CreatedBy = author is null ? null : new Member { Id = 1, DisplayName = author },
    };

    public static AssistantService Service(
        FakeEmbeddingClient embed, FakeVectorStore store, FakeTaskSource tasks, IChatChain chain,
        FakeClock? clock = null, AssistantOptions? options = null) =>
        new(embed, store, tasks, chain, options ?? new AssistantOptions(),
            clock ?? new FakeClock(new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero)),
            NullLogger<AssistantService>.Instance);

    public static string GeminiOk(string text) =>
        "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":" + System.Text.Json.JsonSerializer.Serialize(text) + "}]},\"finishReason\":\"STOP\"}]}";

    public static string GroqOk(string text) =>
        "{\"choices\":[{\"message\":{\"content\":" + System.Text.Json.JsonSerializer.Serialize(text) + "},\"finish_reason\":\"stop\"}]}";
}
