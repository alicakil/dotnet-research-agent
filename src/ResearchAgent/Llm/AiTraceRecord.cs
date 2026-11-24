namespace ResearchAgent.Llm;

/// <summary>One provider call as it really happened: the exact prompts, the response, tokens, cost and timing.</summary>
public sealed class AiTraceRecord
{
    public Guid CorrelationId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? ScopeId { get; set; }
    public string? UserId { get; set; }
    public string? ConversationId { get; set; }
    public string? Surface { get; set; }
    public IReadOnlyDictionary<string, string>? Properties { get; set; }
    public string TaskType { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string? Model { get; set; }
    public string? SystemPrompt { get; set; }
    public string? UserPrompt { get; set; }
    public string? ResponseText { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int CachedInputTokens { get; set; }
    public decimal CostUsd { get; set; }
    public int LatencyMs { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>Where traces go: a database table, a log, an observability tool. It must never throw.</summary>
public interface IAiTraceRecorder
{
    Task RecordAsync(AiTraceRecord trace, CancellationToken cancellationToken = default);
}

/// <summary>Default recorder: drops traces.</summary>
public sealed class NullAiTraceRecorder : IAiTraceRecorder
{
    public Task RecordAsync(AiTraceRecord trace, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
