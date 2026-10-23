using System.Text;
using System.Text.Json;

namespace WebApi.Services.Ai;

/// <summary>Tek bir arama sonucu satırı (ham SERP: başlık + snippet + link).</summary>
public sealed record WebSearchResult(string Title, string Snippet, string Url);

/// <summary>
/// Arama çağrısının sonucu. <see cref="Failed"/> ayrı taşınır çünkü "sonuç yok" ile
/// "arama çalışmadı" TAMAMEN farklı iki durum: ilki normal (zayıf dijital iz — ölçtük,
/// SESSİZ KALMAMALI — yoksa ücretli katman bir gün ölür ve kimse fark etmez.
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
/// Ham web arama sağlayıcısı. Ayrı arayüz çünkü sağlayıcı değiştirilebilir olmalı
/// (Serper ucuz ham SERP verir → özeti biz çıkarıyoruz; Tavily/Exa içerik çıkarımı dahil
/// ama ~10× pahalı). <see cref="IsConfigured"/> false ise arama katmanı SESSİZCE atlanır —
/// derin araştırma yine adayın kendi URL'leriyle çalışır.
/// </summary>
public interface IWebSearchProvider
{
    bool IsConfigured { get; }
    Task<WebSearchOutcome> SearchAsync(string query, int maxResults, CancellationToken ct = default);

    /// <summary>
    /// Aramayı bir ülkenin Google'ından yapar (<paramref name="countryCode"/>, ISO-3166 alfa-2; null = ülkesiz). Kaydolan
    /// uzmanın araması (VIT-1308): İrlanda'daki bir dermatolog İrlanda sonuçlarında öne çıkar. Ülke desteklemeyen sağlayıcı
    /// ülkesiz arar.
    /// </summary>
    Task<WebSearchOutcome> SearchAsync(string query, int maxResults, string? countryCode, CancellationToken ct = default) =>
        SearchAsync(query, maxResults, ct);
}

/// <summary>Arama servislerinin ülke parametresi (<c>gl</c>): iki harfli ülke kodu küçük harfle, değilse null.</summary>
public static class WebSearchCountry
{
    public static string? Gl(string? countryCode)
    {
        var code = countryCode?.Trim();
        return code is { Length: 2 } && code.All(char.IsAsciiLetter) ? code.ToLowerInvariant() : null;
    }
}

/// <summary>
/// ESKALASYON arama sağlayıcısı — hızlı SERP kademesinden AYRI bir seam. Kasten ayrı bir tip:
/// bu sağlayıcı pahalı ve yavaştır (ölçüm: 45–95 sn/sorgu), o yüzden zincire halka olarak
/// KONULMAZ; yalnız hızlı kademe yetersiz kaldığında tek seferlik çağrılır. Ayrı arayüz olması
/// DI'da yanlışlıkla <see cref="IWebSearchProvider"/> yerine geçmesini de engeller.
/// </summary>
public interface IEscalationSearchProvider : IWebSearchProvider;

/// <summary>
/// Serper.dev (Google SERP proxy) — ~$1/1000 sorgu. Anahtar: <c>Ai:Serper:ApiKey</c>
/// (SSM: /vitena/{env}/ai/serper-api-key → env Ai__Serper__ApiKey). Anahtar yoksa
/// IsConfigured=false döner ve arama adımı atlanır (özellik yine çalışır, sadece daha az kanıt).
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
                // 401/403 = anahtar sorunu, 429 = kota/rate — ikisi de "insan müdahalesi gerek".
                _logger.LogWarning("Serper arama başarısız: HTTP {Status} (anahtar/kota kontrol edilmeli)", status);
                return WebSearchOutcome.Fail($"HTTP {status}");
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("organic", out var organic) || organic.ValueKind != JsonValueKind.Array)
                return WebSearchOutcome.Empty;   // gerçekten sonuç yok — hata değil

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
            // Arama opsiyonel bir katman — patlaması derin araştırmayı durdurmaz, ama görünür olur.
            _logger.LogWarning(ex, "Serper arama hatası: {Query}", query);
            return WebSearchOutcome.Fail(ex.GetType().Name);
        }
    }
}
