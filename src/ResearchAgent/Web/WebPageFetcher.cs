using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ResearchAgent.Web;

/// <summary>
/// The result of fetching one page. If <see cref="Text"/> is filled, there is usable content.
///
/// <see cref="Structured"/> is a profile summary extracted from the page's JSON-LD (if there is one). It is
/// kept SEPARATE from the plain text because its quality is completely different: rating, review count and
/// review texts are already labeled there, so they need not be dug out of 29,000 characters of menu/footer
/// noise. It is filled on pages that publish profile data (typically directory profiles) and empty on
/// ordinary sites, which is normal.
///
/// <see cref="Html"/> is the page's raw HTML (capped); only <see cref="WebPageFetcher.FetchHtmlAsync"/> fills it
/// (for example, the photo URLs on a directory profile are in the markup, not in the text).
///
/// <see cref="Title"/> is the page title (og:title, else &lt;title&gt;): the name of a subject that was added
/// by link alone, without a typed name, is taken from here.
///
/// <see cref="Url"/> is the address the fetch ended up at. On a consent wall (<c>consent_wall</c>) it is the
/// address in front of the wall: there, a Google Maps short link has already expanded to the full link that
/// carries the name of the place.
/// </summary>
public sealed record FetchedPage(
    string Url, bool Success, string? Text, string? SkipReason, string? Structured = null, string? Html = null,
    string? Title = null);

/// <summary>The page-fetching seam — the deep research service plugs into it, tests supply a stub (no network).</summary>
public interface IWebPageFetcher
{
    Task<FetchedPage> FetchAsync(string url, CancellationToken ct = default);

    /// <summary>
    /// Same as <see cref="FetchAsync"/>, but the result also carries the raw HTML (<see cref="FetchedPage.Html"/>): for rules
    /// that look at a page's structured data (for example, the city on a directory profile). A fetcher that cannot supply
    /// HTML (the rendering arm, test fakes) returns the plain fetch; a rule that looks at the HTML then extracts nothing.
    /// </summary>
    Task<FetchedPage> FetchHtmlAsync(string url, CancellationToken ct = default) => FetchAsync(url, ct);
}

/// <summary>
/// Safely fetches the URLs it is GIVEN on the server side and reduces them to plain text (the
/// evidence-gathering layer of deep research). Design constraints:
///
///  • SSRF protection at the core: http/https only; after DNS resolution, private/loopback/link-local/CGNAT
///    addresses are rejected; redirects are followed BY HAND and every hop is re-validated.
///  • Hosts behind a login wall (LinkedIn, Facebook, X) are not fetched at all: trying them is
///    wasted delay and risks violating their terms of service.
///  • Budget: a per-URL timeout, a download size cap and a text character cap.
///
/// Error TOLERANT: every failure comes back inside <see cref="FetchedPage"/> with its reason and never
/// throws — the rest of the research must stay up even if page fetching fails.
/// </summary>
public sealed class WebPageFetcher(HttpClient httpClient, ILogger<WebPageFetcher> logger) : IWebPageFetcher
{
    // 5, because a chain sometimes starts with a round trip to the same address: the server first sets a
    // language cookie and redirects back (ASP.NET culture cookie), and only then goes to the real domain
    // (example.com → 302 / → 302 www.example.co.uk). With 3 hops, chains like this ran out just before
    // the end. Cookies are kept (UseCookies on the handler) — without them the round trip never ends.
    private const int MaxRedirects = 5;
    private const int MaxDownloadBytes = 1_500_000;
    private const int MaxTextChars = 8_000;
    private static readonly TimeSpan PerUrlTimeout = TimeSpan.FromSeconds(8);

    /// <summary>Hosts we will not try to fetch (login wall / active bot blocking).</summary>
    private static readonly string[] BlockedHosts =
    {
        "linkedin.com", "lnkd.in",       // HTTP 999 — auth wall, datacenter IPs are blocked by ASN
        "facebook.com", "fb.com",        // login wall
        "x.com", "twitter.com",          // login wall
    };

    /// <summary>Hosts with a low content yield that are still worth trying (og tags / a bio may come out).</summary>
    private static readonly string[] LowYieldHosts = { "instagram.com", "youtube.com", "tiktok.com" };

    public static bool IsBlockedHost(string url) =>
        TryGetHost(url, out var host) && BlockedHosts.Any(b => host == b || host.EndsWith("." + b));

