namespace ResearchAgent.Llm;

/// <summary>
/// Hook for quota and metering. The router asks it before a call (can this scope spend?) and tells
/// it the outcome afterwards. Hosts plug in their own wallet or usage ledger; the default does nothing.
/// The scope is whatever the host uses for ownership (tenant, account, project). It comes from
/// <see cref="AiTraceContext"/>, which the host fills from its session, never from the prompt.
/// </summary>
public interface IAiUsageMeter
{
    /// <summary>Throws <see cref="AiQuotaExceededException"/> when the scope cannot pay for the call.</summary>
    Task EnsureCanSpendAsync(string? scopeId, int cost, CancellationToken cancellationToken = default);

    Task RecordSuccessAsync(string? scopeId, string? userId, string taskType, AiCompletionResult result, int cost, CancellationToken cancellationToken = default);

    /// <summary>A failed call costs nothing, but it is worth a row for audit and debugging.</summary>
    Task RecordFailureAsync(string? scopeId, string? userId, string taskType, string? error, CancellationToken cancellationToken = default);
}

/// <summary>Default meter: no quota, no ledger.</summary>
public sealed class NullAiUsageMeter : IAiUsageMeter
{
    public Task EnsureCanSpendAsync(string? scopeId, int cost, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task RecordSuccessAsync(string? scopeId, string? userId, string taskType, AiCompletionResult result, int cost, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task RecordFailureAsync(string? scopeId, string? userId, string taskType, string? error, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
