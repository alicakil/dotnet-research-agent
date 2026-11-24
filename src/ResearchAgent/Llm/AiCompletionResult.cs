namespace ResearchAgent.Llm;

/// <summary>
/// Result of one completion: the text, the provider and model that really served it, and the tokens
/// spent. Provider and model can differ from the primary of the policy after a fallback; metering
/// records these real values.
/// </summary>
public sealed class AiCompletionResult
{
    /// <summary>The produced text; a JSON string matching the schema when <c>ResponseJsonSchema</c> was set.</summary>
    public required string Text { get; init; }

    /// <summary>The provider that served the request (may differ from the primary after a fallback).</summary>
    public required AiProvider Provider { get; init; }

    /// <summary>The model name used (for example "gemini-2.5-flash").</summary>
    public required string Model { get; init; }

    public required AiUsage Usage { get; init; }
}
