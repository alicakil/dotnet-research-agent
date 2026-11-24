using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace ResearchAgent.Web;

/// <summary>
/// The fetch arm that RENDERS JavaScript. A second tier for pages that cannot be read with a direct
/// HTTP fetch (<see cref="WebPageFetcher"/>).
///
/// Typical targets render on the client side: a plain HTTP fetch sees only an empty shell.
///
/// ⚠️ This arm is EXPENSIVE and QUOTA-LIMITED, so it NEVER runs first — see
/// <see cref="CompositeWebPageFetcher"/>. Spending the quota on pages that a plain fetch already handles
/// would leave no quota for the pages that really need it.
///
/// Without a key, <see cref="IsConfigured"/> returns false and the arm silently drops out of the chain;
/// research simply carries on as it did before, with plain fetching only.
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

        // The SSRF and login-wall rules apply HERE TOO. Rendering a blocked host would breach
        // its terms of service, and a login wall would not give us anything useful anyway.
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

            // Title: the name of a subject that was added by link alone, without a typed name, is taken from here.
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
            _logger.LogWarning(ex, "Firecrawl fetch failed: {Url}", url);
            return new FetchedPage(url, false, null, "firecrawl_failed");
        }
    }

    private static string Truncate(string text) =>
        text.Length <= MaxChars ? text : text[..MaxChars];
}

