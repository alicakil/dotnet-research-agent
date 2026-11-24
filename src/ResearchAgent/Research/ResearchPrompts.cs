using System.Text;

namespace ResearchAgent.Research;

/// <summary>Prompts and output schemas of the research agent. Kept in one place so they can be evaluated and versioned together.</summary>
internal static class ResearchPrompts
{
    public const string PlannerSystem =
        "You plan web research. Given a subject, write short, distinct search queries that together cover " +
        "what a careful analyst would check: what the subject is, official sources, recent news, and the " +
        "specific focus if one is given. Use the subject's own language and names. Do not research private " +
        "individuals.";

    public static string PlannerUser(ResearchRequest r) =>
        $"Subject: {r.Topic}\n" +
        (string.IsNullOrWhiteSpace(r.Focus) ? "" : $"Focus: {r.Focus}\n") +
        $"Write at most {Math.Max(1, r.MaxQueries)} queries.";

    public const string PlanSchema = """
        {"type":"object","properties":{"queries":{"type":"array","items":{"type":"string"}}},"required":["queries"]}
        """;

    public const string SynthesizerSystem =
        "You write a short research report using ONLY the numbered sources below. Every finding must cite " +
        "the numbers of the sources that state it. Do not use outside knowledge, do not guess, and do not " +
        "merge different subjects that share a name. If the sources disagree, say so in a finding. If the " +
        "sources say nothing useful, return an empty list of findings. Write in English.";

    public static string SynthesizerUser(ResearchRequest r, IReadOnlyList<ResearchSource> sources)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Subject: {r.Topic}");
        if (!string.IsNullOrWhiteSpace(r.Focus)) sb.AppendLine($"Focus: {r.Focus}");
        sb.AppendLine().AppendLine("Sources:");
        foreach (var s in sources)
        {
            sb.AppendLine($"[{s.Id}] {s.Title} ({s.Url})");
            sb.AppendLine(s.Excerpt);
            sb.AppendLine();
        }

        sb.AppendLine("Return a two or three sentence summary and up to 10 findings, each with its source numbers.");
        return sb.ToString();
    }

    public const string ReportSchema = """
        {"type":"object","properties":{
          "summary":{"type":"string"},
          "findings":{"type":"array","items":{"type":"object","properties":{
            "claim":{"type":"string"},
            "sources":{"type":"array","items":{"type":"integer"}}},
            "required":["claim","sources"]}}},
         "required":["summary","findings"]}
        """;
}
