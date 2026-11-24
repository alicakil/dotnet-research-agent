namespace ResearchAgent.Llm;

/// <summary>
/// Request-scoped, ambient identity for traces. Upper layers (an endpoint, the router, an agent)
/// fill it; <see cref="TracingAiProviderAdapter"/> reads it at the bottom of the stack. Identity
/// therefore reaches the trace as separate columns and never enters the anonymous payload that goes
/// to the model: "who" comes from the session, "what" from the prompt.
///
/// One scope can make several AI calls (an agent's tool loop); <see cref="CorrelationId"/> groups the
/// rows of one logical operation, including its fallback attempts. <see cref="BeginOperation"/> renews it.
/// </summary>
public sealed class AiTraceContext
{
    /// <summary>Ownership scope of the call (tenant, account, project), set by the host from its session.</summary>
    public string? ScopeId { get; set; }

    public string? UserId { get; set; }

    public string? ConversationId { get; set; }

    /// <summary>Where the call comes from ("research", "support-chat"...).</summary>
    public string? Surface { get; set; }

    /// <summary>Host-specific extra columns (device id, session id...).</summary>
    public Dictionary<string, string> Properties { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Guid? CorrelationId { get; private set; }

    /// <summary>Starts a new logical AI operation with a fresh correlation id; fallback attempts share it.</summary>
    public Guid BeginOperation()
    {
        var id = Guid.NewGuid();
        CorrelationId = id;
        return id;
    }
}