/// <summary>
/// Two-tier page fetching: FIRST a cheap plain HTTP fetch, then the JS-rendering arm if needed.
/// The same pattern is already used on the search side (<see cref="CompositeWebSearchProvider"/>) —
/// cheapest to most expensive, stop at the first success.
///
/// Escalation is CONDITIONAL, because the rendering arm is quota-limited. Three triggers:
///  • the plain fetch failed,
///  • the body is suspiciously short (under 400 characters),
///  • the body is IDENTICAL to a body seen earlier ON THE SAME HOST → an SPA shell.
///
/// Measured in production: on one site, a plain fetch returned **exactly the same 2638 characters** for
/// every route — it is a single-page app, so every route serves the same HTML skeleton and the content is
/// rendered on the client. Because the shell is 2638 characters long, the "short body" rule did NOT catch
/// it, and the arm would silently never have kicked in. On the same page Firecrawl returned the real
/// team listing, at 7712 characters.
///
/// Repeat detection is kept per request scope: it remembers across one research run and is not carried
/// between runs.
///
/// Permanent blockers do NOT escalate: a blocked host, a login wall and an invalid address give the same
/// result even when rendered — spending the quota on them is pointless.
/// </summary>
public sealed class CompositeWebPageFetcher(
    WebPageFetcher primary,
    FirecrawlPageFetcher rendered,
    ILogger<CompositeWebPageFetcher> logger) : IWebPageFetcher
{
    private const int ThinBodyThreshold = 400;

    /// <summary>
    /// Failures that rendering CANNOT change. 404/410 must be on this list, and what happens without them
    /// was observed in production (24 Aug): path guessing tries <c>/equipo</c> (Spanish for "team"), the site
    /// returns 404, and the plain fetch correctly rejects it — but the composite saw a "failure" and passed
    /// it on to Firecrawl, which happily rendered the "Página no encontrada" (page not found) page and
    /// returned SUCCESS. A page that does not exist thus ended up in the evidence pile.
    ///
    /// 403 is deliberately NOT on the list: it is often a bot block, and it is the only HTTP failure
    /// the rendering arm can get around.
    /// </summary>
    private static readonly string[] PermanentSkips =
    [
        "blocked_host", "blocked_host_after_redirect", "login_wall", "consent_wall", "invalid_url", "empty_url",
        "http_404", "http_410", "http_400", "http_401",
        // If the domain does not exist (NXDOMAIN) or the target is on a private network, there is no page to render either.
        "host_not_found", "private_address_blocked"
    ];

    /// <summary>host → fingerprints of the bodies a plain fetch returned on that host.</summary>
    private readonly Dictionary<string, HashSet<string>> _seenBodies = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Hosts proven UNREACHABLE in this run. Every path tried on a dead host burns money and time twice
    /// (the plain-fetch timeout plus the rendering arm), and that fills the research run's 180 s budget.
    ///
    /// Measured (27 Aug 2026): four addresses on one dead site were tried and each burned ~38 s (8 s timeout
    /// + ~30 s Firecrawl). After 152 seconds the budget ran out and the NEXT STEP died with it — along with
    /// the WORKING evidence we already had.
    ///
    /// Once a host is marked dead, its remaining addresses in that run are dropped immediately. It is not
    /// carried between runs (scoped): a temporary outage must not blind us to a site permanently.
    /// </summary>
    private readonly HashSet<string> _deadHosts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Lock for <see cref="_seenBodies"/> and <see cref="_deadHosts"/>: the pages of one run are also read
    /// concurrently (parallel evidence branches).
    /// </summary>
    private readonly object _gate = new();

    /// <summary>Transport-layer failures — not a missing page: the host is UNREACHABLE.</summary>
    private static readonly string[] UnreachableReasons = ["timeout", "fetch_error", "dns_error"];

    public async Task<FetchedPage> FetchAsync(string url, CancellationToken ct = default)
    {
        var host = HostOf(url);
        if (IsDead(host))
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
            "Plain fetch insufficient ({Reason}) — switching to the JS-rendering arm: {Url}",
            !page.Success ? page.SkipReason
                : shell ? $"identical body already seen on the same host ({length} characters) → SPA shell"
                : $"body is {length} characters",
            url);

        var viaRender = await rendered.FetchAsync(url, ct);

        // If neither arm could reach it, the host counts as dead: trying the site's other addresses
        // does nothing but burn the budget.
        if (!viaRender.Success && IsUnreachable(page)) MarkDead(host, url);

        // If rendering fails too, the PLAIN FETCH's result is kept: a weak body beats no body,
        // and the failure reason is the first fetch's reason — replacing it with the second arm's
        // message would muddy the diagnosis.
        return viaRender.Success ? viaRender : page;
    }

    /// <summary>
    /// Raw HTML only comes from the plain fetch (the rendering arm returns no HTML): for rules that look at
    /// structured data. A host already proven unreachable in this run is still skipped; only <see cref="FetchAsync"/>
    /// marks a host dead (after it has tried the rendering arm too).
    /// </summary>
    public Task<FetchedPage> FetchHtmlAsync(string url, CancellationToken ct = default) =>
        IsDead(HostOf(url))
            ? Task.FromResult(new FetchedPage(url, false, null, "host_unreachable"))
            : primary.FetchHtmlAsync(url, ct);

    private static bool IsUnreachable(FetchedPage page) =>
        !page.Success && page.SkipReason is { } r && UnreachableReasons.Contains(r);

    private bool IsDead(string host)
    {
        lock (_gate) return _deadHosts.Contains(host);
    }

    private void MarkDead(string host, string url)
    {
        lock (_gate)
        {
            if (host.Length == 0 || !_deadHosts.Add(host)) return;
        }
        logger.LogInformation("Host unreachable, its remaining addresses will be skipped in this run: {Host} ({Url})", host, url);
    }

    private static string HostOf(string url) =>
        Uri.TryCreate(WebPageFetcher.NormalizeUrl(url), UriKind.Absolute, out var uri) ? uri.Host : string.Empty;

    /// <summary>
    /// Is this body IDENTICAL to one already seen on the same host? If so, there is no page-specific
    /// content — it is an SPA skeleton. The first sighting returns false (there is nothing to compare
    /// with) and the body is recorded; the real catch happens on the second page, which for us is the team page.
    /// </summary>
    private bool IsRepeatedBody(string url, string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;

        // Length + head/tail pieces: keeping the full text in the dictionary would mean carrying tens of
        // thousands of characters per page in memory for the whole request.
        var head = text.Length <= 200 ? text : text[..200];
        var tail = text.Length <= 200 ? string.Empty : text[^200..];
        lock (_gate)
        {
            if (!_seenBodies.TryGetValue(host, out var bodies))
                _seenBodies[host] = bodies = new HashSet<string>(StringComparer.Ordinal);
            return !bodies.Add($"{text.Length}|{head}|{tail}");
        }
    }
}
