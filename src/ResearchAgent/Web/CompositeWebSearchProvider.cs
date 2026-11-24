namespace ResearchAgent.Web;

/// <summary>
/// Tries the search providers IN ORDER: the first one that is configured and succeeds wins.
/// The order is cheapest to most expensive (Serper raw SERP ~$1/1k → OpenAI web_search ~$10/1k + content
/// tokens), so no money is burned while Serper works, and evidence gathering does not stop when it does not.
///
/// Semantics (resilience):
///  • If a provider FAILS, the next one is tried (the failure is not swallowed, it is logged).
///  • The chain stops at the first provider that RETURNS results.
///  • All returned empty → Empty (genuinely no results; a weak digital footprint is normal).
///  • All failed → Fail (visible to the caller; a human needs to step in).
/// NO parallel calls — that would mean paying twice for the same evidence.
/// </summary>
public sealed class CompositeWebSearchProvider(
    IReadOnlyList<IWebSearchProvider> providers,
    ILogger<CompositeWebSearchProvider> logger) : IWebSearchProvider
{
    public bool IsConfigured => providers.Any(p => p.IsConfigured);

    public Task<WebSearchOutcome> SearchAsync(string query, int maxResults, CancellationToken ct = default) =>
        SearchAsync(query, maxResults, null, ct);

    /// <summary>The country (<paramref name="countryCode"/>) is passed to every provider; a provider that does not support it searches without one.</summary>
    public async Task<WebSearchOutcome> SearchAsync(string query, int maxResults, string? countryCode, CancellationToken ct = default)
    {
        var attempted = 0;
        var failures = new List<string>();

        foreach (var provider in providers.Where(p => p.IsConfigured))
        {
            attempted++;
            var outcome = await provider.SearchAsync(query, maxResults, countryCode, ct);

            if (outcome.Failed)
            {
                failures.Add($"{provider.GetType().Name}: {outcome.Error}");
                logger.LogWarning("Search provider {Provider} failed ({Error}) — moving on to the next one.",
                    provider.GetType().Name, outcome.Error);
                continue;
            }

            if (outcome.Results.Count > 0)
                return outcome;

            // Configured and it ran, but there are no results → probably no footprint; still worth
            // trying the next one (a different index can return different results).
        }

        if (attempted == 0) return WebSearchOutcome.Empty;                 // none configured
        if (failures.Count == attempted) return WebSearchOutcome.Fail(string.Join(" · ", failures));
        return WebSearchOutcome.Empty;                                     // ran, but no results
    }
}