    /// <summary>
    /// Paths that lead to a login wall. They return 200 and their bodies are longer than 80 characters,
    /// so unless they are checked they look like a SUCCESSFUL fetch — in one real run a link landed on
    /// <c>/accounts/login/?next=…</c> and that page was counted as evidence. Luckily the model said "the
    /// content could not be seen", but we must not have to rely on that: presenting an unreadable source
    /// as if it had been read undermines evidence discipline at its root.
    /// </summary>
    private static readonly string[] LoginPaths =
        ["/accounts/login", "/login", "/signin", "/sign-in", "/auth/login", "/users/sign_in"];

    private static bool IsLoginWall(Uri uri) =>
        LoginPaths.Any(p => uri.AbsolutePath.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Cookie consent wall. The server runs in the EU: every Google Maps link goes to <c>consent.google.com</c> first, and
    /// that page returns 200 + a long body, so it looked like a successful fetch. On 28 Oct 2026 a subject that was added
    /// with only a Maps link was named after this page's title ("Bevor Sie zu Google Maps weitergehen"), and the research
    /// ran under that name. Consent is never given; the page counts as unreadable.
    /// </summary>
    private static bool IsConsentWall(Uri uri) =>
        uri.Host.StartsWith("consent.google.", StringComparison.OrdinalIgnoreCase)
        || uri.Host.Equals("consent.youtube.com", StringComparison.OrdinalIgnoreCase);

    public static bool IsLowYieldHost(string url) =>
        TryGetHost(url, out var host) && LowYieldHosts.Any(b => host == b || host.EndsWith("." + b));

    /// <summary>
    /// Turns a user-typed address into a fetchable URL. People do not type the scheme into a form field —
    /// they write "mysite.com", not "https://mysite.com". In production this made a subject's own website
    /// (the most valuable evidence source, with a fixed identity) get silently skipped as "invalid_url".
    /// A scheme that is present is left alone; otherwise https is assumed (if http is needed, the redirect is followed).
    ///
    /// The DOT REQUIREMENT is deliberate: adding the scheme to every input would turn garbage such as
    /// "not-a-url" into a valid absolute URI. It would then fail at DNS resolution with a misleading reason
    /// code instead of "invalid_url" (originally <c>private_address_blocked</c>, the SAME code as a real SSRF
    /// attempt, which made that code lose its meaning). A host name without a dot is never a public site anyway.
    /// </summary>
    public static string NormalizeUrl(string url)
    {
        var trimmed = (url ?? string.Empty).Trim();
        if (trimmed.Length == 0) return trimmed;
        if (trimmed.Contains("://", StringComparison.Ordinal)) return trimmed;

        var hostPart = trimmed.Split('/', '?', '#')[0];
        return hostPart.Contains('.', StringComparison.Ordinal) ? "https://" + trimmed : trimmed;
    }

    public Task<FetchedPage> FetchAsync(string url, CancellationToken ct = default) => FetchCoreAsync(url, keepHtml: false, ct);

    /// <summary>
    /// The same rules as <see cref="FetchAsync"/> (SSRF, redirects, caps, login wall), but the result also carries the raw
    /// HTML (<see cref="FetchedPage.Html"/>): for example, to get the photo URLs on a directory profile.
    /// </summary>
    public Task<FetchedPage> FetchHtmlAsync(string url, CancellationToken ct = default) => FetchCoreAsync(url, keepHtml: true, ct);

    private async Task<FetchedPage> FetchCoreAsync(string url, bool keepHtml, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url))
            return new FetchedPage(url, false, null, "empty_url");

        url = NormalizeUrl(url);

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return new FetchedPage(url, false, null, "invalid_url");

        // A login page can be given up front or turn up through a redirect; either way there is
        // no content, only a sign-in form.
        if (IsLoginWall(uri))
            return new FetchedPage(url, false, null, "login_wall");
        if (IsConsentWall(uri))
            return new FetchedPage(url, false, null, "consent_wall");

        if (IsBlockedHost(url))
            return new FetchedPage(url, false, null, "blocked_host");

        var page = await AttemptAsync(url, uri, keepHtml, ct);

        // A SITE WITHOUT HTTPS. Common among small business sites: port 443 is silently swallowed (not even
        // an RST comes back, the connection just hangs), port 80 works and redirects to the real address.
        // A browser never runs into this, because when https fails it falls back to http itself — we did not.
        //
        // Measured (27 Aug 2026): the stored address was https://example.com. Port 443 times out, while
        // http://example.com answers with a 302 to www.example.com.tr, where the site actually lives.
        //
        // Only tried on a TRANSPORT-layer failure: if a RESPONSE such as 404/403 came back, the site does
        // speak https, and dropping the scheme would be wrong.
        if (!page.Success
            && page.SkipReason is "timeout" or "fetch_error"
            && uri.Scheme == Uri.UriSchemeHttps
            && uri.IsDefaultPort)
        {
            var overHttp = new UriBuilder(uri) { Scheme = Uri.UriSchemeHttp, Port = -1 }.Uri;
            var viaHttp = await AttemptAsync(url, overHttp, keepHtml, ct);
            if (viaHttp.Success)
            {
                logger.LogInformation("HTTPS did not respond, fell back to http: {Url}", url);
                return viaHttp;
            }
        }

