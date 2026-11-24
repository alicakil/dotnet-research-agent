using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace ResearchAgent.Web;

/// <summary>
/// Search through the built-in <c>web_search</c> tool of the OpenAI Responses API. Instead of buying
/// raw SERP data like Serper, it uses the model's own search tool.
///
/// Why a second provider: the OpenAI key is ALREADY configured for the LLM layer → it works without a
/// new secret and without an infrastructure deploy. Evidence gathering stays alive while Serper is off.
/// The capability is also not tied to any vertical: other features can use this interface later.
///
/// It reads the sources from the <c>url_citation</c> annotations of the response, NOT from the model's
/// narrative — machine-generated, so the sources cannot be hallucinated; the cited text span is used as
/// the snippet. That keeps the (title + snippet + url) triple that identity matching needs.
///
/// ⚠️ TRACEABILITY: this call BYPASSES the adapters (the Responses API is a different surface), so NO
/// record lands in the AI trace (<c>AiTraceRecord</c>). The cost shows up in the OpenAI Costs API (admin
/// dashboard); the token usage is also written to the log here, so it can be followed in your log tooling
/// (e.g. CloudWatch).
/// </summary>
public sealed class OpenAiWebSearchProvider : IEscalationSearchProvider
{
    private const string Endpoint = "https://api.openai.com/v1/responses";
    private const int MaxSnippetChars = 400;
    private const int MaxNarrativeChars = 4_000;

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
        // Search + page reading + reasoning all happen on the model's side, so a single call takes a LONG time.
        // Measured on 31 Jul 2026 (real runs): 45 s · 52 s · 62 s · 68 s · 95 s.
        // The old 40 s value was BELOW that whole range — the escalation call routinely died with a
        // TaskCanceledException, no evidence was gathered, and the cause just looked like a plain "timeout".
        // 180 s also leaves room for the upper tail; the 600 s Lambda timeout we run under covers it comfortably.
        _httpClient.Timeout = TimeSpan.FromSeconds(180);
    }

    /// <summary>
    /// Needs the key AND the <see cref="AiOptions.EnableOpenAiWebSearch"/> kill switch to be on.
    /// While it is off, escalation is never attempted and the research settles for what the fast tier found.
    /// </summary>
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
                // The model must not interpret, just search and show the sources: the identity decision
                // is made by the later synthesis step; here we want the raw signal.
                input =
                    $"Search the public web for: {query}\n\n" +
                    "List the most relevant public sources you find (official websites, directories, " +
                    "registries, press). For each source write one short line stating what it shows about " +
                    "the subject. Do not speculate, do not merge different subjects that share a name, and " +
                    "do not conclude anything: just report what each source says. If you find nothing " +
                    "relevant, say so plainly.",
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request, ct);
            var payload = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("OpenAI web_search failed: HTTP {Status} {Body}",
                    (int)response.StatusCode, AiAdapterHelpers.Truncate(payload));
                return WebSearchOutcome.Fail($"HTTP {(int)response.StatusCode}");
            }

            var results = ParseCitations(payload, maxResults);
            LogUsage(payload, query, results.Count);

            // The model's PROSE is carried along too, not just the links. We measured it: NEGATIVE and
            // DISTINGUISHING findings such as "no source shows this subject as X" or "this source uses a
            // different full name" exist only there. Had we kept only the citations, we would have thrown
            // away the best part of a 45–95 s call.
            return WebSearchOutcome.Ok(results, ExtractNarrative(payload));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OpenAI web_search error: {Query}", query);
            return WebSearchOutcome.Fail(ex.GetType().Name);
        }
    }

    /// <summary>
    /// Walks the response and collects the <c>url_citation</c> entries inside <c>output[].content[].annotations[]</c>.
    /// Snippet = the text span the citation points at (start_index..end_index); if the range is invalid, the start of the text.
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

    /// <summary>
    /// Joins the model's free text (all <c>output[].content[].text</c> parts).
    /// It is handed to the synthesis step as "the search engine's own report" — this is where the identity disambiguation lives.
    /// </summary>
    private static string? ExtractNarrative(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (!doc.RootElement.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
                return null;

            var parts = new List<string>();
            foreach (var item in output.EnumerateArray())
            {
                if (!item.TryGetProperty("content", out var contents) || contents.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var content in contents.EnumerateArray())
                {
                    if (content.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                    {
                        var s = t.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) parts.Add(s);
                    }
                }
            }

            if (parts.Count == 0) return null;
            var joined = string.Join("\n", parts).Trim();
            return joined.Length > MaxNarrativeChars ? joined[..MaxNarrativeChars] : joined;
        }
        catch { return null; }   // the text is a bonus; failing to read it does not invalidate the citations
    }

    private static string ExtractSnippet(string text, JsonElement annotation)
    {
        if (text.Length == 0) return "";

        var start = annotation.TryGetProperty("start_index", out var s) && s.ValueKind == JsonValueKind.Number
            ? s.GetInt32() : 0;
        var end = annotation.TryGetProperty("end_index", out var e) && e.ValueKind == JsonValueKind.Number
            ? e.GetInt32() : 0;

        // Invalid/reversed range → take a piece from the start of the text (so the source is still usable).
        if (start < 0 || end <= start || start >= text.Length)
            return text[..Math.Min(MaxSnippetChars, text.Length)].Trim();

        end = Math.Min(end, text.Length);
        var span = text[start..end];
        return span.Length > MaxSnippetChars ? span[..MaxSnippetChars].Trim() : span.Trim();
    }

    /// <summary>Writes token usage to the log — this call does not land in the AI trace, so this is the only visibility.</summary>
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
                    "OpenAI web_search: model={Model} in={In} out={Out} results={Count} query={Query}",
                    _options.WebSearchModel, input, output, resultCount, AiAdapterHelpers.Truncate(query));
                return;
            }
        }
        catch { /* not a problem if usage could not be read — it does not affect the result */ }

        _logger.LogInformation("OpenAI web_search: results={Count} query={Query}", resultCount, AiAdapterHelpers.Truncate(query));
    }
}
