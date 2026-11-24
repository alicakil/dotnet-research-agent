using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ResearchAgent.Research;

public interface IDeepResearchAgent
{
    Task<ResearchReport> ResearchAsync(ResearchRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Evidence-first deep research. Code gathers the evidence, the model only plans and synthesizes,
/// and code checks the result:
///
///  1. Plan: the model proposes a few search queries (falls back to the topic itself).
///  2. Search: all queries run at once, one evidence branch each; results are de-duplicated by URL.
///  3. Read: the best pages are fetched in parallel; a page that cannot be read keeps its search snippet.
///  4. Synthesize: one model call sees only the numbered sources and must cite them for every claim.
///  5. Verify: a finding that cites an unknown source is dropped; a finding whose sources do not
///     contain its key terms is kept but marked unverified.
/// </summary>
public sealed class DeepResearchAgent(
    IAiCompletionService ai,
    IWebSearchProvider search,
    IWebPageFetcher fetcher,
    ILogger<DeepResearchAgent> logger) : IDeepResearchAgent
{
    public const string PlanTask = "research.plan";
    public const string SynthesizeTask = "research.synthesize";

    private const int ResultsPerQuery = 5;
    private const int ExcerptChars = 3000;
    private const int MaxParallelFetches = 4;

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public async Task<ResearchReport> ResearchAsync(ResearchRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Topic))
            throw new ArgumentException("A topic is required.", nameof(request));

        var queries = await PlanQueriesAsync(request, cancellationToken);

        // Evidence branches run at the same time; a failing search provider only empties its branch.
        var outcomes = await Task.WhenAll(queries.Select(q => search.SearchAsync(q, ResultsPerQuery, request.CountryCode, cancellationToken)));
        var candidates = outcomes
            .SelectMany(o => o.Results)
            .Where(r => Uri.TryCreate(r.Url, UriKind.Absolute, out _))
            .DistinctBy(r => NormalizeUrl(r.Url))
            .Take(request.MaxSources)
            .ToList();

        if (candidates.Count == 0)
            return Empty(request, queries, ResearchStatus.NoEvidence);

        var sources = await ReadSourcesAsync(candidates, cancellationToken);

        Draft? draft;
        try
        {
            draft = await SynthesizeAsync(request, sources, cancellationToken);
        }
        catch (Exception ex) when (ex is AiCompletionFailedException or JsonException)
        {
            logger.LogWarning(ex, "Research synthesis failed for {Topic}.", request.Topic);
            return Empty(request, queries, ResearchStatus.Failed, sources, ex.Message);
        }

        if (draft is null)
            return Empty(request, queries, ResearchStatus.Failed, sources, "The model returned no report.");

        var (findings, dropped) = Verify(draft.Findings ?? [], sources);
        var status = findings.Count > 0 ? ResearchStatus.Done : ResearchStatus.NoEvidence;

        return new ResearchReport(request.Topic, status, draft.Summary?.Trim() ?? string.Empty, findings,
            sources, queries, dropped);
    }

    private async Task<IReadOnlyList<string>> PlanQueriesAsync(ResearchRequest request, CancellationToken ct)
    {
        var fallback = new[] { request.Focus is null ? request.Topic : $"{request.Topic} {request.Focus}" };
        try
        {
            var result = await ai.CompleteAsync(new AiCompletionRequest
            {
                TaskType = PlanTask,
                SystemPrompt = ResearchPrompts.PlannerSystem,
                UserPrompt = ResearchPrompts.PlannerUser(request),
                ResponseJsonSchema = ResearchPrompts.PlanSchema,
            }, ct);

            var plan = JsonSerializer.Deserialize<Plan>(ExtractJson(result.Text), Json);
            var queries = (plan?.Queries ?? [])
                .Where(q => !string.IsNullOrWhiteSpace(q))
                .Select(q => q.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(Math.Max(1, request.MaxQueries))
                .ToList();
            return queries.Count > 0 ? queries : fallback;
        }
        catch (Exception ex) when (ex is AiCompletionFailedException or JsonException)
        {
            // Planning is an accelerator, not a dependency: the topic alone is still a good query.
            logger.LogWarning(ex, "Research planning failed; searching for the topic itself.");
            return fallback;
        }
    }

    private async Task<IReadOnlyList<ResearchSource>> ReadSourcesAsync(IReadOnlyList<WebSearchResult> candidates, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(MaxParallelFetches);
        var pages = await Task.WhenAll(candidates.Select(async (c, i) =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var page = await fetcher.FetchAsync(c.Url, ct);
                var text = page.Success ? page.Text : null;
                var fromSnippet = string.IsNullOrWhiteSpace(text);
                var excerpt = Trim(fromSnippet ? c.Snippet : text!, ExcerptChars);
                var title = !string.IsNullOrWhiteSpace(page.Title) ? page.Title! : c.Title;
                return new ResearchSource(i + 1, title, c.Url, excerpt, fromSnippet);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Could not read {Url}; keeping the search snippet.", c.Url);
                return new ResearchSource(i + 1, c.Title, c.Url, Trim(c.Snippet, ExcerptChars), true);
            }
            finally
            {
                gate.Release();
            }
        }));

