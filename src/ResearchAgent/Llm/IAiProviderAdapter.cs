namespace ResearchAgent.Llm;

/// <summary>
/// Contract for one LLM provider (OpenAI, Gemini, ...). <see cref="AiRouter"/> tries adapters in
/// policy order: when the primary fails or is out of quota it falls back to the next one.
///
/// Every adapter must return the token use (<see cref="AiUsage"/>): metering and cost depend on it.
/// </summary>
public interface IAiProviderAdapter
{
    AiProvider Provider { get; }

    /// <summary>
    /// Whether the provider can be used right now (an API key is set, and so on). The router skips
    /// adapters that are not configured, so one missing key does not bring the whole flow down.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Whether this provider can read <see cref="AiCompletionRequest.Images"/>. Defaults to false:
    /// sending a request with images to a provider that cannot read them would drop the images silently
    /// and produce an "empty page" answer, so callers filter on this flag.
    /// </summary>
    bool SupportsImages => false;

    /// <summary>
    /// Completes the request with this provider. Throws on an error, a quota or a rate limit; the router
    /// catches it and moves on to the next provider.
    /// </summary>
    Task<AiCompletionResult> CompleteAsync(AiCompletionRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Multi-turn chat with tool calling (function calling), the agentic flow. Only OpenAI-compatible
    /// providers implement it; others throw <see cref="NotSupportedException"/> and the caller picks a
    /// provider that can. It does not affect the single-shot <see cref="CompleteAsync"/>.
    /// </summary>
    Task<AiChatResult> CompleteChatAsync(AiChatRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"{Provider} does not support tool calling (CompleteChatAsync).");
}
