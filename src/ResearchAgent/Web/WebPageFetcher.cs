using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace WebApi.Services.Ai;

/// <summary>Tek bir sayfa çekme sonucu. <see cref="Text"/> doluysa kullanılabilir içerik var.</summary>
public sealed record FetchedPage(string Url, bool Success, string? Text, string? SkipReason);

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
    private const int MaxRedirects = 3;
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

        if (IsBlockedHost(url))
            return new FetchedPage(url, false, null, "blocked_host");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(PerUrlTimeout);

        try
        {
            var current = uri;
            for (var hop = 0; hop <= MaxRedirects; hop++)
            {
                if (!await IsPubliclyRoutableAsync(current, cts.Token))
                    return new FetchedPage(url, false, null, "private_address_blocked");

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

                return text.Length < 80
                    ? new FetchedPage(current.ToString(), false, null, "too_little_text")
                    : new FetchedPage(current.ToString(), true, text, null);
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
    /// </summary>
    private static async Task<bool> IsPubliclyRoutableAsync(Uri uri, CancellationToken ct)
    {
        try
        {
            var addresses = IPAddress.TryParse(uri.Host, out var literal)
                ? new[] { literal }
                : await Dns.GetHostAddressesAsync(uri.Host, ct);

            return addresses.Length > 0 && addresses.All(IsPublic);
        }
        catch
        {
            return false;   // çözümlenemeyen host = denenmez
        }
    }

    private static bool IsPublic(IPAddress ip)
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
        body = Normalize(WebUtility.HtmlDecode(body));

        return metaParts.Count > 0
            ? string.Join(" · ", metaParts.Distinct()) + "\n\n" + body
            : body;
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
