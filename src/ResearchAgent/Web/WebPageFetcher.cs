using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WebApi.Services.Ai;

/// <summary>
/// Tek bir sayfa çekme sonucu. <see cref="Text"/> doluysa kullanılabilir içerik var.
///
/// <see cref="Structured"/> sayfanın JSON-LD'sinden süzülen mesleki profil özeti (varsa). Düz
/// metinden AYRI tutuluyor çünkü kalitesi bambaşka: puan, yorum sayısı ve yorum metinleri orada
/// etiketli duruyor — 29 bin karakterlik menü/footer gürültüsünün içinden çıkarılmasına gerek
/// dolu; sıradan sitelerde boş ve bu normaldir.
/// </summary>
public sealed record FetchedPage(string Url, bool Success, string? Text, string? SkipReason, string? Structured = null);

/// <summary>Sayfa çekme seam'i — derin araştırma servisi buna bağlanır, testler stub verir (ağ yok).</summary>
public interface IWebPageFetcher
{
    Task<FetchedPage> FetchAsync(string url, CancellationToken ct = default);
}

/// <summary>
/// Kullanıcının VERDİĞİ URL'leri sunucu tarafından güvenle çekip düz metne indirger
/// (derin araştırma kanıt toplama katmanı). Tasarım kısıtları:
///
///  • SSRF çekirdekte: yalnız http/https, DNS çözümü sonrası özel/loopback/link-local/CGNAT
///    adresleri reddedilir, yönlendirmeler ELDE takip edilir ve her hop yeniden doğrulanır.
///    denemek boşa gecikme, ayrıca ToS ihlali riski).
///  • Bütçe: host başına timeout, indirme boyutu tavanı, metin karakteri tavanı.
///
/// Hata TOLERANSLI: her başarısızlık <see cref="FetchedPage"/> içinde sebebiyle döner, exception
/// fırlatmaz — derin araştırma katmanı fail etse bile A/B/C kararı ayakta kalmalı.
/// </summary>
public sealed class WebPageFetcher(HttpClient httpClient, ILogger<WebPageFetcher> logger) : IWebPageFetcher
{
    // 5, çünkü zincir bazen bir TUR ile başlıyor: sunucu önce dil çerezini basıp aynı adrese
    // yönlendiriyor (ASP.NET kültür çerezi), asıl alan adına ancak ondan sonra gidiyor
    // (denticity.com → 302 / → 302 www.denticity.com.tr). 3 hop bu tür zincirlerde biterken
    // yakalanıyordu. Çerezler tutuluyor (handler UseCookies) — yoksa tur sonsuza döner.
    private const int MaxRedirects = 5;
    private const int MaxDownloadBytes = 1_500_000;
    private const int MaxTextChars = 8_000;
    private static readonly TimeSpan PerUrlTimeout = TimeSpan.FromSeconds(8);

    /// <summary>Çekmeyi denemeyeceğimiz host'lar (login duvarı / aktif bot bloğu).</summary>
    private static readonly string[] BlockedHosts =
    {
        "linkedin.com", "lnkd.in",       // HTTP 999 — authwall, datacenter IP'leri ASN'de bloklu
        "facebook.com", "fb.com",        // login duvarı
        "x.com", "twitter.com",          // login duvarı
    };

    /// <summary>İçerik verimi düşük ama denemeye değer host'lar (og etiketleri/bio çıkabilir).</summary>
    private static readonly string[] LowYieldHosts = { "instagram.com", "youtube.com", "tiktok.com" };

    public static bool IsBlockedHost(string url) =>
        TryGetHost(url, out var host) && BlockedHosts.Any(b => host == b || host.EndsWith("." + b));

