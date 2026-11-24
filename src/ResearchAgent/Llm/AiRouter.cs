using Microsoft.Extensions.Options;

namespace ResearchAgent.Llm;

/// <summary>
/// <see cref="IAiCompletionService"/> implementation. Chain of responsibility for every call:
/// identity (from the ambient context) → quota guard → provider choice with fallback → metering.
/// Application services inject only this; they never know which provider answered.
/// </summary>
public sealed class AiRouter : IAiCompletionService
{
    private readonly IEnumerable<IAiProviderAdapter> _adapters;
    private readonly AiOptions _options;
    private readonly IAiUsageMeter _meter;
    private readonly AiTraceContext _traceContext;
    private readonly ILogger<AiRouter> _logger;

    public AiRouter(
        IEnumerable<IAiProviderAdapter> adapters,
        IOptions<AiOptions> options,
        IAiUsageMeter meter,
        AiTraceContext traceContext,
        ILogger<AiRouter> logger)
    {
        _adapters = adapters;
        _options = options.Value;
        _meter = meter;
        _traceContext = traceContext;
        _logger = logger;
    }

    public async Task<AiCompletionResult> CompleteAsync(AiCompletionRequest request, CancellationToken cancellationToken = default)
    {
        // 1. Identity comes from the ambient context that the host filled from its session; never
        //    from the request or the prompt. Fallback attempts share one correlation id.
        var scopeId = _traceContext.ScopeId;
        var userId = _traceContext.UserId;
        _traceContext.BeginOperation();

        var policy = _options.GetPolicy(request.TaskType);

        // 2. Quota guard: the meter throws AiQuotaExceededException when the scope cannot pay.
        await _meter.EnsureCanSpendAsync(scopeId, policy.Cost, cancellationToken);

        // The policy fills what the request left open; a value on the request wins.
        // Images are carried over as they are: dropping them would make the model answer as if it
        // had read an empty page, the kind of failure nobody notices.
        var effectiveRequest = new AiCompletionRequest
        {
            TaskType = request.TaskType,
            SystemPrompt = request.SystemPrompt,
            UserPrompt = request.UserPrompt,
            Images = request.Images,
            ResponseJsonSchema = request.ResponseJsonSchema,
            MaxOutputTokens = request.MaxOutputTokens ?? policy.MaxOutputTokens,
            Model = request.Model ?? policy.Model,
            ReasoningEffort = request.ReasoningEffort ?? policy.ReasoningEffort,
        };

        // A request with images may only go to a provider that can read them; the others leave the chain.
        var needsVision = request.Images is { Count: > 0 };
        if (needsVision && !policy.Providers.Any(pv => _adapters.Any(a => a.Provider == pv && a.IsConfigured && a.SupportsImages)))
            throw new AiCompletionFailedException("No configured provider can read images; the request cannot be served.");

        // 3. Providers in policy order, with fallback.
        Exception? lastError = null;
        foreach (var providerType in policy.Providers)
        {
            var adapter = _adapters.FirstOrDefault(a => a.Provider == providerType && a.IsConfigured && (!needsVision || a.SupportsImages));
            if (adapter is null)
            {
                _logger.LogDebug("AI provider {Provider} skipped (not registered or no API key).", providerType);
                continue;
            }

            try
            {
                var result = await adapter.CompleteAsync(effectiveRequest, cancellationToken);

                // 4. Metering: charge the scope and write a success row.
                await _meter.RecordSuccessAsync(scopeId, userId, request.TaskType, result, policy.Cost, cancellationToken);

                _logger.LogInformation(
                    "AI call done: scope={Scope}, task={Task}, provider={Provider}, in={In}, out={Out}, cost={Cost}",
                    scopeId, request.TaskType, result.Provider, result.Usage.InputTokens, result.Usage.OutputTokens, policy.Cost);

                return result;
            }
            catch (Exception ex)
            {
                lastError = ex;
                _logger.LogWarning(ex, "AI provider {Provider} failed; trying the next one.", providerType);
            }
        }

        // 5. All failed: nothing is charged, a failure row is written, and the caller gets a friendly error.
        await _meter.RecordFailureAsync(scopeId, userId, request.TaskType, lastError?.Message, cancellationToken);

        var errText = lastError?.Message ?? string.Empty;
        var isRateLimited = errText.Contains("429") || errText.Contains("rate_limit", StringComparison.OrdinalIgnoreCase);
        var isDailyQuota = errText.Contains("per day", StringComparison.OrdinalIgnoreCase) || errText.Contains("(TPD)", StringComparison.OrdinalIgnoreCase);
        throw new AiCompletionFailedException(
            isDailyQuota
                ? "The AI provider's daily quota is used up; it renews tomorrow."
                : isRateLimited
                    ? "The AI provider is busy right now (rate limit). Please try again in a few seconds."
                    : "The AI service is temporarily unavailable. Please try again shortly.",
            lastError);
    }
}
