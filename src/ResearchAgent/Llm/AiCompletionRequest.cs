namespace ResearchAgent.Llm;

/// <summary>
/// One provider-independent completion request.
///
/// SECURITY: identity (scope, user, role) is NOT here. It is taken from the session at call time,
/// never from the caller's data or from the prompt, so one tenant cannot "ask for" another tenant's
/// data: the model decides "what" to produce, never "who" is asking. Grounding data (for example a
/// tenant's own records) is fetched beforehand by the calling service, with its own authorization,
/// and placed into <see cref="SystemPrompt"/> or <see cref="UserPrompt"/>.
/// </summary>
public sealed class AiCompletionRequest
{
    /// <summary>Task name ("research.plan", "support.chat"...). It selects the policy: models, limits, cost.</summary>
    public required string TaskType { get; init; }

    /// <summary>System instruction: role, rules and grounding context (data fetched beforehand).</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>The user's or the task's prompt.</summary>
    public required string UserPrompt { get; init; }

    /// <summary>
    /// Image inputs as data URIs (<c>data:image/jpeg;base64,…</c>). When set, the request may only go to
    /// a provider that can read images. Other adapters opt out through
    /// <see cref="IAiProviderAdapter.SupportsImages"/> and the fallback chain skips them: dropping the
    /// images silently and sending the text alone would produce an answer about an empty page, the
    /// worst kind of failure because nobody notices it.
    /// </summary>
    public IReadOnlyList<string>? Images { get; init; }

    /// <summary>
    /// When set, the provider is asked for structured output that matches this JSON schema; otherwise
    /// free text comes back. How strictly a provider follows the schema varies, so adapters do their
    /// best and the calling service still validates the output.
    /// </summary>
    public string? ResponseJsonSchema { get; init; }

    /// <summary>Reasoning effort override: the policy decides, the adapter applies it. Null = model default. Only OpenAI reads it.</summary>
    public string? ReasoningEffort { get; init; }

    /// <summary>Maximum output tokens (a cost ceiling). Null = provider or policy default.</summary>
    public int? MaxOutputTokens { get; init; }

    /// <summary>
    /// Model override: when set it replaces the provider's configured default. Callers usually pass
    /// <c>policy.Model</c> here. Only the OpenAI adapter reads it, because model names are provider
    /// specific: after a fallback to another provider that provider uses its own configured model on
    /// purpose, otherwise it would answer 400 to a model name it does not know.
    /// </summary>
    public string? Model { get; init; }
}
