using System.Text;
using System.Text.Json;

namespace ResearchAgent.Web;

/// <summary>A single search result row (raw SERP: title + snippet + link).</summary>
public sealed record WebSearchResult(string Title, string Snippet, string Url);

/// <summary>
/// The outcome of a search call. <see cref="Failed"/> is carried separately because "no results" and
/// "the search did not work" are two COMPLETELY different situations: the first is normal (a weak digital
/// footprint — we measured it), the second is a failure and must NOT be silent — otherwise the paid
/// tier can die one day and nobody notices.
/// </summary>
public sealed record WebSearchOutcome(
    IReadOnlyList<WebSearchResult> Results,
    bool Failed,
    string? Error,
    string? Summary = null)
{
    public static readonly WebSearchOutcome Empty = new(Array.Empty<WebSearchResult>(), false, null);
    public static WebSearchOutcome Ok(IReadOnlyList<WebSearchResult> results, string? summary = null) =>
        new(results, false, null, summary);
    public static WebSearchOutcome Fail(string error) => new(Array.Empty<WebSearchResult>(), true, error);
}

/// <summary>
/// A raw web search provider. A separate interface because the provider must be swappable
/// (Serper returns cheap raw SERP data → we extract the summary ourselves; Tavily/Exa include content
/// extraction but cost ~10× more). When <see cref="IsConfigured"/> is false the search layer is skipped
/// SILENTLY — deep research can still work from the subject's own URLs.
/// </summary>
public interface IWebSearchProvider
{
    bool IsConfigured { get; }
    Task<WebSearchOutcome> SearchAsync(string query, int maxResults, CancellationToken ct = default);

    /// <summary>
    /// Searches through one country's Google (<paramref name="countryCode"/>, ISO-3166 alpha-2; null = no country),
    /// so that a subject based in Ireland ranks higher in Irish results. A provider that does not support
    /// countries searches without one.
    /// </summary>
    Task<WebSearchOutcome> SearchAsync(string query, int maxResults, string? countryCode, CancellationToken ct = default) =>
        SearchAsync(query, maxResults, ct);
}

/// <summary>The country parameter (<c>gl</c>) of the search services: a two-letter country code in lower case, otherwise null.</summary>
public static class WebSearchCountry
{
    public static string? Gl(string? countryCode)
    {
        var code = countryCode?.Trim();
        return code is { Length: 2 } && code.All(char.IsAsciiLetter) ? code.ToLowerInvariant() : null;
    }
}

/// <summary>
/// ESCALATION search provider — a seam SEPARATE from the fast SERP tier. A deliberately separate type:
/// this provider is expensive and slow (measured: 45–95 s per query), so it is NOT put into the chain
/// as a link; it is called once, and only when the fast tier falls short. Being a separate interface
/// also stops it from silently standing in for <see cref="IWebSearchProvider"/> in DI.
/// </summary>
public interface IEscalationSearchProvider : IWebSearchProvider;

/// <summary>
/// Serper.dev (Google SERP proxy) — about $1 per 1,000 queries. Key: <c>Ai:Serper:ApiKey</c>
/// (environment variable <c>Ai__Serper__ApiKey</c>). Without a key, IsConfigured returns false and
/// the search step is skipped (the feature still works, just with less evidence).
/// </summary>
public sealed class SerperSearchProvider : IWebSearchProvider
{
    private const string Endpoint = "https://google.serper.dev/search";

    private readonly HttpClient _httpClient;
    private readonly ILogger<SerperSearchProvider> _logger;
    private readonly string? _apiKey;

    public SerperSearchProvider(HttpClient httpClient, IConfiguration configuration, ILogger<SerperSearchProvider> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _apiKey = configuration["Ai:Serper:ApiKey"] ?? Environment.GetEnvironmentVariable("SERPER_API_KEY");
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);

    public Task<WebSearchOutcome> SearchAsync(string query, int maxResults, CancellationToken ct = default) =>
        SearchAsync(query, maxResults, null, ct);

    public async Task<WebSearchOutcome> SearchAsync(string query, int maxResults, string? countryCode, CancellationToken ct = default)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(query))
            return WebSearchOutcome.Empty;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            request.Headers.TryAddWithoutValidation("X-API-KEY", _apiKey);
            var body = new Dictionary<string, object> { ["q"] = query, ["num"] = Math.Clamp(maxResults, 1, 10) };
            if (WebSearchCountry.Gl(countryCode) is { } gl) body["gl"] = gl;
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                // 401/403 = key problem, 429 = quota/rate limit — both "need a human".
                _logger.LogWarning("Serper search failed: HTTP {Status} (check the key/quota)", status);
                return WebSearchOutcome.Fail($"HTTP {status}");
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("organic", out var organic) || organic.ValueKind != JsonValueKind.Array)
                return WebSearchOutcome.Empty;   // genuinely no results — not an error

            var results = new List<WebSearchResult>();
            foreach (var item in organic.EnumerateArray().Take(maxResults))
            {
                var url = item.TryGetProperty("link", out var l) ? l.GetString() : null;
                if (string.IsNullOrWhiteSpace(url)) continue;
                results.Add(new WebSearchResult(
                    item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                    item.TryGetProperty("snippet", out var s) ? s.GetString() ?? "" : "",
                    url));
            }
            return WebSearchOutcome.Ok(results);
        }
        catch (Exception ex)
        {
            // Search is an optional layer — a failure does not stop deep research, but it stays visible.
            _logger.LogWarning(ex, "Serper search error: {Query}", query);
            return WebSearchOutcome.Fail(ex.GetType().Name);
        }
    }
}
