namespace FamilyCalendar.Services.Assistant;

/// <summary>Chiavi API risolte una volta sola: env <c>GEMINI_API_KEY</c>/<c>GROQ_API_KEY</c>, poi config <c>Gemini:ApiKey</c>/<c>Groq:ApiKey</c>. Mai loggate.</summary>
public sealed class AssistantKeys
{
    public string? Gemini { get; }
    public string? Groq { get; }

    public AssistantKeys(string? gemini, string? groq)
    {
        Gemini = Clean(gemini);
        Groq = Clean(groq);
    }

    public static AssistantKeys FromConfiguration(IConfiguration config) => new(
        Pick(Environment.GetEnvironmentVariable("GEMINI_API_KEY"), config["Gemini:ApiKey"]),
        Pick(Environment.GetEnvironmentVariable("GROQ_API_KEY"), config["Groq:ApiKey"]));

    private static string? Pick(string? env, string? fallback) => string.IsNullOrWhiteSpace(env) ? fallback : env;

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>Impostazioni dell'assistente (sezione <c>Assistant</c>).</summary>
public sealed class AssistantOptions
{
    public static readonly string[] DefaultGeminiModels = { "gemini-3.5-flash-lite", "gemini-3.1-flash-lite" };

    /// <summary>Modelli Gemini di risposta, in ordine di preferenza (<c>Assistant:GeminiModels</c>, array o lista separata da virgole); poi Groq.</summary>
    public IReadOnlyList<string> GeminiModels { get; init; } = DefaultGeminiModels;

    /// <summary><c>Assistant:PerUserPerHour</c> (default 10).</summary>
    public int PerUserPerHour { get; init; } = 10;

    /// <summary><c>Assistant:GlobalPerDay</c> (default 80).</summary>
    public int GlobalPerDay { get; init; } = 80;

    /// <summary><c>Assistant:MaxConcurrent</c> (default 2).</summary>
    public int MaxConcurrent { get; init; } = 2;

    /// <summary>
    /// Budget di tempo di una domanda. INVARIANTE: il timeout del client (frontend, 60 s) deve essere MAGGIORE di
    /// <see cref="SlotWait"/> + <see cref="Deadline"/> (5 + 50 = 55 s), altrimenti il client rinuncia prima che il server
    /// possa restituire l'esito dei fallback. Timeout per singola chiamata (vedi <c>AddAssistant</c>): embedding della
    /// domanda 8 s per tentativo (max 2), Gemini chat 15 s, Groq chat 20 s.
    /// </summary>
    public static readonly TimeSpan ClientTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Tempo massimo complessivo di una domanda, embedding compreso (<c>Assistant:DeadlineSeconds</c>, default 50).</summary>
    public TimeSpan Deadline { get; init; } = TimeSpan.FromSeconds(50);

    /// <summary>Valorizzata se <c>Assistant:DeadlineSeconds</c> era troppo alto ed è stato ridotto per rispettare l'invariante col timeout del client.</summary>
    public TimeSpan? DeadlineReducedFrom { get; init; }

    /// <summary>Margine tra deadline + attesa slot e il timeout del client.</summary>
    public static readonly TimeSpan ClientMargin = TimeSpan.FromSeconds(5);

    /// <summary>Attesa massima di un posto tra le chiamate contemporanee (<c>Assistant:SlotWaitSeconds</c>, default 5).</summary>
    public TimeSpan SlotWait { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Massimo di attività embeddate per ogni giro di backfill (<c>Assistant:BackfillMaxPerRun</c>, default 200).</summary>
    public int BackfillMaxPerRun { get; init; } = 200;

    /// <summary>Tetto in caratteri del contesto passato al modello (<c>Assistant:MaxContextChars</c>, default 24000).</summary>
    public int MaxContextChars { get; init; } = 24000;

    public static AssistantOptions FromConfiguration(IConfiguration config)
    {
        var section = config.GetSection("Assistant");
        var modelsSection = section.GetSection("GeminiModels");
        var models = modelsSection.GetChildren()
            .Select(c => c.Value).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim()).ToList();
        if (models.Count == 0 && !string.IsNullOrWhiteSpace(modelsSection.Value))
            models = modelsSection.Value.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        var slotWait = TimeSpan.FromSeconds(Math.Clamp(section.GetValue("SlotWaitSeconds", 5), 0, 30));
        var configuredDeadline = TimeSpan.FromSeconds(Math.Clamp(section.GetValue("DeadlineSeconds", 50), 5, 600));
        // Invariante: client (60 s) > slot + deadline, con margine: il deadline non puo' superare client - slot - margine.
        var maxDeadline = ClientTimeout - slotWait - ClientMargin;
        var deadline = configuredDeadline > maxDeadline ? maxDeadline : configuredDeadline;

        return new AssistantOptions
        {
            GeminiModels = models.Count > 0 ? models : DefaultGeminiModels,
            PerUserPerHour = Math.Max(1, section.GetValue("PerUserPerHour", 10)),
            GlobalPerDay = Math.Max(1, section.GetValue("GlobalPerDay", 80)),
            MaxConcurrent = Math.Max(1, section.GetValue("MaxConcurrent", 2)),
            Deadline = deadline,
            DeadlineReducedFrom = deadline < configuredDeadline ? configuredDeadline : null,
            SlotWait = slotWait,
            BackfillMaxPerRun = Math.Max(1, section.GetValue("BackfillMaxPerRun", 200)),
            MaxContextChars = Math.Max(500, section.GetValue("MaxContextChars", 24000)),
        };
    }
}
