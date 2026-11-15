using System.Net;
using System.Text;
using System.Text.Json;
using FamilyCalendar.Controllers;
using FamilyCalendar.Services;
using FamilyCalendar.Services.Assistant;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FamilyCalendar.Tests;

/// <summary>
/// Prova la pipeline HTTP reale (Kestrel su una porta libera, filtri MVC, limite del corpo) con l'assistente finto,
/// senza database. Il login è disattivato (nessuna password configurata), quindi [TokenAuth] lascia passare.
/// </summary>
public sealed class AssistantEndpointTests : IAsyncLifetime
{
    private static readonly string[] AuthVars =
        { "FAMILYCALENDAR_AUTH_PASSWORD", "FAMILYCALENDAR_AUTH_PASSWORD_ADMIN", "FAMILYCALENDAR_AUTH_SECRET" };

    private readonly Dictionary<string, string?> _savedEnv = new();
    private WebApplication _app = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        // AuthService legge queste variabili d'ambiente: se fossero impostate (shell, CI) il login sarebbe attivo e i test
        // riceverebbero 401. Le azzero per la durata del test e le ripristino in DisposeAsync.
        foreach (var v in AuthVars)
        {
            _savedEnv[v] = Environment.GetEnvironmentVariable(v);
            Environment.SetEnvironmentVariable(v, null);
        }

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        var embed = new FakeEmbeddingClient();
        var tasks = new FakeTaskSource();
        tasks.Tasks.Add(Make.Task(1, "Gita", "2026-11-20", "09:00"));
        var options = new AssistantOptions();
        var clock = new FakeClock(new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero));

        builder.Services.AddSingleton(new AuthService(builder.Configuration));
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(new AssistantLimiter(options, clock));
        builder.Services.AddSingleton(Make.Service(embed, new FakeVectorStore(), tasks, new FakeChain(), clock, options));
        builder.Services.AddControllers().AddApplicationPart(typeof(AssistantController).Assembly);

        _app = builder.Build();
        _app.MapControllers();
        await _app.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        foreach (var (name, value) in _savedEnv)
            Environment.SetEnvironmentVariable(name, value);
    }

    private Task<HttpResponseMessage> Post(string json) =>
        _http.PostAsync("/api/assistant/ask", new StringContent(json, Encoding.UTF8, "application/json"));

    [Fact]
    public async Task ValidQuestion_Is200WithCamelCaseJson()
    {
        var resp = await Post("{\"question\":\"cosa c'e'?\"}");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("Risposta [1]", doc.RootElement.GetProperty("answer").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("sources")[0].GetProperty("ref").GetInt32());
        Assert.False(doc.RootElement.GetProperty("reducedModel").GetBoolean());
        Assert.Equal(1, doc.RootElement.GetProperty("retrieved").GetInt32());
    }

    [Theory]
    [InlineData("{\"question\":")]   // JSON malformato
    [InlineData("")]                 // corpo vuoto
    [InlineData("null")]
    [InlineData("{\"question\":\"   \"}")]
    public async Task InvalidOrMissingBody_Is400WithJsonMessage(string body)
    {
        var resp = await Post(body);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("Scrivi una domanda.", doc.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task BodyOver16KB_IsRejectedWith413()
    {
        var resp = await Post("{\"question\":\"" + new string('a', 20_000) + "\"}");

        // Il limite e' applicato dal server (Kestrel) sul corpo, prima del codice dell'action: 413 senza il JSON {"message"}.
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, resp.StatusCode);
    }

    [Fact]
    public async Task QuestionJustOverTheLimit_Is400WithJsonMessage()
    {
        var resp = await Post("{\"question\":\"" + new string('a', 501) + "\"}");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Contains("500", doc.RootElement.GetProperty("message").GetString());
    }
}
