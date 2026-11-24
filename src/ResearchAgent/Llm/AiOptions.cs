namespace ResearchAgent.Llm;

/// <summary>
/// Configuration of the AI layer, bound from the "Ai" section. Only non-secret policy lives here:
/// model names, provider order, output limits and prices. API keys come from configuration
/// ("Ai:OpenAI:ApiKey", "Ai:Gemini:ApiKey") or environment variables, never from this object.
/// Changing a policy needs a config change, not a redeploy.
/// </summary>
public sealed class AiOptions
{
    public const string SectionName = "Ai";

    public AiProviderSettings OpenAI { get; set; } = new()
    {
        Model = "gpt-6-luna",
        InputPricePerMillionUsd = 0.10m,
        OutputPricePerMillionUsd = 0.50m,
        Prices =
        {
            ["gpt-6.1-terra"] = new() { InputPerMillionUsd = 1.0m, OutputPerMillionUsd = 5.0m },
            ["gpt-5.6-terra"] = new() { InputPerMillionUsd = 2.0m, OutputPerMillionUsd = 12.0m },
        },
    };

    public AiProviderSettings Gemini { get; set; } = new()
    {
        Model = "gemini-2.5-flash",
        InputPricePerMillionUsd = 0.30m,
        OutputPricePerMillionUsd = 2.50m,
    };

    /// <summary>Model used by the OpenAI <c>web_search</c> tool. The step only searches and lists sources, so a small model is enough.</summary>
    public string WebSearchModel { get; set; } = "gpt-6-luna";

    /// <summary>Switch for the paid OpenAI <c>web_search</c> escalation layer (slow: 45 to 95 seconds per query).</summary>
    public bool EnableOpenAiWebSearch { get; set; }

    /// <summary>Policy for any task that has no entry in <see cref="Policies"/>.</summary>
    public AiTaskPolicy DefaultPolicy { get; set; } = new();

    /// <summary>Per-task policies, keyed by task name (for example "research.synthesize").</summary>
    public Dictionary<string, AiTaskPolicy> Policies { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public AiTaskPolicy GetPolicy(string taskType) =>
        Policies.TryGetValue(taskType, out var policy) ? policy : DefaultPolicy;

    /// <summary>
    /// Real cost of a call in USD. Cached input tokens are a subset of the input tokens and are
    /// billed at the cached price when one is configured; without that the report overstates the cost
    /// several times for prompts with a long fixed prefix.
    /// </summary>
    public decimal ComputeCostUsd(AiProvider provider, string model, AiUsage usage)
    {
        var settings = provider switch
        {
            AiProvider.OpenAI => OpenAI,
            AiProvider.Gemini => Gemini,
            _ => null,
        };
        if (settings is null) return 0m;

        var price = settings.Prices.TryGetValue(model, out var p) ? p : settings.DefaultPrice;
        var cached = Math.Min(usage.CachedInputTokens, usage.InputTokens);
        var uncached = usage.InputTokens - cached;
        var cachedPrice = price.CachedInputPerMillionUsd ?? price.InputPerMillionUsd;
        return (uncached * price.InputPerMillionUsd + cached * cachedPrice + usage.OutputTokens * price.OutputPerMillionUsd) / 1_000_000m;
    }
}

public sealed class AiProviderSettings
{
    public string Model { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; } = 120;

    public decimal InputPricePerMillionUsd { get; set; }

    public decimal? CachedInputPricePerMillionUsd { get; set; }

    public decimal OutputPricePerMillionUsd { get; set; }

    /// <summary>Prices of other models of this provider, keyed by model name.</summary>
    public Dictionary<string, ModelPrice> Prices { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    internal ModelPrice DefaultPrice => new()
    {
        InputPerMillionUsd = InputPricePerMillionUsd,
        CachedInputPerMillionUsd = CachedInputPricePerMillionUsd,
        OutputPerMillionUsd = OutputPricePerMillionUsd,
    };
}

public sealed class ModelPrice
{
    public decimal InputPerMillionUsd { get; set; }
    public decimal? CachedInputPerMillionUsd { get; set; }
    public decimal OutputPerMillionUsd { get; set; }
}

/// <summary>How one task type is served: which providers in which order, and with which limits.</summary>
public sealed class AiTaskPolicy
{
    /// <summary>Providers in fallback order: the router tries the next one when a call fails.</summary>
    public List<AiProvider> Providers { get; set; } = [AiProvider.OpenAI, AiProvider.Gemini];

    public int MaxOutputTokens { get; set; } = 4096;

    public string? ReasoningEffort { get; set; }

    /// <summary>Model override for this task (OpenAI only; a fallback provider uses its own model).</summary>
    public string? Model { get; set; }

    /// <summary>Use the OpenAI Responses API for the tool-calling turns of this task.</summary>
    public bool UseResponsesApi { get; set; }

    /// <summary>Units charged by the usage meter for one successful call (credits, in the host's terms).</summary>
    public int Cost { get; set; } = 1;

    /// <summary>
    /// Whether traces may keep the user prompt and the response. Turn it off for tasks that carry
    /// personal or sensitive text: timing, tokens, cost and errors are still recorded.
    /// </summary>
    public bool TraceContent { get; set; } = true;
}
