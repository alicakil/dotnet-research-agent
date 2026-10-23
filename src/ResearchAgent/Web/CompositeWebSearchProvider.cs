namespace WebApi.Services.Ai;

/// <summary>
/// Arama sağlayıcılarını SIRAYLA dener: ilk yapılandırılmış ve başarılı olan kazanır.
/// Sıra = ucuzdan pahalıya (Serper ham SERP ~$1/1k → OpenAI web_search ~$10/1k + içerik token'ı),
/// böylece Serper çalıştığında para yanmaz, çalışmadığında kanıt toplama durmaz.
///
/// Semantik (kırılmaya dayanıklılık):
///  • Bir sağlayıcı FAIL ederse sıradakine geçilir (arıza yutulmaz, log'a yazılır).
///  • Sonuç DÖNEN ilk sağlayıcıda durulur.
///  • Hepsi boş döndüyse → Empty (gerçekten sonuç yok; zayıf dijital iz normaldir).
///  • Hepsi fail ettiyse → Fail (brief'e düşer, insan müdahalesi gerekir).
/// PARALEL çağrı YOK — aynı kanıt için iki kez ödemek olurdu.
/// </summary>
public sealed class CompositeWebSearchProvider(
    IReadOnlyList<IWebSearchProvider> providers,
    ILogger<CompositeWebSearchProvider> logger) : IWebSearchProvider
{
    public bool IsConfigured => providers.Any(p => p.IsConfigured);

    public Task<WebSearchOutcome> SearchAsync(string query, int maxResults, CancellationToken ct = default) =>
        SearchAsync(query, maxResults, null, ct);

    /// <summary>Ülke (<paramref name="countryCode"/>) her sağlayıcıya geçer; desteklemeyen ülkesiz arar.</summary>
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
                logger.LogWarning("Arama sağlayıcısı {Provider} başarısız ({Error}) — sıradakine geçiliyor.",
                    provider.GetType().Name, outcome.Error);
                continue;
            }

            if (outcome.Results.Count > 0)
                return outcome;

            // Yapılandırılmış, çalıştı, ama sonuç yok → gerçekten iz yok; yine de sıradakini denemeye
            // değer (farklı indeks farklı sonuç verebilir).
        }

        if (attempted == 0) return WebSearchOutcome.Empty;                 // hiçbiri yapılandırılmamış
        if (failures.Count == attempted) return WebSearchOutcome.Fail(string.Join(" · ", failures));
        return WebSearchOutcome.Empty;                                     // çalıştı ama sonuç yok
    }
}
