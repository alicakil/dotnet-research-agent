namespace ResearchAgent.Llm;

/// <summary>
/// A concrete LLM provider. Routing (fallback) and metering work on this value. A new provider is one
/// more value here and one more <see cref="IAiProviderAdapter"/>. The numbers are stable because stored
/// traces carry them; 2 belonged to a provider that has been removed.
/// </summary>
public enum AiProvider
{
    Gemini = 1,
    OpenAI = 3,
}
