using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ResearchAgent.Agents;
using ResearchAgent.Research;

namespace ResearchAgent;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the AI layer, the agent loop, the web tools and the research agent.
    /// Binds <see cref="AiOptions"/> from the "Ai" section. Metering and trace storage default to no-ops:
    /// register your own <see cref="IAiUsageMeter"/> or <see cref="IAiTraceRecorder"/> before or after this call.
    /// </summary>
    public static IServiceCollection AddResearchAgent(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AiOptions>(configuration.GetSection(AiOptions.SectionName));

        services.TryAddScoped<AiTraceContext>();
        services.TryAddSingleton<IAiUsageMeter, NullAiUsageMeter>();
        services.TryAddSingleton<IAiTraceRecorder, NullAiTraceRecorder>();
        services.TryAddSingleton<IAiRateLimitTracker, AiRateLimitTracker>();

        // Provider adapters, each wrapped by the tracing decorator.
        services.AddHttpClient<OpenAIAdapter>((sp, http) =>
            http.Timeout = TimeSpan.FromSeconds(sp.GetRequiredService<IOptions<AiOptions>>().Value.OpenAI.TimeoutSeconds));
        services.AddHttpClient<GeminiAdapter>((sp, http) =>
            http.Timeout = TimeSpan.FromSeconds(sp.GetRequiredService<IOptions<AiOptions>>().Value.Gemini.TimeoutSeconds));
        services.AddScoped<IAiProviderAdapter>(sp => Traced(sp, sp.GetRequiredService<OpenAIAdapter>()));
        services.AddScoped<IAiProviderAdapter>(sp => Traced(sp, sp.GetRequiredService<GeminiAdapter>()));

        services.AddScoped<IAiCompletionService, AiRouter>();
        services.AddScoped<IAiAgentRunner, AiAgentRunner>();

        // Web search: fast results first, OpenAI web_search only when enabled and the first tier fails.
        services.AddHttpClient<SerperSearchProvider>();
        services.AddHttpClient<OpenAiWebSearchProvider>(http => http.Timeout = TimeSpan.FromSeconds(150));
        services.AddScoped<IWebSearchProvider>(sp => new CompositeWebSearchProvider(
            [sp.GetRequiredService<SerperSearchProvider>(), sp.GetRequiredService<OpenAiWebSearchProvider>()],
            sp.GetRequiredService<ILogger<CompositeWebSearchProvider>>()));

        // Page reading: plain HTTP first, Firecrawl (a real browser) for pages that need one.
        services.AddHttpClient<WebPageFetcher>(http => http.Timeout = TimeSpan.FromSeconds(20));
        services.AddHttpClient<FirecrawlPageFetcher>(http => http.Timeout = TimeSpan.FromSeconds(60));
        services.AddScoped<IWebPageFetcher, CompositeWebPageFetcher>();

        services.AddScoped<IDeepResearchAgent, DeepResearchAgent>();
        return services;
    }

    private static IAiProviderAdapter Traced(IServiceProvider sp, IAiProviderAdapter inner) =>
        new TracingAiProviderAdapter(
            inner,
            sp.GetRequiredService<IAiTraceRecorder>(),
            sp.GetRequiredService<AiTraceContext>(),
            sp.GetRequiredService<IOptions<AiOptions>>());
}
