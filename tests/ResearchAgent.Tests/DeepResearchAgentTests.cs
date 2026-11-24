using ResearchAgent.Research;

namespace ResearchAgent.Tests;

[Trait("Category", "Unit")]
public class DeepResearchAgentTests
{
    private const string About = "https://acme.example/about";
    private const string News = "https://news.example/acme-raises";

    private static readonly Dictionary<string, string> Pages = new()
    {
        [About] = "Acme Robotics builds warehouse robots. The company was founded in 2019 in Madrid.",
        [News] = "Acme Robotics raised 12 million euros in a Series A round led by Example Ventures.",
    };

    private static IReadOnlyList<WebSearchResult> Results(string query) =>
    [
        new("About Acme", "Acme builds robots", About),
        new("Acme raises", "Series A", News),
        new("Acme raises (www copy)", "Series A", "https://www.news.example/acme-raises/"),
    ];

    private static ScriptedAi Ai(string report) => new(r => r.TaskType == DeepResearchAgent.PlanTask
        ? """{"queries":["Acme Robotics","Acme Robotics funding","Acme Robotics"]}"""
        : report);

    [Fact]
    public async Task Builds_a_cited_report_and_checks_every_finding_in_code()
    {
        var ai = Ai("""
            {"summary":"Acme Robotics is a Madrid robotics company.",
             "findings":[
               {"claim":"Acme Robotics was founded in 2019 in Madrid.","sources":[1]},
               {"claim":"Acme Robotics raised 12 million euros in a Series A round.","sources":[2]},
               {"claim":"Acme Robotics has 400 employees in Barcelona.","sources":[1]},
               {"claim":"Acme Robotics is profitable.","sources":[9]}
             ]}
            """);
        var search = new FakeSearch(Results);
        var agent = new DeepResearchAgent(ai, search, new FakeFetcher(Pages), NullLogger<DeepResearchAgent>.Instance);

        var report = await agent.ResearchAsync(new ResearchRequest("Acme Robotics"));

        report.Status.Should().Be(ResearchStatus.Done);
        report.Queries.Should().Equal("Acme Robotics", "Acme Robotics funding");   // duplicates removed
        report.Sources.Should().HaveCount(2);                                      // the www copy is the same page
        report.Findings.Should().HaveCount(3);
        report.DroppedFindings.Should().Be(1);                                     // cited a source it was never given
        report.Findings.Where(f => f.Verified).Select(f => f.SourceIds.Single()).Should().Equal(1, 2);
        report.Findings.Single(f => !f.Verified).Claim.Should().Contain("400 employees");
        search.Queries.Should().HaveCount(2);

        var markdown = report.ToMarkdown();
        markdown.Should().Contain("## Findings").And.Contain("## Unverified").And.Contain($"]({About})");
    }

    [Fact]
    public async Task The_model_only_sees_the_numbered_sources()
    {
        var ai = Ai("""{"summary":"s","findings":[]}""");
        var agent = new DeepResearchAgent(ai, new FakeSearch(Results), new FakeFetcher(Pages), NullLogger<DeepResearchAgent>.Instance);

        await agent.ResearchAsync(new ResearchRequest("Acme Robotics", Focus: "funding"));

        var synthesis = ai.Requests.Single(r => r.TaskType == DeepResearchAgent.SynthesizeTask);
        synthesis.UserPrompt.Should().Contain("[1] ").And.Contain("[2] ").And.Contain("Focus: funding");
        synthesis.ResponseJsonSchema.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Says_no_evidence_when_search_finds_nothing()
    {
        var ai = Ai("""{"summary":"","findings":[]}""");
        var agent = new DeepResearchAgent(ai, new FakeSearch(_ => []), new FakeFetcher(Pages), NullLogger<DeepResearchAgent>.Instance);

        var report = await agent.ResearchAsync(new ResearchRequest("Nobody Knows Inc"));

        report.Status.Should().Be(ResearchStatus.NoEvidence);
        ai.Requests.Should().ContainSingle(r => r.TaskType == DeepResearchAgent.PlanTask);   // no synthesis call
        report.ToMarkdown().Should().Contain("No evidence");
    }

    [Fact]
    public async Task Falls_back_to_the_topic_when_planning_returns_garbage()
    {
        var ai = new ScriptedAi(r => r.TaskType == DeepResearchAgent.PlanTask ? "not json at all" : """{"summary":"s","findings":[]}""");
        var search = new FakeSearch(Results);
        var agent = new DeepResearchAgent(ai, search, new FakeFetcher(Pages), NullLogger<DeepResearchAgent>.Instance);

        await agent.ResearchAsync(new ResearchRequest("Acme Robotics", Focus: "funding"));

        search.Queries.Should().Equal("Acme Robotics funding");
    }

    [Fact]
    public async Task A_page_that_cannot_be_read_keeps_its_search_snippet()
    {
        var ai = Ai("""{"summary":"s","findings":[{"claim":"Acme builds robots.","sources":[1]}]}""");
        var agent = new DeepResearchAgent(ai, new FakeSearch(_ => [new("About Acme", "Acme builds robots", "https://down.example/")]),
            new FakeFetcher(new Dictionary<string, string>()), NullLogger<DeepResearchAgent>.Instance);

        var report = await agent.ResearchAsync(new ResearchRequest("Acme Robotics"));

        report.Sources.Should().ContainSingle().Which.FromSnippetOnly.Should().BeTrue();
        report.Findings.Should().ContainSingle().Which.Verified.Should().BeTrue();
    }
}
