namespace FamilyCalendar.Services.Assistant;

public static class AssistantServiceCollectionExtensions
{
    /// <summary>
    /// Registra l'assistente Aesir: client Gemini/Groq (HttpClient via factory con timeout dedicati), catena di generazione,
    /// limiter, servizio, indicizzazione incrementale e worker di backfill. L'archivio vettoriale (<c>ITaskVectorStore</c>)
    /// è registrato a parte.
    /// </summary>
    public static IServiceCollection AddAssistant(this IServiceCollection services, IConfiguration config)
    {
        var keys = AssistantKeys.FromConfiguration(config);
        var options = AssistantOptions.FromConfiguration(config);
        services.AddSingleton(keys);
        services.AddSingleton(options);
        services.AddSingleton(TimeProvider.System);

        // Timeout per singola chiamata; il deadline complessivo della domanda sta in AssistantOptions.Deadline (50 s) e
        // con l'attesa dello slot (5 s) deve restare sotto il timeout del client frontend (60 s): vedi AssistantOptions.ClientTimeout.
        services.AddHttpClient(GeminiEmbeddingClient.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(8));
        services.AddHttpClient(GeminiEmbeddingClient.BatchHttpClientName, c => c.Timeout = TimeSpan.FromSeconds(30)); // indicizzazione, nessuno aspetta
        services.AddHttpClient(GeminiChatProvider.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(15));
        services.AddHttpClient(GroqChatProvider.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(20));

        services.AddSingleton<IEmbeddingClient>(sp =>
            new GeminiEmbeddingClient(sp.GetRequiredService<IHttpClientFactory>(), keys));

        // Catena: modelli Gemini configurati -> Groq 120b -> Groq 20b (solo i provider per cui c'è una chiave).
        services.AddSingleton<IChatChain>(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            var providers = new List<IChatProvider>();
            if (keys.Gemini is not null)
                providers.AddRange(options.GeminiModels.Select(m => new GeminiChatProvider(factory, keys.Gemini, m)));
            if (keys.Groq is not null)
            {
                providers.Add(new GroqChatProvider(factory, keys.Groq, GroqChatProvider.PrimaryModel));
                providers.Add(new GroqChatProvider(factory, keys.Groq, GroqChatProvider.FallbackModel));
            }
            return new ChatChain(providers, sp.GetRequiredService<ILogger<ChatChain>>());
        });

        services.AddSingleton<AssistantLimiter>();
        services.AddScoped<ITaskSource, EfTaskSource>();
        services.AddScoped<TaskIndexer>();
        services.AddScoped<AssistantService>();

        services.AddSingleton<TaskIndexScheduler>();
        services.AddSingleton<ITaskIndexScheduler>(sp => sp.GetRequiredService<TaskIndexScheduler>());
        services.AddHostedService<TaskIndexingWorker>();
        return services;
    }
}
