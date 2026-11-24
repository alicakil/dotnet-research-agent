using System.Text;

namespace ResearchAgent.Research;

/// <summary>What to research. The agent is meant for organisations, products and topics, not for profiling private people.</summary>
/// <param name="Topic">The subject, for example "Acme Robotics, Madrid".</param>
/// <param name="Focus">Optional question that narrows the research ("pricing and main customers").</param>
/// <param name="CountryCode">Optional two-letter country code for local search results.</param>
/// <param name="MaxQueries">How many search queries the planner may propose.</param>
/// <param name="MaxSources">How many pages are read and handed to the model.</param>
public sealed record ResearchRequest(
    string Topic,
    string? Focus = null,
    string? CountryCode = null,
    int MaxQueries = 4,
    int MaxSources = 8);

public enum ResearchStatus
{
    /// <summary>A report with at least one finding backed by a source.</summary>
    Done,

    /// <summary>Search found nothing worth reading, or no finding survived verification.</summary>
    NoEvidence,

    /// <summary>The model could not be reached or returned something unusable.</summary>
    Failed,
}

/// <summary>A page the model was allowed to read, numbered so findings can cite it.</summary>
public sealed record ResearchSource(int Id, string Title, string Url, string Excerpt, bool FromSnippetOnly);

/// <summary>
/// One claim of the report and the sources it cites. <see cref="Verified"/> is set by code, not by
/// the model: at least one cited source must contain the claim's key terms.
/// </summary>
public sealed record ResearchFinding(string Claim, IReadOnlyList<int> SourceIds, bool Verified);

public sealed record ResearchReport(
    string Topic,
    ResearchStatus Status,
    string Summary,
    IReadOnlyList<ResearchFinding> Findings,
    IReadOnlyList<ResearchSource> Sources,
    IReadOnlyList<string> Queries,
    int DroppedFindings,
    string? Error = null)
{
    /// <summary>Renders the report as Markdown with numbered citations.</summary>
    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Research: {Topic}").AppendLine();

        if (Status != ResearchStatus.Done)
        {
            sb.AppendLine(Status == ResearchStatus.Failed
                ? $"Research failed: {Error ?? "unknown error"}"
                : "No evidence found in public sources.");
            if (Queries.Count > 0)
                sb.AppendLine().AppendLine("Searched for: " + string.Join(" · ", Queries));
            return sb.ToString();
        }

        sb.AppendLine(Summary).AppendLine();

        var verified = Findings.Where(f => f.Verified).ToList();
        var unverified = Findings.Where(f => !f.Verified).ToList();

        sb.AppendLine("## Findings").AppendLine();
        foreach (var f in verified)
            sb.AppendLine($"- {f.Claim} {Cite(f)}");

        if (unverified.Count > 0)
        {
            sb.AppendLine().AppendLine("## Unverified");
            sb.AppendLine().AppendLine("The cited sources do not clearly contain these claims; check them by hand.").AppendLine();
            foreach (var f in unverified)
                sb.AppendLine($"- {f.Claim} {Cite(f)}");
        }

        sb.AppendLine().AppendLine("## Sources").AppendLine();
        foreach (var s in Sources)
            sb.AppendLine($"{s.Id}. [{(string.IsNullOrWhiteSpace(s.Title) ? s.Url : s.Title)}]({s.Url}){(s.FromSnippetOnly ? " (search snippet only)" : "")}");

        return sb.ToString();
    }

    private static string Cite(ResearchFinding f) => string.Concat(f.SourceIds.Select(id => $"[{id}]"));
}
