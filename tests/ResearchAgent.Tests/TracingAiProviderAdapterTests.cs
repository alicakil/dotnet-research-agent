using DAL.Entites;
using FluentAssertions;
using Microsoft.Extensions.Options;
using WebApi.Services.Ai;
using Xunit;

namespace Nebula.UnitTests.WebApi.Services;

/// <summary>
/// İz kaydının içerik kilidi (VIT-656). <c>AiTrace</c> her çağrının giden metnini ve yanıtını 180 gün saklar ve admin
/// asistanı okur; kişisel sağlık metni taşıyan işlerde (<see cref="AiTaskTaxonomy.TracesContent"/> = false) ikisi de
/// yazılmamalı. Süre, token, maliyet ve hata yine yazılır: maliyet raporu ve teşhis bozulmasın.
/// </summary>
[Trait("Category", "Unit")]
public class TracingAiProviderAdapterTests
{
    private const string Sentence = "Dün gece yine mide yanması oldu.";
    private const string Answer = "{\"kind\":\"Problem\",\"title\":\"Mide yanması\"}";

    [Fact]
    public void Health_tasks_are_the_tasks_that_do_not_trace_content()
    {
        AiTaskTaxonomy.TracesContent(AiTaskType.HealthCapture).Should().BeFalse();
        AiTaskTaxonomy.TracesContent(AiTaskType.HealthDocument).Should().BeFalse();
        AiTaskTaxonomy.TracesContent(AiTaskType.WorkVoiceNote).Should().BeTrue();
    }

    [Fact]
    public async Task Health_text_and_the_answer_are_not_written_to_the_trace()
    {
        var recorder = new CapturingRecorder();
        var adapter = Adapter(recorder, _ => Answer);

        await adapter.CompleteAsync(Request(AiTaskType.HealthCapture));

        var trace = recorder.Traces.Should().ContainSingle().Subject;
        trace.UserPrompt.Should().BeNull();
        trace.ResponseText.Should().BeNull();
        trace.TaskType.Should().Be(nameof(AiTaskType.HealthCapture));
        trace.SystemPrompt.Should().Contain("filing clerk");
        trace.Success.Should().BeTrue();
        trace.InputTokens.Should().Be(120);
        trace.OutputTokens.Should().Be(30);
    }

    [Fact]
    public async Task Health_text_is_not_written_when_the_call_fails_either()
    {
        var recorder = new CapturingRecorder();
        var adapter = Adapter(recorder, _ => throw new HttpRequestException("OpenAI 503"));

        await FluentActions.Awaiting(() => adapter.CompleteAsync(Request(AiTaskType.HealthCapture)))
            .Should().ThrowAsync<HttpRequestException>();

        var trace = recorder.Traces.Should().ContainSingle().Subject;
        trace.UserPrompt.Should().BeNull();
        trace.Success.Should().BeFalse();
        trace.ErrorMessage.Should().Be("OpenAI 503");
    }

    [Fact]
    public async Task Other_tasks_keep_their_full_trace()
    {
        var recorder = new CapturingRecorder();
        var adapter = Adapter(recorder, _ => Answer);

        await adapter.CompleteAsync(Request(AiTaskType.WorkVoiceNote));

        var trace = recorder.Traces.Should().ContainSingle().Subject;
        trace.UserPrompt.Should().Be(Sentence);
        trace.ResponseText.Should().Be(Answer);
    }

    private static AiCompletionRequest Request(AiTaskType taskType) => new()
    {
        TaskType = taskType,
        SystemPrompt = "You are a filing clerk, not a clinician.",
        UserPrompt = Sentence,
    };

    private static TracingAiProviderAdapter Adapter(IAiTraceRecorder recorder, Func<AiCompletionRequest, string> respond) =>
        new(new FakeAdapter(respond), recorder, new AiTraceContext(), Options.Create(new AiOptions()));

    private sealed class CapturingRecorder : IAiTraceRecorder
    {
        public List<AiTraceEntity> Traces { get; } = [];

        public Task RecordAsync(AiTraceEntity trace, CancellationToken cancellationToken = default)
        {
            Traces.Add(trace);
            return Task.CompletedTask;
        }
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
