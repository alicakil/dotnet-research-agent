using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace WebApi.Services.Ai;

/// <summary>
/// JS RENDER EDEN çekme kolu. Doğrudan HTTP çekimiyle
/// (<see cref="WebPageFetcher"/>) okunamayan sayfalar için ikinci kademe.
///
/// istemci tarafında render ediyor; düz HTTP çekimi boş bir kabuk görüyor.
///
/// ⚠️ Bu kol PAHALI ve KOTALI olduğu için ASLA ilk sırada çalışmaz — bkz.
/// <see cref="CompositeWebPageFetcher"/>. Kotayı, düz çekimin zaten hallettiği sayfalara
/// harcamak, gerçekten ihtiyaç duyulan sayfa için kota bırakmamak demek.
///
/// Anahtar yoksa <see cref="IsConfigured"/> false döner ve kol zincirden sessizce düşer;
/// araştırma bugünkü davranışıyla devam eder.
/// </summary>
public sealed class FirecrawlPageFetcher : IWebPageFetcher
{
    private const string Endpoint = "https://api.firecrawl.dev/v1/scrape";
    private const int MaxChars = 20_000;

    private readonly HttpClient _http;
    private readonly string? _apiKey;
    private readonly ILogger<FirecrawlPageFetcher> _logger;

    public FirecrawlPageFetcher(
        HttpClient http, IConfiguration configuration, ILogger<FirecrawlPageFetcher> logger)
    {
        _http = http;
        _logger = logger;
        _apiKey = configuration["Ai:Firecrawl:ApiKey"]
                  ?? Environment.GetEnvironmentVariable("FIRECRAWL_API_KEY");

        _http.Timeout = TimeSpan.FromSeconds(45);
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);

    public async Task<FetchedPage> FetchAsync(string url, CancellationToken ct = default)
    {
        if (!IsConfigured)
            return new FetchedPage(url, false, null, "firecrawl_not_configured");

        // SSRF ve giriş duvarı kuralları BURADA DA geçerli. Render eden bir kol, engelli host
        // onların şartlarına aykırı olurdu, ve zaten e-posta vermezdi.
        if (WebPageFetcher.IsBlockedHost(url))
            return new FetchedPage(url, false, null, "blocked_host");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            request.Content = JsonContent.Create(new
            {
                url,
                formats = new[] { "markdown" },
                onlyMainContent = true,
            });

            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return new FetchedPage(url, false, null, $"firecrawl_http_{(int)response.StatusCode}");

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("data", out var data))
                return new FetchedPage(url, false, null, "firecrawl_no_data");

            var text = data.TryGetProperty("markdown", out var md) && md.ValueKind == JsonValueKind.String
                ? md.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(text))
                return new FetchedPage(url, false, null, "firecrawl_empty");

            var final = data.TryGetProperty("metadata", out var meta)
                        && meta.TryGetProperty("sourceURL", out var src)
                        && src.ValueKind == JsonValueKind.String
                ? src.GetString() ?? url
                : url;

            // Başlık (VIT-1185): adı yazılmadan linkle açılan kliniğin adı buradan çıkarılır.
            string? title = null;
            if (data.TryGetProperty("metadata", out var m))
                foreach (var key in new[] { "ogTitle", "og:title", "title" })
                    if (m.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(v.GetString()))
                    {
                        title = v.GetString()!.Trim();
                        break;
                    }

            return new FetchedPage(final, true, Truncate(text!), null, Title: title);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Firecrawl çekimi başarısız: {Url}", url);
            return new FetchedPage(url, false, null, "firecrawl_failed");
        }
    }

    private static string Truncate(string text) =>
        text.Length <= MaxChars ? text : text[..MaxChars];
}

