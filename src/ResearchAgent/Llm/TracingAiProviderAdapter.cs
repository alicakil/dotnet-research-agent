using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace ResearchAgent.Llm;

/// <summary>
/// Decorator around every provider adapter: it records each real provider call, including failed
/// fallback attempts, as one <see cref="AiTraceRecord"/> with the exact prompts, the response, tokens,
/// cost and latency. Identity columns come from <see cref="AiTraceContext"/>, never from the payload.
/// Recording never breaks the call: the recorder is fail-safe and a failure is re-thrown unchanged so
/// the router's fallback keeps working.
/// </summary>
public sealed class TracingAiProviderAdapter : IAiProviderAdapter
{
    private readonly IAiProviderAdapter _inner;
    private readonly IAiTraceRecorder _recorder;
    private readonly AiTraceContext _context;
    private readonly AiOptions _options;

    public TracingAiProviderAdapter(
        IAiProviderAdapter inner, IAiTraceRecorder recorder, AiTraceContext context, IOptions<AiOptions> options)
    {
        _inner = inner;
        _recorder = recorder;
        _context = context;
        _options = options.Value;
    }

    public AiProvider Provider => _inner.Provider;

    public bool IsConfigured => _inner.IsConfigured;

    // Every capability flag has to be forwarded, not inherited: this decorator wraps every adapter,
    // so an un-forwarded member silently reports the interface DEFAULT for the whole fleet. That is
    // what happened with images: callers filter on SupportsImages, the decorator answered false for
    // OpenAI too, and screenshot reading failed instantly with "no capable provider".
    public bool SupportsImages => _inner.SupportsImages;

    public async Task<AiCompletionResult> CompleteAsync(AiCompletionRequest request, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var systemText = AiAdapterHelpers.ComposeSystemPrompt(request);   // the exact system text the model receives
        try
        {
            var result = await _inner.CompleteAsync(request, cancellationToken);
            sw.Stop();
            await SafeRecord(NewTrace(request.TaskType, systemText, request.UserPrompt)
                .WithSuccess(result, sw.ElapsedMilliseconds, ComputeCost(result)), request.TaskType);
            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            await SafeRecord(NewTrace(request.TaskType, systemText, request.UserPrompt)
                .WithFailure(ex, sw.ElapsedMilliseconds), request.TaskType);
            throw;   // let the router fall back
        }
    }

    public async Task<AiChatResult> CompleteChatAsync(AiChatRequest request, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var systemText = request.Messages.FirstOrDefault(m => m.Role == "system")?.Content;
        var conversation = JsonSerializer.Serialize(request.Messages);   // the full multi-turn context
        try
        {
            var result = await _inner.CompleteChatAsync(request, cancellationToken);
            sw.Stop();
            var response = JsonSerializer.Serialize(new { text = result.Text, toolCalls = result.ToolCalls });
            var trace = NewTrace(request.TaskType, systemText, conversation);
            trace.Provider = result.Provider.ToString();
            trace.Model = result.Model;
            trace.ResponseText = response;
            trace.InputTokens = result.Usage.InputTokens;
            trace.OutputTokens = result.Usage.OutputTokens;
            trace.CachedInputTokens = result.Usage.CachedInputTokens;
            trace.CostUsd = _options.ComputeCostUsd(result.Provider, result.Model, result.Usage);
            trace.LatencyMs = (int)sw.ElapsedMilliseconds;
            trace.Success = true;
            await SafeRecord(trace, request.TaskType);
            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            await SafeRecord(NewTrace(request.TaskType, systemText, conversation)
                .WithFailure(ex, sw.ElapsedMilliseconds), request.TaskType);
            throw;
        }
    }

    private decimal ComputeCost(AiCompletionResult result) =>
        _options.ComputeCostUsd(result.Provider, result.Model, result.Usage);

    private AiTraceRecord NewTrace(string taskType, string? systemPrompt, string? userPrompt) => new()
    {
        CorrelationId = _context.CorrelationId ?? Guid.NewGuid(),
        ScopeId = _context.ScopeId,
        UserId = _context.UserId,
        ConversationId = _context.ConversationId,
        Surface = _context.Surface,
        Properties = _context.Properties.Count > 0 ? new Dictionary<string, string>(_context.Properties) : null,
        TaskType = taskType,
        Provider = _inner.Provider.ToString(),   // which provider, even when a fallback attempt failed
        SystemPrompt = systemPrompt,
        UserPrompt = userPrompt,
    };

    private async Task SafeRecord(AiTraceRecord trace, string taskType)
    {
        // Tasks that carry personal text keep only the fixed system prompt, timing, tokens, cost and
        // errors: the user prompt and the response stay out of the trace store.
        if (!_options.GetPolicy(taskType).TraceContent)
        {
            trace.UserPrompt = null;
            trace.ResponseText = null;
        }

        try
        {
            // Never tied to the request's cancellation: a cancelled call still deserves its row.
            await _recorder.RecordAsync(trace, CancellationToken.None);
        }
        catch
        {
            // A trace store outage must not break AI calls.
        }
    }
}

internal static class AiTraceRecordExtensions
{
    public static AiTraceRecord WithSuccess(this AiTraceRecord trace, AiCompletionResult result, long latencyMs, decimal costUsd)
    {
        trace.Provider = result.Provider.ToString();
        trace.Model = result.Model;
        trace.ResponseText = result.Text;
        trace.InputTokens = result.Usage.InputTokens;
        trace.OutputTokens = result.Usage.OutputTokens;
        trace.CachedInputTokens = result.Usage.CachedInputTokens;
        trace.CostUsd = costUsd;
        trace.LatencyMs = (int)latencyMs;
        trace.Success = true;
        return trace;
    }

    public static AiTraceRecord WithFailure(this AiTraceRecord trace, Exception ex, long latencyMs)
    {
        trace.LatencyMs = (int)latencyMs;
        trace.Success = false;
        var msg = ex.Message ?? ex.GetType().Name;
        trace.ErrorMessage = msg.Length > 2000 ? msg[..2000] : msg;
        return trace;
    }
}
