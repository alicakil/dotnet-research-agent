using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace WebApi.Services.Ai;

/// <summary>
/// OpenAI Responses API'nin gömülü <c>web_search</c> aracıyla arama. Serper gibi ham SERP
/// satın almak yerine modelin kendi arama aracını kullanır.
///
/// Neden ikinci bir sağlayıcı: OpenAI anahtarı Lambda ortamında ZATEN var → yeni SSM parametresi
/// ve manuel <c>cdk deploy</c> gerekmeden çalışır. Serper devre dışıyken kanıt toplama ayakta kalır.
/// Yetenek ayrıca dikey-bağımsız: ileride başka özellikler de bu arayüzden faydalanabilir.
///
/// Kaynakları modelin ANLATISINDAN değil, yanıttaki <c>url_citation</c> annotation'larından okur —
/// makine üretimi, hallüsinasyona kapalı; snippet olarak atıf yapılan metin aralığı kullanılır.
/// Böylece kimlik eşleştirmesi için gereken (başlık + snippet + url) üçlüsü korunur.
///
/// ⚠️ İZLENEBİLİRLİK: bu çağrı adapter'ları BYPASS eder (Responses API farklı bir yüzey), bu yüzden
/// <c>AiTrace</c>'e satır DÜŞMEZ. Maliyet OpenAI Costs API'sinde (admin panelinde) görünür; buradaki
/// token kullanımı ayrıca log'a yazılır ki CloudWatch'tan izlenebilsin.
/// </summary>
public sealed class OpenAiWebSearchProvider : IWebSearchProvider
{
    private const string Endpoint = "https://api.openai.com/v1/responses";
    private const int MaxSnippetChars = 400;

    private readonly HttpClient _httpClient;
    private readonly AiOptions _options;
    private readonly ILogger<OpenAiWebSearchProvider> _logger;
    private readonly string? _apiKey;

    public OpenAiWebSearchProvider(
        HttpClient httpClient,
        IOptions<AiOptions> options,
        IConfiguration configuration,
        ILogger<OpenAiWebSearchProvider> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
        _apiKey = configuration["Ai:OpenAI:ApiKey"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        // Arama + sayfa okuma modelin tarafında olduğu için tek çağrı uzun sürebilir.
        _httpClient.Timeout = TimeSpan.FromSeconds(40);
    }

    /// <summary>Anahtar var AMA özellik bayrağı da açık olmalı — bkz.
    /// <see cref="AiOptions.EnableOpenAiWebSearch"/> (varsayılan kapalı: 36 sn gecikme + kimlik-dışı sonuçlar).</summary>
    public bool IsConfigured => _options.EnableOpenAiWebSearch && !string.IsNullOrWhiteSpace(_apiKey);

    public async Task<WebSearchOutcome> SearchAsync(string query, int maxResults, CancellationToken ct = default)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(query))
            return WebSearchOutcome.Empty;

        try
        {
            var body = new
            {
                model = _options.WebSearchModel,
                tools = new object[] { new { type = "web_search" } },
                // Model YORUM yapmasın, sadece arayıp kaynakları göstersin: kimlik kararını
                // sonraki (terra) sentez adımı veriyor, burada ham sinyal istiyoruz.
                input =
                    $"Search the public web for: {query}\n\n" +
                    "List the most relevant public professional sources you find (personal or clinic " +
                    "websites, professional directories, press). For each source write one short line " +
                    "stating what it shows about that person (profession, city, workplace, seniority). " +
                    "Do not speculate, do not merge different people, and do not conclude anything — " +
                    "just report what each source says. If you find nothing relevant, say so plainly.",
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request, ct);
            var payload = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("OpenAI web_search başarısız: HTTP {Status} {Body}",
                    (int)response.StatusCode, AiAdapterHelpers.Truncate(payload));
                return WebSearchOutcome.Fail($"HTTP {(int)response.StatusCode}");
            }

            var results = ParseCitations(payload, maxResults);
            LogUsage(payload, query, results.Count);
            return WebSearchOutcome.Ok(results);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OpenAI web_search hatası: {Query}", query);
            return WebSearchOutcome.Fail(ex.GetType().Name);
        }
    }

    /// <summary>
    /// Yanıtı gezip <c>output[].content[].annotations[]</c> içindeki <c>url_citation</c>'ları toplar.
    /// Snippet = atfın işaret ettiği metin aralığı (start_index..end_index); aralık bozuksa metnin başı.
    /// </summary>
    private static List<WebSearchResult> ParseCitations(string payload, int maxResults)
    {
        var results = new List<WebSearchResult>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var doc = JsonDocument.Parse(payload);
        if (!doc.RootElement.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
            return results;

        foreach (var item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("content", out var contents) || contents.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var content in contents.EnumerateArray())
            {
                var text = content.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String
                    ? t.GetString() ?? ""
                    : "";

                if (!content.TryGetProperty("annotations", out var annotations)
                    || annotations.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var a in annotations.EnumerateArray())
                {
                    if (!a.TryGetProperty("type", out var type) || type.GetString() != "url_citation") continue;

                    var url = a.TryGetProperty("url", out var u) ? u.GetString() : null;
                    if (string.IsNullOrWhiteSpace(url) || !seen.Add(url)) continue;

                    var title = a.TryGetProperty("title", out var ti) ? ti.GetString() ?? "" : "";
                    results.Add(new WebSearchResult(title, ExtractSnippet(text, a), url));

                    if (results.Count >= maxResults) return results;
                }
            }
        }

        return results;
    }

    private static string ExtractSnippet(string text, JsonElement annotation)
    {
        if (text.Length == 0) return "";

        var start = annotation.TryGetProperty("start_index", out var s) && s.ValueKind == JsonValueKind.Number
            ? s.GetInt32() : 0;
        var end = annotation.TryGetProperty("end_index", out var e) && e.ValueKind == JsonValueKind.Number
            ? e.GetInt32() : 0;

        // Bozuk/ters aralık → metnin başından bir parça al (kaynak yine de kullanılabilir olsun).
        if (start < 0 || end <= start || start >= text.Length)
            return text[..Math.Min(MaxSnippetChars, text.Length)].Trim();

        end = Math.Min(end, text.Length);
        var span = text[start..end];
        return span.Length > MaxSnippetChars ? span[..MaxSnippetChars].Trim() : span.Trim();
    }

    /// <summary>Token kullanımını log'a yazar — bu çağrı AiTrace'e düşmediği için tek görünürlük burası.</summary>
    private void LogUsage(string payload, string query, int resultCount)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty("usage", out var usage))
            {
                var input = usage.TryGetProperty("input_tokens", out var i) ? i.GetInt32() : 0;
                var output = usage.TryGetProperty("output_tokens", out var o) ? o.GetInt32() : 0;
                _logger.LogInformation(
                    "OpenAI web_search: model={Model} in={In} out={Out} sonuç={Count} sorgu={Query}",
                    _options.WebSearchModel, input, output, resultCount, AiAdapterHelpers.Truncate(query));
                return;
            }
        }
        catch { /* usage okunamadıysa sorun değil — sonucu etkilemez */ }

        _logger.LogInformation("OpenAI web_search: sonuç={Count} sorgu={Query}", resultCount, AiAdapterHelpers.Truncate(query));
    }
}
