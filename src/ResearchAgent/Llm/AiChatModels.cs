namespace WebApi.Services.Ai;

// ── Tool-calling (agentic) sohbet modelleri ──
// Mevcut single-shot AiCompletionRequest/Result'tan AYRI; çok-turlu + araç (function) destekler.
// KİMLİK İÇERMEZ (single-shot ile aynı güvenlik invaryantı: "ne" prompt'tan, "kim" oturumdan).

/// <summary>Tool-calling sohbetinde tek bir mesaj (system/user/assistant/tool).</summary>
public sealed class AiChatMessage
{
    /// <summary>"system" | "user" | "assistant" | "tool".</summary>
    public required string Role { get; init; }

    /// <summary>Metin içerik (assistant araç çağırıyorsa null olabilir).</summary>
    public string? Content { get; init; }

    /// <summary>assistant'ın araç çağrı talepleri (varsa).</summary>
    public IReadOnlyList<AiToolCall>? ToolCalls { get; init; }

    /// <summary>role="tool" için: hangi araç çağrısına (Id) yanıt olduğu.</summary>
    public string? ToolCallId { get; init; }
}

/// <summary>Modelin bir aracı (function) çağırma talebi.</summary>
public sealed class AiToolCall
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>Modelin verdiği argümanlar (ham JSON string).</summary>
    public required string ArgumentsJson { get; init; }
}

/// <summary>Modele sunulan araç tanımı (function schema).</summary>
public sealed class AiToolDefinition
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    /// <summary>JSON Schema (object). Parametresiz araç: {"type":"object","properties":{}}.</summary>
    public required string ParametersJsonSchema { get; init; }
}

/// <summary>Tool-calling sohbet isteği (multi-turn + araçlar). KİMLİK YOK.</summary>
public sealed class AiChatRequest
{
    public required AiTaskType TaskType { get; init; }
    public required IReadOnlyList<AiChatMessage> Messages { get; init; }
    public IReadOnlyList<AiToolDefinition>? Tools { get; init; }
    public int? MaxOutputTokens { get; init; }
}

/// <summary>Tool-calling sonucu: ya final <see cref="Text"/>, ya da <see cref="ToolCalls"/> (araç talebi).</summary>
public sealed class AiChatResult
{
    public string? Text { get; init; }
    public IReadOnlyList<AiToolCall> ToolCalls { get; init; } = Array.Empty<AiToolCall>();
    public required AiProvider Provider { get; init; }
    public required string Model { get; init; }
    public required AiUsage Usage { get; init; }
}
