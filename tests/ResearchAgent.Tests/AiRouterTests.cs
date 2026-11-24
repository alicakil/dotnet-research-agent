using Microsoft.Extensions.Options;

namespace ResearchAgent.Tests;

[Trait("Category", "Unit")]
public class AiRouterTests
{
    private static AiCompletionRequest Request(IReadOnlyList<string>? images = null) => new()
    {
        TaskType = "test.task",
        UserPrompt = "Say hello.",
        Images = images,
    };

    private static AiRouter Router(IAiUsageMeter meter, params IAiProviderAdapter[] adapters) =>
        new(adapters, Options.Create(new AiOptions()), meter, new AiTraceContext(), NullLogger<AiRouter>.Instance);

    [Fact]
    public async Task Falls_back_to_the_next_provider_when_the_first_one_fails()
    {
        var openAi = new ScriptedAdapter(AiProvider.OpenAI, _ => throw new HttpRequestException("OpenAI 500"));
        var gemini = new ScriptedAdapter(AiProvider.Gemini, _ => "hello from gemini");
        var meter = new RecordingMeter();

        var result = await Router(meter, openAi, gemini).CompleteAsync(Request());

        result.Text.Should().Be("hello from gemini");
        result.Provider.Should().Be(AiProvider.Gemini);
        meter.Successes.Should().Be(1);
        meter.Failures.Should().Be(0);
    }

    [Fact]
    public async Task Reports_a_friendly_error_and_charges_nothing_when_every_provider_fails()
    {
        var openAi = new ScriptedAdapter(AiProvider.OpenAI, _ => throw new HttpRequestException("OpenAI 429 rate_limit"));
        var gemini = new ScriptedAdapter(AiProvider.Gemini, _ => throw new HttpRequestException("Gemini 429"));
        var meter = new RecordingMeter();

        var act = () => Router(meter, openAi, gemini).CompleteAsync(Request());

        (await act.Should().ThrowAsync<AiCompletionFailedException>()).Which.Message.Should().Contain("rate limit");
        meter.Successes.Should().Be(0);
        meter.Failures.Should().Be(1);
        meter.RemainingCredits.Should().Be(100);
    }

    [Fact]
    public async Task A_request_with_images_skips_providers_that_cannot_read_them()
    {
        var openAi = new ScriptedAdapter(AiProvider.OpenAI, _ => "I can see the image", images: true);
        var gemini = new ScriptedAdapter(AiProvider.Gemini, _ => "text only");
        var router = new AiRouter([gemini, openAi],
            Options.Create(new AiOptions { DefaultPolicy = new AiTaskPolicy { Providers = [AiProvider.Gemini, AiProvider.OpenAI] } }),
            new NullAiUsageMeter(), new AiTraceContext(), NullLogger<AiRouter>.Instance);

        var result = await router.CompleteAsync(Request(["data:image/png;base64,AAAA"]));

        result.Provider.Should().Be(AiProvider.OpenAI);
        gemini.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task The_quota_guard_runs_before_any_provider_is_called()
    {
        var openAi = new ScriptedAdapter(AiProvider.OpenAI, _ => "hello");
        var meter = new RecordingMeter { RemainingCredits = 0 };

        var act = () => Router(meter, openAi).CompleteAsync(Request());

        await act.Should().ThrowAsync<AiQuotaExceededException>();
        openAi.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task The_policy_fills_what_the_request_leaves_open()
    {
        var openAi = new ScriptedAdapter(AiProvider.OpenAI, _ => "ok");
        var options = new AiOptions();
        options.Policies["test.task"] = new AiTaskPolicy { MaxOutputTokens = 321, Model = "big-model", ReasoningEffort = "high" };
        var router = new AiRouter([openAi], Options.Create(options), new NullAiUsageMeter(), new AiTraceContext(), NullLogger<AiRouter>.Instance);

        await router.CompleteAsync(Request());

        var sent = openAi.Requests.Should().ContainSingle().Subject;
        sent.MaxOutputTokens.Should().Be(321);
        sent.Model.Should().Be("big-model");
        sent.ReasoningEffort.Should().Be("high");
    }
}
