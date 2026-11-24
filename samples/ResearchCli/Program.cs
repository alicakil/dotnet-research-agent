// Research a company or topic from the command line and print a cited Markdown report.
//
//   export OPENAI_API_KEY=...       # or GEMINI_API_KEY (at least one model provider)
//   export SERPER_API_KEY=...       # web search
//   export FIRECRAWL_API_KEY=...    # optional: reads pages that need a real browser
//   dotnet run --project samples/ResearchCli -- "Acme Robotics, Madrid" --focus "products and customers"

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ResearchAgent;
using ResearchAgent.Research;

var topic = args.FirstOrDefault(a => !a.StartsWith("--"));
if (string.IsNullOrWhiteSpace(topic))
{
    Console.Error.WriteLine("Usage: ResearchCli \"<company or topic>\" [--focus \"<question>\"] [--country es]");
    return 1;
}

string? Option(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

var builder = Host.CreateApplicationBuilder();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Services.AddResearchAgent(builder.Configuration);

using var host = builder.Build();
using var scope = host.Services.CreateScope();
var agent = scope.ServiceProvider.GetRequiredService<IDeepResearchAgent>();

var report = await agent.ResearchAsync(new ResearchRequest(topic, Option("--focus"), Option("--country")));
Console.WriteLine(report.ToMarkdown());
return report.Status == ResearchStatus.Failed ? 2 : 0;