/// <summary>
/// İki kademeli sayfa çekme: ÖNCE ucuz düz HTTP, gerekirse JS render eden kol.
/// Aynı desen arama tarafında zaten kurulu (<see cref="CompositeWebSearchProvider"/>) —
/// ucuzdan pahalıya, ilk başarılıda dur.
///
/// Eskalasyon KOŞULLU, çünkü render eden kol kotalı. Üç tetik:
///  • düz çekim başarısızsa,
///  • gövde şüpheli derecede kısaysa (400 karakterin altı),
///  • gövde AYNI HOST'TA daha önce görülen bir gövdeyle BİREBİR AYNIYSA → SPA kabuğu.
///
/// düz çekimde **tam olarak aynı 2638 karakteri** döndürüyor — tek sayfalık uygulama her rotada
/// aynı HTML iskeletini veriyor, içerik istemcide render ediliyor. Kabuk 2638 karakter olduğu
/// için "kısa gövde" kuralı bunu YAKALAMIYORDU ve kol sessizce hiç devreye girmeyecekti.
/// Aynı sayfada Firecrawl 7712 karakterle gerçek ekip listesini getirdi.
///
/// Tekrar tespiti istek kapsamında (scoped) tutuluyor: bir araştırma turu boyunca hatırlıyor,
/// turlar arasında taşınmıyor.
///
/// Kalıcı engeller eskalasyona GİRMEZ: engelli host, giriş duvarı ve geçersiz adres, render
/// edilse de aynı sonucu verir — o kotayı harcamak boşuna.
/// </summary>
public sealed class CompositeWebPageFetcher(
    WebPageFetcher primary,
    FirecrawlPageFetcher rendered,
    ILogger<CompositeWebPageFetcher> logger) : IWebPageFetcher
{
    private const int ThinBodyThreshold = 400;

    /// <summary>
    /// Render etmenin DEĞİŞTİREMEYECEĞİ arızalar. 404/410 bu listede olmalı ve olmaması prod'da
    /// ölçüldü (24 Ağu): yol tahmini <c>/equipo</c>'yu deniyor, site 404 dönüyor, düz çekim doğru
    /// şekilde eliyor — ama composite "başarısız" görüp Firecrawl'a geçiyordu ve Firecrawl
    /// "Página no encontrada" sayfasını sorunsuz render edip BAŞARILI dönüyordu. Var olmayan bir
    /// sayfa böylece kanıt yığınına giriyordu.
    ///
    /// 403 bilerek DIŞARIDA: orası çoğu zaman bot engeli ve render eden kolun çözebileceği tek
    /// HTTP arızası o.
    /// </summary>
    private static readonly string[] PermanentSkips =
    [
        "blocked_host", "blocked_host_after_redirect", "login_wall", "invalid_url", "empty_url",
        "http_404", "http_410", "http_400", "http_401",
        // Alan adı yoksa (NXDOMAIN) ya da hedef özel ağdaysa render edecek bir sayfa da yok.
        "host_not_found", "private_address_blocked"
    ];

    /// <summary>host → o host'ta düz çekimden dönen gövdelerin parmak izleri.</summary>
    private readonly Dictionary<string, HashSet<string>> _seenBodies = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Bu turda ULAŞILAMADIĞI kanıtlanmış host'lar. Ölü bir host'a her yol denemesi iki kez
    /// para/zaman yakıyor (düz çekim zaman aşımı + render eden kol), ve araştırmanın 180 sn'lik
    /// bütçesi bununla doluyor.
    ///
    /// Ölçüldü (DENTICITY, 27 Ağu 2026): denticity.com'a dört adres denendi, her biri ~38 sn
    /// (8 sn zaman aşımı + ~30 sn Firecrawl) yaktı. 152 saniye sonra bütçe bitti ve SIRADAKİ ADIM —
    /// elimizdeki ÇALIŞAN kanıtı da öldürdü.
    ///
    /// Host bir kez ölü işaretlenince o turdaki kalan adresleri anında elenir. Turlar arasında
    /// taşınmaz (scoped): geçici bir arıza kliniği kalıcı olarak kör etmemeli.
    /// </summary>
    private readonly HashSet<string> _deadHosts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Taşıma katmanı arızaları — sayfa yok değil, host'a ULAŞILAMIYOR.</summary>
    private static readonly string[] UnreachableReasons = ["timeout", "fetch_error", "dns_error"];

    public async Task<FetchedPage> FetchAsync(string url, CancellationToken ct = default)
    {
        var host = HostOf(url);
        if (_deadHosts.Contains(host))
            return new FetchedPage(url, false, null, "host_unreachable");

        var page = await primary.FetchAsync(url, ct);

        if (!rendered.IsConfigured)
        {
            if (IsUnreachable(page)) MarkDead(host, url);
            return page;
        }
        if (page.SkipReason is { } reason && PermanentSkips.Contains(reason)) return page;

        var length = page.Text?.Length ?? 0;
        var thin = page.Success && length < ThinBodyThreshold;
        var shell = page.Success && IsRepeatedBody(url, page.Text);

        if (page.Success && !thin && !shell) return page;

        logger.LogInformation(
            "Düz çekim yetersiz ({Sebep}) — JS render eden kola geçiliyor: {Url}",
            !page.Success ? page.SkipReason
                : shell ? $"aynı host'ta birebir aynı gövde ({length} karakter) → SPA kabuğu"
                : $"gövde {length} karakter",
            url);

        var viaRender = await rendered.FetchAsync(url, ct);

        // İki kol da ulaşamadıysa host ölü sayılır: aynı sitenin diğer adreslerini denemek
        // bütçeyi yakmaktan başka bir şey yapmıyor.
        if (!viaRender.Success && IsUnreachable(page)) MarkDead(host, url);

        // Render de tutmazsa DÜZ ÇEKİMİN sonucu korunur: zayıf bir gövde, hiç gövdeden iyidir
        // ve arıza sebebi ilk çekimin sebebidir — onu ikinci kolun mesajıyla değiştirmek
        // teşhisi bozardı.
        return viaRender.Success ? viaRender : page;
    }

    private static bool IsUnreachable(FetchedPage page) =>
        !page.Success && page.SkipReason is { } r && UnreachableReasons.Contains(r);

    private void MarkDead(string host, string url)
    {
        if (host.Length == 0 || !_deadHosts.Add(host)) return;
        logger.LogInformation("Host ulaşılamıyor, bu turda kalan adresleri elenecek: {Host} ({Url})", host, url);
    }

    private static string HostOf(string url) =>
        Uri.TryCreate(WebPageFetcher.NormalizeUrl(url), UriKind.Absolute, out var uri) ? uri.Host : string.Empty;

    /// <summary>
    /// Bu gövde, aynı host'ta daha önce görülenin AYNISI mı? Öyleyse sayfaya özel içerik yok
    /// demektir — SPA iskeleti. İlk görülüşte false döner (kıyaslanacak bir şey yok) ve gövde
    /// kaydedilir; asıl yakalama ikinci sayfada olur, ki bizim için o sayfa ekip sayfasıdır.
    /// </summary>
    private bool IsRepeatedBody(string url, string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;
        if (!_seenBodies.TryGetValue(host, out var bodies))
            _seenBodies[host] = bodies = new HashSet<string>(StringComparer.Ordinal);

        // Uzunluk + baş/son parçası: tam metni sözlükte tutmak, sayfa başına on binlerce
        // karakteri istek boyunca bellekte taşımak olurdu.
        var head = text.Length <= 200 ? text : text[..200];
        var tail = text.Length <= 200 ? string.Empty : text[^200..];
        return !bodies.Add($"{text.Length}|{head}|{tail}");
    }
}