        return pages.Where(p => !string.IsNullOrWhiteSpace(p.Excerpt)).ToList();
    }

    private async Task<Draft?> SynthesizeAsync(ResearchRequest request, IReadOnlyList<ResearchSource> sources, CancellationToken ct)
    {
        var result = await ai.CompleteAsync(new AiCompletionRequest
        {
            TaskType = SynthesizeTask,
            SystemPrompt = ResearchPrompts.SynthesizerSystem,
            UserPrompt = ResearchPrompts.SynthesizerUser(request, sources),
            ResponseJsonSchema = ResearchPrompts.ReportSchema,
        }, ct);

        return JsonSerializer.Deserialize<Draft>(ExtractJson(result.Text), Json);
    }

    /// <summary>
    /// Code-side checks on the model's draft. Citations must point at sources the model was given;
    /// a finding without one valid citation is dropped. A finding is verified when at least one cited
    /// source contains at least half of the claim's key terms (or two of them, for long claims).
    /// </summary>
    internal static (List<ResearchFinding> Findings, int Dropped) Verify(IEnumerable<DraftFinding> draft, IReadOnlyList<ResearchSource> sources)
    {
        var byId = sources.ToDictionary(s => s.Id);
        var findings = new List<ResearchFinding>();
        var dropped = 0;

        foreach (var f in draft)
        {
            var claim = f.Claim?.Trim();
            var ids = (f.Sources ?? []).Where(byId.ContainsKey).Distinct().ToList();
            if (string.IsNullOrWhiteSpace(claim) || ids.Count == 0)
            {
                dropped++;
                continue;
            }

            var terms = KeyTerms(claim);
            var verified = terms.Count == 0 || ids.Any(id =>
            {
                var text = byId[id].Excerpt;
                var hits = terms.Count(t => text.Contains(t, StringComparison.OrdinalIgnoreCase));
                return hits >= Math.Min(2, terms.Count) || hits * 2 >= terms.Count;
            });

            findings.Add(new ResearchFinding(claim, ids, verified));
        }

        return (findings, dropped);
    }

    /// <summary>Words that carry the claim: numbers and longer words, without common filler.</summary>
    internal static List<string> KeyTerms(string claim) =>
        claim.Split([' ', ',', '.', ';', ':', '(', ')', '"', '\'', '/', '!', '?'], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim())
            .Where(w => (w.Length >= 5 || w.Any(char.IsDigit)) && !Filler.Contains(w))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static readonly HashSet<string> Filler = new(StringComparer.OrdinalIgnoreCase)
    {
        "about", "according", "which", "their", "there", "these", "those", "would", "could", "should",
        "being", "where", "while", "other", "since", "until", "company", "offers", "provides", "based",
    };

    private ResearchReport Empty(ResearchRequest request, IReadOnlyList<string> queries, ResearchStatus status,
        IReadOnlyList<ResearchSource>? sources = null, string? error = null) =>
        new(request.Topic, status, string.Empty, [], sources ?? [], queries, 0, error);

    private static string NormalizeUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;
        return (uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host).ToLowerInvariant()
               + uri.AbsolutePath.TrimEnd('/').ToLowerInvariant();
    }

    private static string Trim(string text, int max)
    {
        var t = text.Trim();
        return t.Length <= max ? t : t[..max];
    }

    /// <summary>Models sometimes wrap JSON in a code fence; take the outermost object.</summary>
    private static string ExtractJson(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : text;
    }

    private sealed class Plan
    {
        public List<string>? Queries { get; set; }
    }

    internal sealed class Draft
    {
        public string? Summary { get; set; }
        public List<DraftFinding>? Findings { get; set; }
    }

    internal sealed class DraftFinding
    {
        public string? Claim { get; set; }

        [JsonPropertyName("sources")]
        public List<int>? Sources { get; set; }
    }
}
