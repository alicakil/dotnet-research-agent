namespace ResearchAgent.Llm;

// ── Tool-calling (agentic) chat models ──
// SEPARATE from the single-shot AiCompletionRequest/Result; they support multi-turn chat and tools (functions).
// They CARRY NO IDENTITY (same security invariant as single-shot: "what" comes from the prompt, "who" from the session).

/// <summary>One message in a tool-calling chat (system/user/assistant/tool).</summary>
public sealed class AiChatMessage
{
    /// <summary>"system" | "user" | "assistant" | "tool".</summary>
    public required string Role { get; init; }

    /// <summary>Text content (may be null when the assistant is calling tools).</summary>
    public string? Content { get; init; }

    /// <summary>The assistant's tool call requests, if any.</summary>
    public IReadOnlyList<AiToolCall>? ToolCalls { get; init; }

    /// <summary>For role="tool": the tool call (Id) this message answers.</summary>
    public string? ToolCallId { get; init; }

    /// <summary>
    /// A chat message that came from the CLIENT → the message that goes to the model. A client may only
    /// send the two sides of a conversation: its own ("user") and the assistant's earlier reply (called
    /// "ai" by the chat UI; "assistant" is accepted too). Case-insensitive. Any other role and any empty
    /// content return null: the caller drops the message instead of rejecting the request.
    ///
    /// Chat endpoints build the message ONLY from here. When the role was passed through unchanged, an
    /// anonymous visitor could send an instruction-level message with the "developer" or "system" role,
    /// and a "tool" role reached the model as an orphaned tool result.
    /// </summary>
    public static AiChatMessage? FromClient(string? role, string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;

        var modelRole = role?.ToLowerInvariant() switch
        {
            "user" => "user",
            "ai" or "assistant" => "assistant",
            _ => null,
        };
        return modelRole is null ? null : new AiChatMessage { Role = modelRole, Content = content };
    }
}

/// <summary>The model's request to call a tool (function).</summary>
public sealed class AiToolCall
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>The arguments the model supplied (raw JSON string).</summary>
    public required string ArgumentsJson { get; init; }
}

/// <summary>A tool definition offered to the model (function schema).</summary>
public sealed class AiToolDefinition
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    /// <summary>JSON Schema (object). For a tool without parameters: {"type":"object","properties":{}}.</summary>
    public required string ParametersJsonSchema { get; init; }

    /// <summary>
    /// A tool that ends the loop (for example a hand-off between two assistants): when the model calls it,
    /// <see cref="ResearchAgent.Agents.AiAgentRunner"/> does not dispatch the tool and does not run the model for
    /// another turn; it returns the call to its caller in <see cref="ResearchAgent.Agents.AiAgentResult.EndedByTool"/>.
    /// It is not sent to the provider (adapters read only the name, description and schema).
    /// </summary>
    public bool EndsLoop { get; init; }
}

/// <summary>A tool-calling chat request (multi-turn plus tools). NO IDENTITY.</summary>
public sealed class AiChatRequest
{
    public required string TaskType { get; init; }
    public required IReadOnlyList<AiChatMessage> Messages { get; init; }
    public IReadOnlyList<AiToolDefinition>? Tools { get; init; }
    public int? MaxOutputTokens { get; init; }
    /// <summary>OpenAI model override (from the policy; null → the adapter's default).</summary>
    public string? Model { get; init; }

    /// <summary>
    /// reasoning_effort override (from the policy; null → a default that depends on the model, see
    /// <see cref="AiAdapterHelpers.ResolveReasoningEffort"/>).
    ///
    /// This used to be read ONLY by the single-shot <c>CompleteAsync</c>; the chat surfaces silently
    /// fell back to the model's default whatever the policy said. For gpt-5-mini that default was
    /// "minimal": the assistant was effectively answering without reasoning and, on hard questions,
    /// used up its thinking budget and returned an EMPTY answer.
    /// </summary>
    public string? ReasoningEffort { get; init; }

    /// <summary>
    /// Whether the OpenAI call goes through <c>/v1/responses</c> (comes from the policy). Other
    /// providers ignore it: they have only one schema.
    /// </summary>
    public bool UseResponsesApi { get; init; }

    /// <summary>
    /// The previous step of the Responses chain. When set, the adapter sends only the NEW items (the tool
    /// results); the history, including the model's own reasoning item, stays on the OpenAI side.
    ///
    /// Why carry the reasoning item: in the first turn the model makes a plan such as "call the price tool
    /// first". If that plan is not carried over, the tool data that comes back in the second turn has no
    /// context and the model starts thinking from scratch: slower, more expensive and, because each turn
    /// is independent of the last, less consistent. With the chain, the second turn measured 0 reasoning tokens.
    ///
    /// When null, the full message list is sent: the chain is a speed-up, not a dependency.
    /// </summary>
    public string? PreviousResponseId { get; init; }
}

/// <summary>Result of a tool-calling chat: either the final <see cref="Text"/> or <see cref="ToolCalls"/> (a tool request).</summary>
public sealed class AiChatResult
{
    public string? Text { get; init; }
    public IReadOnlyList<AiToolCall> ToolCalls { get; init; } = Array.Empty<AiToolCall>();
    public required AiProvider Provider { get; init; }
    public required string Model { get; init; }
    public required AiUsage Usage { get; init; }

    /// <summary>
    /// On the Responses path, the id of this turn; the runner passes it to the next turn as
    /// <c>PreviousResponseId</c>. Null on the chat/completions path, where the runner continues with the
    /// full message list.
    /// </summary>
    public string? ResponseId { get; init; }
}