    /// <summary>
    /// Giriş duvarına yönlendiren yollar. Bunlar 200 döner ve gövdeleri 80 karakterden uzundur,
    /// yani kontrol edilmezse BAŞARILI bir çekim gibi görünür — kanıt yığınına bir oturum açma
    /// <c>/accounts/login/?next=…</c> adresine düştü ve o sayfa kanıt sayıldı. Model neyse ki
    /// "içerik görülemedi" dedi, ama buna güvenmek zorunda kalmamalıyız: okunamayan bir kaynağı
    /// okunmuş gibi sunmak, kanıt disiplinini temelden bozar.
    /// </summary>
    private static readonly string[] LoginPaths =
        ["/accounts/login", "/login", "/signin", "/sign-in", "/auth/login", "/users/sign_in"];

    private static bool IsLoginWall(Uri uri) =>
        LoginPaths.Any(p => uri.AbsolutePath.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    public static bool IsLowYieldHost(string url) =>
        TryGetHost(url, out var host) && LowYieldHosts.Any(b => host == b || host.EndsWith("." + b));

    /// <summary>
    /// Kullanıcının yazdığı adresi çekilebilir bir URL'ye çevirir. Form alanına şema YAZILMAZ —
    /// insanlar "sitem.com" yazar, "https://sitem.com" değil. Prod'da bu yüzden bir adayın kendi
    /// sitesi (en değerli, kimliği sabit kanıt kaynağı) sessizce "invalid_url" ile atlanmıştı.
    /// Şema varsa dokunulmaz; yoksa https varsayılır (http gerekiyorsa yönlendirme takip ediliyor).
    ///
    /// NOKTA ŞARTI kasıtlı: şemayı her girdiye eklemek "not-a-url" gibi çöpü de geçerli bir
    /// mutlak URI yapar, sonra DNS çözülemeyince <c>private_address_blocked</c> ile reddedilir —
    /// yani çöp girdi, SSRF girişimiyle AYNI sebep koduna düşer ve o kod anlamını yitirir.
    /// Noktasız bir host adı zaten hiçbir zaman kamuya açık bir site değildir.
    /// </summary>
    public static string NormalizeUrl(string url)
    {
        var trimmed = (url ?? string.Empty).Trim();
        if (trimmed.Length == 0) return trimmed;
        if (trimmed.Contains("://", StringComparison.Ordinal)) return trimmed;

        var hostPart = trimmed.Split('/', '?', '#')[0];
        return hostPart.Contains('.', StringComparison.Ordinal) ? "https://" + trimmed : trimmed;
    }

    public async Task<FetchedPage> FetchAsync(string url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            return new FetchedPage(url, false, null, "empty_url");

        url = NormalizeUrl(url);

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return new FetchedPage(url, false, null, "invalid_url");

        // Giriş sayfası hem başlangıçta verilebilir hem de yönlendirmeyle karşımıza çıkabilir;
        // ikisinde de içerik yok, sadece oturum açma formu var.
        if (IsLoginWall(uri))
            return new FetchedPage(url, false, null, "login_wall");

        if (IsBlockedHost(url))
            return new FetchedPage(url, false, null, "blocked_host");

        var page = await AttemptAsync(url, uri, ct);

        // HTTPS'İ OLMAYAN SİTE. Küçük işletme sitelerinde sık: 443 sessizce yutuluyor (RST bile
        // dönmüyor, bağlantı asılı kalıyor), 80 çalışıyor ve oradan gerçek adrese yönlendiriyor.
        // Tarayıcı bunu görmez, çünkü https başarısız olunca kendisi http'ye düşer — biz düşmüyorduk.
        //
        // Ölçüldü (DENTICITY, 27 Ağu 2026): kayıtlı adres https://denticity.com. 443 zaman aşımına
        // düşüyor, http://denticity.com ise 302 ile www.denticity.com.tr'ye gidiyor ve site orada.
        //
        // Yalnız TAŞIMA katmanı arızasında denenir: 404/403 gibi bir CEVAP geldiyse site https
        // konuşuyor demektir, şemayı düşürmek yanlış olurdu.
        if (!page.Success
            && page.SkipReason is "timeout" or "fetch_error"
            && uri.Scheme == Uri.UriSchemeHttps
            && uri.IsDefaultPort)
        {
            var overHttp = new UriBuilder(uri) { Scheme = Uri.UriSchemeHttp, Port = -1 }.Uri;
            var viaHttp = await AttemptAsync(url, overHttp, ct);
            if (viaHttp.Success)
            {
                logger.LogInformation("HTTPS yanıt vermedi, http'ye düşüldü: {Url}", url);
                return viaHttp;
            }
        }

        return page;
    }

    /// <summary>
    /// Tek bir şemayla çekme denemesi. Kendi zaman aşımı saatiyle çalışır: http'ye düşme
    /// denemesinin, https'in tükettiği süreden arta kalanla değil, tam bütçeyle koşması gerekiyor.
    /// </summary>
    private async Task<FetchedPage> AttemptAsync(string url, Uri start, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(PerUrlTimeout);

        try
        {
            var current = start;
            for (var hop = 0; hop <= MaxRedirects; hop++)
            {
                if (await ResolveBlockReasonAsync(current, cts.Token) is { } blockReason)
                    return new FetchedPage(url, false, null, blockReason);

                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                // Bazı siteler UA'sız isteklere 403 veriyor; kimliğimizi dürüstçe belirtiyoruz.
                request.Headers.TryAddWithoutValidation("User-Agent",
                    "Mozilla/5.0 (compatible; VitenaBot/1.0; +https://vitena.care)");
                request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml");

                using var response = await httpClient.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

                if ((int)response.StatusCode is >= 300 and < 400
                    && response.Headers.Location is { } location)
                {
                    current = location.IsAbsoluteUri ? location : new Uri(current, location);
                    if (IsBlockedHost(current.ToString()))
                        return new FetchedPage(url, false, null, "blocked_host_after_redirect");
                    if (IsLoginWall(current))
                        return new FetchedPage(url, false, null, "login_wall");
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                    return new FetchedPage(url, false, null, $"http_{(int)response.StatusCode}");

                var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
                if (!mediaType.Contains("html") && !mediaType.StartsWith("text/"))
                    return new FetchedPage(url, false, null, $"unsupported_content_type:{mediaType}");

                var html = await ReadCappedAsync(response, cts.Token);
                var text = HtmlToText(html);
                if (text.Length > MaxTextChars) text = text[..MaxTextChars];
                var structured = ExtractStructuredProfile(html);

                // Yapılandırılmış veri VARSA düz metin kısa kalsa bile sayfa işe yarar: dizin
                // profillerinin değerli kısmı zaten JSON-LD'de duruyor.
                return text.Length < 80 && structured is null
                    ? new FetchedPage(current.ToString(), false, null, "too_little_text")
                    : new FetchedPage(current.ToString(), true, text, null, structured);
            }

            return new FetchedPage(url, false, null, "too_many_redirects");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new FetchedPage(url, false, null, "timeout");
        }
        catch (Exception ex)
        {
            logger.LogInformation("WebPageFetcher {Url} başarısız: {Message}", url, ex.Message);
            return new FetchedPage(url, false, null, "fetch_error");
        }
    }

    private static async Task<string> ReadCappedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[8192];
        using var ms = new MemoryStream();
        int read;
        while (ms.Length < MaxDownloadBytes
               && (read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            ms.Write(buffer, 0, read);
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>
    /// SSRF koruması: host'un çözümlendiği TÜM adresler public olmalı. Lambda VPC içinde
    /// çalışıyor — 10.x, 169.254.x (IMDS), 127.x, ::1 gibi hedefler kesinlikle dışarı kapalı.
    ///
    /// Dönen sebep kodu AYRIŞTIRILMIŞTIR. İkisi de "çekmedik" ile biter ama anlamları taban tabana
    /// zıttır: <c>private_address_blocked</c> güvenlik kararıdır, <c>host_not_found</c> ise adresin
    /// hiç var olmadığını söyler. Tek kod altında toplanınca operatöre yanlış bilgi gidiyordu —
    /// raporlandı, ki bu düzeltilebilir bir arıza izlenimi verir. Sebep koduna güvenip
    /// araştıracağımız için doğru olması gerekiyor.
    /// </summary>
    public static async Task<string?> ResolveBlockReasonAsync(Uri uri, CancellationToken ct)
    {
        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(uri.Host, out var literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(uri.Host, ct);
        }
        catch (SocketException)
        {
            return "host_not_found";     // NXDOMAIN — böyle bir alan adı yok
        }
        catch
        {
            return "dns_error";          // çözümleme başarısız (timeout, geçici arıza)
        }

        if (addresses.Length == 0) return "host_not_found";
        return addresses.All(IsPublic) ? null : "private_address_blocked";
    }

    /// <summary>
    /// Adres dışarıya açık mı: özel ağ, loopback, link-local, CGNAT ve ayrılmış aralıklar değil. Sağlık öyküsünün
    /// dosya çekicisi de aynı kurala bakar (HealthRemoteFileFetcher): kural tek yerde dursun.
    /// </summary>
    public static bool IsPublic(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return false;

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal) return false;
            if (ip.IsIPv4MappedToIPv6) return IsPublic(ip.MapToIPv4());
            return !ip.Equals(IPAddress.IPv6Any);
        }

        var b = ip.GetAddressBytes();
        return b[0] switch
        {
            0 or 10 or 127 => false,                       // this-network, private, loopback
            100 => b[1] < 64 || b[1] > 127,                // 100.64/10 CGNAT
            169 => b[1] != 254,                            // 169.254/16 link-local (IMDS!)
            172 => b[1] < 16 || b[1] > 31,                 // 172.16/12 private
            192 => !(b[1] == 168 || (b[1] == 0 && b[2] == 2)), // 192.168/16, 192.0.2/24
            198 => !(b[1] >= 18 && b[1] <= 19),            // 198.18/15 benchmark
            >= 224 => false,                               // multicast + reserved
            _ => true,
        };
    }

    // ── HTML → düz metin ──────────────────────────────────────────────────────

    private static readonly Regex ScriptStyleRegex =
        new(@"<(script|style|noscript|svg)[^>]*>.*?</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
    private static readonly Regex TagRegex = new(@"<[^>]+>");
    private static readonly Regex WhitespaceRegex = new(@"[ \t   ]{2,}");
    private static readonly Regex MetaTagRegex = new(@"<meta\b[^>]*>", RegexOptions.IgnoreCase);

    /// <summary>
    /// Boşluktan ibaret satırlar tek satır sonuna iner. Ölçüldü (16 Eki 2026, VIT-1020): bir doktortakvimi profilinin düz
    /// metni 32 bin karakterdi ve bunun üçte biri boşluk satırlarıydı; 8.000 karakterlik tavan eğitim ve deneyim
    /// bölümlerine (8.700-10.600) varmadan doluyordu. Sıkıştırınca aynı tavana özgeçmişin tamamı sığıyor (6.200-7.500),
    /// hasta yorumları (9.000'den sonra) dışarıda kalıyor. Tavan aynı: modele giden metin büyümedi, boşluğu gitti.
    /// </summary>
    private static readonly Regex BlankLinesRegex = new(@"[ \t]*\n\s*");
    /// <summary>Öznitelik değeri — kapanış tırnağı AÇILIŞ tırnağıyla eşlenir; aksi halde
    /// içerikteki kesme işareti ("Bursa'da …") değeri erken keser.</summary>
    private static readonly Regex AttrRegex =
        new(@"\b(name|property|content)\s*=\s*(?:(""|')(?<v>.*?)\2|(?<v>[^\s""'>]+))",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly string[] WantedMeta = { "description", "og:description", "og:title" };

    /// <summary>Görünür metni çıkarır; meta description/og:description'ı da başa ekler
    public static string HtmlToText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;

        // Meta etiketleri tek tek işlenir: öznitelik SIRASI değişken (content önce de gelebilir),
        // tek mega-regex bunu güvenilir yakalamıyor.
        var metaParts = new List<string>();
        foreach (Match tag in MetaTagRegex.Matches(html))
        {
            string? key = null, content = null;
            foreach (Match attr in AttrRegex.Matches(tag.Value))
            {
                var value = attr.Groups["v"].Value;
                switch (attr.Groups[1].Value.ToLowerInvariant())
                {
                    case "name" or "property": key = value.ToLowerInvariant(); break;
                    case "content": content = value; break;
                }
            }
            if (key is not null && content is { Length: >= 10 } && WantedMeta.Contains(key))
                metaParts.Add(Normalize(WebUtility.HtmlDecode(content)));
        }

        var body = ScriptStyleRegex.Replace(html, " ");
        body = TagRegex.Replace(body, " ");
        body = BlankLinesRegex.Replace(Normalize(WebUtility.HtmlDecode(body)), "\n");

        return metaParts.Count > 0
            ? string.Join(" · ", metaParts.Distinct()) + "\n\n" + body
            : body;
    }

    // ── JSON-LD → mesleki profil özeti ────────────────────────────────────────

    private static readonly Regex JsonLdRegex =
        new(@"<script[^>]+type\s*=\s*[""']application/ld\+json[""'][^>]*>(.*?)</script>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

    /// <summary>Kişi/işletme anlamına gelen schema.org tipleri — dizinlerde profil bunlardan biridir.</summary>
    private static readonly HashSet<string> ProfileTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Physician", "Person", "MedicalBusiness", "MedicalClinic", "LocalBusiness",
        "Dentist", "HealthAndBeautyBusiness", "ProfessionalService",
    };

    private const int MaxStructuredChars = 1_800;
    private const int MaxReviews = 3;

    /// <summary>
    /// Sayfanın JSON-LD bloklarından mesleki profil alanlarını süzer.
    ///
    /// Neden beyaz liste: JSON-LD'nin tamamı yüz binlerce karakter olabiliyor (navigasyon, breadcrumb,
    /// site şeması) ve ham hâlde modele vermek hem pahalı hem gürültülü. Buradaki alanlar, uygunluk
    /// puanını gerçekten hareket ettiren şeyler: kendi muayenehanesi var mı, randevu/fiyat açık mı,
    /// kaç yorum almış — yani "kendi danışan akışına sahip mi" sorusunun cevabı.
    ///
    /// TAM TOLERANSLI: bozuk/kesik JSON sessizce atlanır (indirme tavanı bir bloğu ortadan kesmiş
    /// olabilir). Hiçbir şey bulunamazsa null döner ve çağıran düz metinle devam eder.
    /// </summary>
    public static string? ExtractStructuredProfile(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;

        var lines = new List<string>();
        foreach (Match block in JsonLdRegex.Matches(html))
        {
            if (lines.Count > 0 && string.Join('\n', lines).Length >= MaxStructuredChars) break;
            try
            {
                using var doc = JsonDocument.Parse(WebUtility.HtmlDecode(block.Groups[1].Value).Trim());
                CollectProfiles(doc.RootElement, lines, 0);
            }
            catch { /* bozuk veya kesik blok — atla */ }
        }

        if (lines.Count == 0) return null;
        var joined = string.Join('\n', lines.Distinct());
        return joined.Length > MaxStructuredChars ? joined[..MaxStructuredChars] : joined;
    }

    /// <summary>JSON-LD ağacında gezer: dizi, @graph ve iç içe düğümler hepsi olabiliyor.</summary>
    private static void CollectProfiles(JsonElement node, List<string> lines, int depth)
    {
        if (depth > 4 || lines.Count > 40) return;

        if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray()) CollectProfiles(item, lines, depth + 1);
            return;
        }

        if (node.ValueKind != JsonValueKind.Object) return;

        if (node.TryGetProperty("@graph", out var graph)) CollectProfiles(graph, lines, depth + 1);

        var type = node.TryGetProperty("@type", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString()
            : null;

        if (type is not null && ProfileTypes.Contains(type))
            Emit(node, type, lines);
    }

    private static void Emit(JsonElement node, string type, List<string> lines)
    {
        var name = Text(node, "name");
        if (string.IsNullOrWhiteSpace(name)) return;

        lines.Add($"[{type}] {name}");
        foreach (var field in new[] { "jobTitle", "description", "telephone", "email", "url", "priceRange", "medicalSpecialty" })
        {
            var v = Text(node, field);
            if (!string.IsNullOrWhiteSpace(v)) lines.Add($"  {field}: {Cap(v!, 300)}");
        }

        if (node.TryGetProperty("address", out var addr))
        {
            var parts = new[] { "streetAddress", "addressLocality", "addressRegion", "postalCode" }
                .Select(k => Text(addr.ValueKind == JsonValueKind.Array && addr.GetArrayLength() > 0 ? addr[0] : addr, k))
                .Where(v => !string.IsNullOrWhiteSpace(v));
            var joined = string.Join(", ", parts);
            if (joined.Length > 0)
            {
                // Birden çok adres = birden çok çalışma yeri; uygunluk açısından anlamlı bir sinyal.
                var count = addr.ValueKind == JsonValueKind.Array ? addr.GetArrayLength() : 1;
                lines.Add($"  address: {Cap(joined, 200)}{(count > 1 ? $" (+{count - 1} adres daha)" : "")}");
            }
        }

        if (node.TryGetProperty("aggregateRating", out var rating) && rating.ValueKind == JsonValueKind.Object)
        {
            var value = Text(rating, "ratingValue");
            var count = Text(rating, "reviewCount") ?? Text(rating, "ratingCount");
            if (value is not null) lines.Add($"  rating: {value}{(count is not null ? $" ({count} yorum)" : "")}");
        }

        if (node.TryGetProperty("review", out var reviews))
        {
            var items = reviews.ValueKind == JsonValueKind.Array
                ? reviews.EnumerateArray().Take(MaxReviews)
                : new[] { reviews }.AsEnumerable();
            foreach (var r in items)
            {
                var body = Text(r, "reviewBody");
                if (string.IsNullOrWhiteSpace(body)) continue;
                var date = Text(r, "datePublished");
                lines.Add($"  review{(date is not null ? $" ({date[..Math.Min(10, date.Length)]})" : "")}: {Cap(body!, 220)}");
            }
        }
    }

    /// <summary>Alanı metin olarak okur; schema.org'da aynı alan string, sayı veya {name:...} olabiliyor.</summary>
    private static string? Text(JsonElement node, string property)
    {
        if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(property, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.ToString(),
            JsonValueKind.Object => Text(v, "name"),
            JsonValueKind.Array when v.GetArrayLength() > 0 =>
                string.Join(", ", v.EnumerateArray().Take(4)
                    .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : Text(x, "name"))
                    .Where(s => !string.IsNullOrWhiteSpace(s))),
            _ => null,
        };
    }

    private static string Cap(string s, int max)
    {
        var clean = Normalize(WebUtility.HtmlDecode(s)).Replace('\n', ' ');
        return clean.Length > max ? clean[..max] + "…" : clean;
    }

    /// <summary>NBSP ve akraba Unicode boşlukları normal boşluğa çevirip sıkıştırır — modele
    /// giden metinde görünmez karakter bırakmamak için (token şişmesi + eşleşme sorunları).</summary>
    private static string Normalize(string s) =>
        WhitespaceRegex.Replace(
            s.Replace(' ', ' ').Replace(' ', ' ').Replace(' ', ' ')
             .Replace("\r\n", "\n"),
            " ").Trim();

    private static bool TryGetHost(string url, out string host)
    {
        host = string.Empty;
        // TryGetHost false döner ve IsBlockedHost onu "bloklu değil" sayar — blok listesinde delik.
        if (!Uri.TryCreate(NormalizeUrl(url ?? string.Empty), UriKind.Absolute, out var uri)) return false;
        host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.")) host = host[4..];
        return true;
    }
}
