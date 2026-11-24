using System.Collections.Concurrent;
using System.Net.Http.Headers;

namespace ResearchAgent.Llm;

/// <summary>
/// Holds the LIVE rate-limit information (remaining / reset) taken from the providers' response headers.
/// In-memory (singleton), only the LATEST snapshot per provider. It is reset on restart and fills up after
/// the first AI call that calls <c>Record</c>. Reads the <c>x-ratelimit-*</c> headers (Gemini does not
/// return them, so no snapshot is created for it). This differs from the usage WE count: it is the
/// provider's own live report.
///
/// ⚠️ Currently NO adapter calls <c>Record</c>: its only caller was the adapter of a fallback provider
/// that has since been removed. Until an adapter calls it, the list stays empty and any "live quota"
/// view built on <c>GetSnapshots</c> has nothing to show.
/// </summary>
public interface IAiRateLimitTracker
{
    void Record(AiProvider provider, HttpResponseHeaders headers);
    IReadOnlyList<AiRateLimitSnapshot> GetSnapshots();
}

public sealed class AiRateLimitTracker : IAiRateLimitTracker
{
    private readonly ConcurrentDictionary<AiProvider, AiRateLimitSnapshot> _latest = new();

    public void Record(AiProvider provider, HttpResponseHeaders headers)
    {
        var snap = new AiRateLimitSnapshot
        {
            Provider = provider.ToString(),
            CapturedAt = DateTime.UtcNow,
            LimitRequests = TryInt(headers, "x-ratelimit-limit-requests"),
            RemainingRequests = TryInt(headers, "x-ratelimit-remaining-requests"),
            ResetRequests = TryStr(headers, "x-ratelimit-reset-requests"),
            LimitTokens = TryInt(headers, "x-ratelimit-limit-tokens"),
            RemainingTokens = TryInt(headers, "x-ratelimit-remaining-tokens"),
            ResetTokens = TryStr(headers, "x-ratelimit-reset-tokens"),
        };

        // Save only when at least one rate-limit header came back; otherwise (e.g. Gemini) the old snapshot is kept.
        if (snap.RemainingRequests is not null || snap.RemainingTokens is not null)
            _latest[provider] = snap;
    }

    public IReadOnlyList<AiRateLimitSnapshot> GetSnapshots() => _latest.Values.ToList();

    private static int? TryInt(HttpResponseHeaders h, string name)
        => h.TryGetValues(name, out var v) && int.TryParse(v.FirstOrDefault(), out var i) ? i : null;

    private static string? TryStr(HttpResponseHeaders h, string name)
        => h.TryGetValues(name, out var v) ? v.FirstOrDefault() : null;
}

public sealed class AiRateLimitSnapshot
{
    public string Provider { get; set; } = string.Empty;
    public int? LimitRequests { get; set; }
    public int? RemainingRequests { get; set; }
    public string? ResetRequests { get; set; }   // duration string returned by the provider (e.g. "45.6s", "2m59s")
    public int? LimitTokens { get; set; }
    public int? RemainingTokens { get; set; }
    public string? ResetTokens { get; set; }
    public DateTime CapturedAt { get; set; }
}
