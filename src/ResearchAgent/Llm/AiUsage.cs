namespace ResearchAgent.Llm;

/// <summary>Token use of one AI call. Every adapter must fill it: metering and real cost depend on it.</summary>
public sealed class AiUsage
{
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }

    /// <summary>
    /// The part of <see cref="InputTokens"/> served from the provider's cache (a subset, not an extra
    /// count). A fixed system prompt goes out with the same prefix on every call, so this part is billed
    /// at a discount; without counting it the cost report shows several times the real figure and
    /// prompt-size decisions rest on the wrong number.
    /// </summary>
    public int CachedInputTokens { get; init; }

    public int TotalTokens => InputTokens + OutputTokens;
}
