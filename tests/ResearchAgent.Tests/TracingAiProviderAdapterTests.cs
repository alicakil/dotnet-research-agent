using Microsoft.Extensions.Options;

namespace ResearchAgent.Tests;

/// <summary>
/// The content lock of traces. A trace keeps the exact prompt and response of every call, so tasks that
/// carry personal text (policy <see cref="AiTaskPolicy.TraceContent"/> = false) must not store either.
/// Timing, tokens, cost and errors are still written: the cost report and diagnosis must not suffer.
/// </summary>
[Trait("Category", "Unit")]
public class TracingAiProviderAdapterTests
{
    private const string PersonalTask = "notes.personal";
    private const string Sentence = "I had heartburn again last night.";
    private const string Answer = "{\"kind\":\"Problem\",\"title\":\"Heartburn\"}";

    [Fact]
    public async Task Personal_text_and_the_answer_are_not_written_to_the_trace()
    {
        var recorder = new CapturingRecorder();
        var adapter = Adapter(recorder, _ => Answer);

        await adapter.CompleteAsync(Request(PersonalTask));

        var trace = recorder.Traces.Should().ContainSingle().Subject;
        trace.UserPrompt.Should().BeNull();
        trace.ResponseText.Should().BeNull();
        trace.TaskType.Should().Be(PersonalTask);
        trace.SystemPrompt.Should().Contain("filing clerk");
        trace.Success.Should().BeTrue();
        trace.InputTokens.Should().Be(120);
        trace.OutputTokens.Should().Be(30);
    }

    [Fact]
    public async Task Personal_text_is_not_written_when_the_call_fails_either()
    {
        var recorder = new CapturingRecorder();
        var adapter = Adapter(recorder, _ => throw new HttpRequestException("OpenAI 503"));

        await FluentActions.Awaiting(() => adapter.CompleteAsync(Request(PersonalTask)))
            .Should().ThrowAsync<HttpRequestException>();

        var trace = recorder.Traces.Should().ContainSingle().Subject;
        trace.UserPrompt.Should().BeNull();
        trace.Success.Should().BeFalse();
        trace.ErrorMessage.Should().Be("OpenAI 503");
    }

    [Fact]
    public async Task Other_tasks_keep_their_full_trace_and_identity_comes_from_the_context()
    {
        var recorder = new CapturingRecorder();
        var context = new AiTraceContext { ScopeId = "tenant-1", UserId = "user-7", Surface = "tests" };
        context.BeginOperation();
        var adapter = Adapter(recorder, _ => Answer, context);

        await adapter.CompleteAsync(Request("message.polish"));

        var trace = recorder.Traces.Should().ContainSingle().Subject;
        trace.UserPrompt.Should().Be(Sentence);
        trace.ResponseText.Should().Be(Answer);
        trace.ScopeId.Should().Be("tenant-1");
        trace.UserId.Should().Be("user-7");
        trace.CorrelationId.Should().Be(context.CorrelationId!.Value);
    }

    [Fact]
    public async Task A_failing_recorder_does_not_break_the_call()
    {
        var adapter = Adapter(new ThrowingRecorder(), _ => Answer);

        var result = await adapter.CompleteAsync(Request("message.polish"));

        result.Text.Should().Be(Answer);
    }

    private static AiCompletionRequest Request(string taskType) => new()
    {
        TaskType = taskType,
        SystemPrompt = "You are a filing clerk, not a clinician.",
        UserPrompt = Sentence,
    };

    private static TracingAiProviderAdapter Adapter(IAiTraceRecorder recorder, Func<AiCompletionRequest, string> respond, AiTraceContext? context = null)
    {
        var options = new AiOptions();
        options.Policies[PersonalTask] = new AiTaskPolicy { TraceContent = false };
        return new(new FakeAdapter(respond), recorder, context ?? new AiTraceContext(), Options.Create(options));
    }

    private sealed class CapturingRecorder : IAiTraceRecorder
    {
        public List<AiTraceRecord> Traces { get; } = [];

        public Task RecordAsync(AiTraceRecord trace, CancellationToken cancellationToken = default)
        {
            Traces.Add(trace);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingRecorder : IAiTraceRecorder
    {
        public Task RecordAsync(AiTraceRecord trace, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("trace store down");
    }

    private sealed class FakeAdapter(Func<AiCompletionRequest, string> respond) : IAiProviderAdapter
    {
        public AiProvider Provider => AiProvider.OpenAI;

        public bool IsConfigured => true;

        public Task<AiCompletionResult> CompleteAsync(AiCompletionRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AiCompletionResult
            {
                Text = respond(request),
                Provider = AiProvider.OpenAI,
                Model = "fake",
                Usage = new AiUsage { InputTokens = 120, OutputTokens = 30 },
            });
    }
}