        return page;
    }

    /// <summary>
    /// A fetch attempt with a single scheme. It runs on its own timeout clock: the http fallback must get
    /// the full budget, not whatever is left after https has used up its time.
    /// </summary>
    private async Task<FetchedPage> AttemptAsync(string url, Uri start, bool keepHtml, CancellationToken ct)
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
                // Some sites return 403 to requests without a User-Agent; we state our identity honestly.
                request.Headers.TryAddWithoutValidation("User-Agent",
                    "Mozilla/5.0 (compatible; ResearchAgent/1.0; +https://github.com/alicakil/dotnet-research-agent)");
                request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml");

                using var response = await httpClient.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

                if ((int)response.StatusCode is >= 300 and < 400
                    && response.Headers.Location is { } location)
                {
                    var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                    if (IsConsentWall(next))
                        return new FetchedPage(current.AbsoluteUri, false, null, "consent_wall");
                    current = next;
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
                var title = ExtractTitle(html);

                // If structured data EXISTS the page is useful even when the plain text stays short: the
                // valuable part of directory profiles sits in the JSON-LD anyway.
                return text.Length < 80 && structured is null
                    ? new FetchedPage(current.ToString(), false, null, "too_little_text")
                    : new FetchedPage(current.ToString(), true, text, null, structured, keepHtml ? html : null, title);
            }

            return new FetchedPage(url, false, null, "too_many_redirects");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new FetchedPage(url, false, null, "timeout");
        }
        catch (Exception ex)
        {
            logger.LogInformation("WebPageFetcher {Url} failed: {Message}", url, ex.Message);
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
    /// SSRF protection: ALL addresses the host resolves to must be public. The fetcher runs inside a VPC
    /// (AWS Lambda in our deployment), so targets such as 10.x, 169.254.x (IMDS), 127.x and ::1 must be
    /// strictly off limits.
    ///
    /// The returned reason code is deliberately DISTINCT. Both outcomes end in "we did not fetch it", but
    /// their meanings are opposite: <c>private_address_blocked</c> is a security decision, while
    /// <c>host_not_found</c> says the address does not exist at all. Under a single code the operator got
    /// wrong information — a domain that does not exist was reported as blocked, which looks like a fixable
    /// fault. We investigate based on the reason code, so it has to be right.
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
            return "host_not_found";     // NXDOMAIN — no such domain
        }
        catch
        {
            return "dns_error";          // resolution failed (timeout, transient failure)
        }

        if (addresses.Length == 0) return "host_not_found";
        return addresses.All(IsPublic) ? null : "private_address_blocked";
    }

    /// <summary>
    /// Is the address publicly routable: not a private network, loopback, link-local, CGNAT or reserved range.
    /// Public so that other outbound fetchers can share the same rule — it should live in one place.
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

    // ── HTML → plain text ──────────────────────────────────────────────────────

    private static readonly Regex ScriptStyleRegex =
        new(@"<(script|style|noscript|svg)[^>]*>.*?</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
    private static readonly Regex TagRegex = new(@"<[^>]+>");
    private static readonly Regex WhitespaceRegex = new(@"[ \t   ]{2,}");
    private static readonly Regex MetaTagRegex = new(@"<meta\b[^>]*>", RegexOptions.IgnoreCase);

    /// <summary>
    /// Whitespace-only lines collapse into a single line break. Measured (16 Oct 2026): the plain text of one directory
    /// profile was 32,000 characters and a third of it was blank lines; the 8,000-character cap filled up before reaching
    /// the education and experience sections (at 8,700-10,600). After collapsing, the whole background fits under the same
    /// cap (6,200-7,500) and only the reviews (after 9,000) stay out. The cap is unchanged: the text sent to the model did
    /// not grow, the blank space just went away.
    /// </summary>
    private static readonly Regex BlankLinesRegex = new(@"[ \t]*\n\s*");
    /// <summary>Attribute value — the closing quote is matched to the OPENING quote; otherwise
    /// an apostrophe inside the content ("Joe's Garage …") would cut the value short.</summary>
    private static readonly Regex AttrRegex =
        new(@"\b(name|property|content)\s*=\s*(?:(""|')(?<v>.*?)\2|(?<v>[^\s""'>]+))",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly string[] WantedMeta = { "description", "og:description", "og:title" };

    /// <summary>Extracts the visible text; also prepends the meta description / og:description.</summary>
    public static string HtmlToText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;

        // Meta tags are processed one by one: the ORDER of attributes varies (content can come first),
        // and a single mega-regex does not catch that reliably.
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

    private static readonly Regex TitleTagRegex =
        new(@"<title[^>]*>(.*?)</title>", RegexOptions.Singleline | RegexOptions.IgnoreCase);

    /// <summary>The page title: og:title (written for sharing, so cleaner), else &lt;title&gt;. Null if there is neither.</summary>
    public static string? ExtractTitle(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;

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
            if (key == "og:title" && !string.IsNullOrWhiteSpace(content))
                return Normalize(WebUtility.HtmlDecode(content)).Replace('\n', ' ');
        }

        var match = TitleTagRegex.Match(html);
        if (!match.Success) return null;
        var title = Normalize(WebUtility.HtmlDecode(match.Groups[1].Value)).Replace('\n', ' ');
        return title.Length > 0 ? title : null;
    }

    // ── JSON-LD → profile summary ────────────────────────────────────────

    private static readonly Regex JsonLdRegex =
        new(@"<script[^>]+type\s*=\s*[""']application/ld\+json[""'][^>]*>(.*?)</script>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

    /// <summary>schema.org types that stand for a person or a business — a profile on a directory is one of these.</summary>
    private static readonly HashSet<string> ProfileTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Physician", "Person", "MedicalBusiness", "MedicalClinic", "LocalBusiness",
        "Dentist", "HealthAndBeautyBusiness", "ProfessionalService",
    };

    private const int MaxStructuredChars = 1_800;
    private const int MaxReviews = 3;

    /// <summary>
    /// Extracts profile fields from the page's JSON-LD blocks.
    ///
    /// Why an allowlist: the whole JSON-LD can run to hundreds of thousands of characters (navigation,
    /// breadcrumbs, site schema), and handing it to the model raw is both expensive and noisy. The fields
    /// kept here are the ones that really matter when assessing a subject: does it have its own premises,
    /// are booking and prices public, how many reviews has it received — i.e. the answer to "does it have
    /// a customer flow of its own".
    ///
    /// FULLY TOLERANT: broken or truncated JSON is skipped silently (the download cap may have cut a block
    /// in half). If nothing is found it returns null and the caller carries on with the plain text.
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
            catch { /* malformed or truncated block — skip */ }
        }

        if (lines.Count == 0) return null;
        var joined = string.Join('\n', lines.Distinct());
        return joined.Length > MaxStructuredChars ? joined[..MaxStructuredChars] : joined;
    }

    /// <summary>Walks the JSON-LD tree: arrays, @graph and nested nodes can all occur.</summary>
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
                // Several addresses = several places of work; a meaningful signal.
                var count = addr.ValueKind == JsonValueKind.Array ? addr.GetArrayLength() : 1;
                lines.Add($"  address: {Cap(joined, 200)}{(count > 1 ? $" (+{count - 1} more)" : "")}");
            }
        }

        if (node.TryGetProperty("aggregateRating", out var rating) && rating.ValueKind == JsonValueKind.Object)
        {
            var value = Text(rating, "ratingValue");
            var count = Text(rating, "reviewCount") ?? Text(rating, "ratingCount");
            if (value is not null) lines.Add($"  rating: {value}{(count is not null ? $" ({count} reviews)" : "")}");
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

    /// <summary>Reads a field as text; in schema.org the same field can be a string, a number or {name:...}.</summary>
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

    /// <summary>Turns NBSP and related Unicode spaces into a normal space and collapses runs — so that
    /// no invisible characters reach the model (token bloat + matching problems).</summary>
    private static string Normalize(string s) =>
        WhitespaceRegex.Replace(
            s.Replace(' ', ' ').Replace(' ', ' ').Replace(' ', ' ')
             .Replace("\r\n", "\n"),
            " ").Trim();

    private static bool TryGetHost(string url, out string host)
    {
        host = string.Empty;
        // Normalize first: a scheme-less address ("linkedin.com/in/x") would fail TryCreate, TryGetHost would
        // return false and IsBlockedHost would treat it as "not blocked" — a hole in the blocklist.
        if (!Uri.TryCreate(NormalizeUrl(url ?? string.Empty), UriKind.Absolute, out var uri)) return false;
        host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.")) host = host[4..];
        return true;
    }
}
