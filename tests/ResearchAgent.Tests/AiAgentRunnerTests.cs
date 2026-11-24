using System.Text.Json;
using Microsoft.Extensions.Options;
using ResearchAgent.Agents;

namespace ResearchAgent.Tests;

[Trait("Category", "Unit")]
public class AiAgentRunnerTests
{
    private static readonly AiToolDefinition PriceTool = new()
    {
        Name = "get_price",
        Description = "Price of a plan",
        ParametersJsonSchema = """{"type":"object","properties":{"plan":{"type":"string"}}}""",
    };

    private static readonly AiToolDefinition HandOff = new()
    {
        Name = "hand_off",
        Description = "Hand the conversation to a person",
        ParametersJsonSchema = """{"type":"object","properties":{}}""",
        EndsLoop = true,
    };

    private static AiChatResult Turn(string? text = null, params AiToolCall[] calls) => new()
    {
        Text = text,
        ToolCalls = calls,
        Provider = AiProvider.OpenAI,
        Model = "fake",
        Usage = new AiUsage { InputTokens = 100, OutputTokens = 10 },
    };

    private static AiAgentRunner Runner(IAiProviderAdapter adapter) =>
        new([adapter], Options.Create(new AiOptions()), NullLogger<AiAgentRunner>.Instance);

    [Fact]
    public async Task Dispatches_a_tool_call_and_returns_the_final_answer()
    {
        var turns = new Queue<AiChatResult>([
            Turn(null, new AiToolCall { Id = "c1", Name = "get_price", ArgumentsJson = """{"plan":"pro"}""" }),
            Turn("The Pro plan costs 20 EUR a month."),
        ]);
        var adapter = new ScriptedAdapter(AiProvider.OpenAI, chat: _ => turns.Dequeue());
        string? seenPlan = null;

        var result = await Runner(adapter).RunAsync("support.chat", "You answer pricing questions.",
            [new AiChatMessage { Role = "user", Content = "How much is Pro?" }], [PriceTool],
            (name, args, _) =>
            {
                seenPlan = args.GetProperty("plan").GetString();
                return Task.FromResult(JsonSerializer.Serialize(new { price = "20 EUR" }));
            });

        result.Text.Should().Be("The Pro plan costs 20 EUR a month.");
        seenPlan.Should().Be("pro");
        result.Usage.InputTokens.Should().Be(200);   // both turns are counted
        adapter.ChatRequests[1].Messages.Should().Contain(m => m.Role == "tool" && m.ToolCallId == "c1");
    }

    [Fact]
    public async Task A_loop_ending_tool_stops_without_dispatching()
    {
        var adapter = new ScriptedAdapter(AiProvider.OpenAI,
            chat: _ => Turn("Let me find a colleague.", new AiToolCall { Id = "c1", Name = "hand_off", ArgumentsJson = "{}" }));
        var dispatched = false;

        var result = await Runner(adapter).RunAsync("support.chat", "system",
            [new AiChatMessage { Role = "user", Content = "I want a human." }], [PriceTool, HandOff],
            (_, _, _) => { dispatched = true; return Task.FromResult("{}"); });

        result.EndedByTool.Should().Be("hand_off");
        dispatched.Should().BeFalse();
        adapter.ChatRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task A_failing_tool_is_reported_to_the_model_instead_of_crashing()
    {
        var turns = new Queue<AiChatResult>([
            Turn(null, new AiToolCall { Id = "c1", Name = "get_price", ArgumentsJson = "not json" }),
            Turn("Sorry, I could not look the price up."),
        ]);
        var adapter = new ScriptedAdapter(AiProvider.OpenAI, chat: _ => turns.Dequeue());

        var result = await Runner(adapter).RunAsync("support.chat", "system",
            [new AiChatMessage { Role = "user", Content = "Price?" }], [PriceTool],
            (_, _, _) => throw new InvalidOperationException("db down"));

        result.Text.Should().Be("Sorry, I could not look the price up.");
        adapter.ChatRequests[1].Messages.Last().Content.Should().Contain("tool failed");
    }
}
