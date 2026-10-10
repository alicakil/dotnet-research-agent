using System.Collections.Concurrent;
using System.Net.Http.Headers;

namespace WebApi.Services.Ai;

/// <summary>
/// Sağlayıcıların response header'larındaki CANLI rate-limit bilgisini (kalan/reset) tutar.
/// In-memory (singleton), sağlayıcı başına en SON snapshot. Restart'ta sıfırlanır; <c>Record</c> çağıran
/// ilk AI çağrısından sonra dolar. <c>x-ratelimit-*</c> header'larını okur (Gemini bu header'ları döndürmez,
/// onun için snapshot oluşmaz). Bu, BİZİM saydığımız kullanımdan farklı: sağlayıcının canlı raporu.
///
/// ⚠️ Bugün <c>Record</c>'u çağıran adapter YOK: tek çağıranı, kaldırılan yedek sağlayıcının adapter'ıydı
/// (VIT-39). Bir adapter çağırana dek liste boş döner ve admin Raporlar → Kullanım'daki "Canlı kota"
/// bölümü görünmez.
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

        // En az bir rate-limit header'i geldiyse kaydet; yoksa (ör. Gemini) eski snapshot korunur.
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
    public string? ResetRequests { get; set; }   // sağlayıcının döndürdüğü süre string'i (ör. "45.6s", "2m59s")
    public int? LimitTokens { get; set; }
    public int? RemainingTokens { get; set; }
    public string? ResetTokens { get; set; }
    public DateTime CapturedAt { get; set; }
}
