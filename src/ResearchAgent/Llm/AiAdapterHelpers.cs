using System.Linq;
using System.Text;
using System.Text.Json;

namespace WebApi.Services.Ai;

/// <summary>Sağlayıcı adapter'ları arasında paylaşılan küçük yardımcılar (kod tekrarını önler).</summary>
internal static class AiAdapterHelpers
{
    /// <summary>
    /// Etkin sistem promptunu oluşturur. <see cref="AiCompletionRequest.ResponseJsonSchema"/> doluysa,
    /// sağlayıcıdan bağımsız olarak "yalnızca bu şemaya uygun JSON döndür" talimatını ekler — adapter
    /// ayrıca sağlayıcının JSON modunu da açar. Şema yoksa sistem promptu olduğu gibi döner.
    /// </summary>
    public static string ComposeSystemPrompt(AiCompletionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ResponseJsonSchema))
            return request.SystemPrompt ?? string.Empty;

        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
            sb.AppendLine(request.SystemPrompt).AppendLine();
        sb.AppendLine("Yalnızca aşağıdaki JSON şemasına uygun, geçerli tek bir JSON nesnesiyle yanıt ver. Şema dışında hiçbir açıklama, metin veya markdown ekleme:");
        sb.Append(request.ResponseJsonSchema);
        return sb.ToString();
    }

    /// <summary>Hata loglarında ham gövdeyi kısaltır.</summary>
    public static string Truncate(string value, int max = 500) =>
        value.Length <= max ? value : value[..max];

    // ── Tool-calling (OpenAI-uyumlu) ortak yardımcıları (OpenAIAdapter + GroqAdapter paylaşır) ──

    /// <summary><see cref="AiChatMessage"/> listesini OpenAI chat-completions mesaj dizisine çevirir.</summary>
    public static List<object> BuildChatMessages(IReadOnlyList<AiChatMessage> messages)
    {
        var result = new List<object>(messages.Count);
        foreach (var m in messages)
        {
            if (m.Role == "tool")
                result.Add(new { role = "tool", tool_call_id = m.ToolCallId, content = m.Content ?? string.Empty });
            else if (m.ToolCalls is { Count: > 0 })
                result.Add(new
                {
                    role = m.Role,
                    content = m.Content,
                    tool_calls = m.ToolCalls.Select(tc => new
                    {
                        id = tc.Id,
                        type = "function",
                        function = new { name = tc.Name, arguments = tc.ArgumentsJson },
                    }).ToList(),
                });
            else
                result.Add(new { role = m.Role, content = m.Content ?? string.Empty });
        }
        return result;
    }

    /// <summary>Araç tanımlarını OpenAI <c>tools</c> dizisine çevirir (parametre şeması JSON olarak gömülür).</summary>
    public static List<object> BuildToolsArray(IReadOnlyList<AiToolDefinition> tools) =>
        tools.Select(t => (object)new
        {
            type = "function",
            function = new
            {
                name = t.Name,
                description = t.Description,
                parameters = JsonSerializer.Deserialize<JsonElement>(t.ParametersJsonSchema),
            },
        }).ToList();

    /// <summary>OpenAI-uyumlu yanıtı ayrıştırır: final metin VEYA tool_calls + usage.</summary>
    public static AiChatResult ParseChatResult(string json, AiProvider provider, string model)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            throw new HttpRequestException($"{provider} boş yanıt döndürdü: {Truncate(json)}");

        string? text = null;
        var toolCalls = new List<AiToolCall>();
        if (choices[0].TryGetProperty("message", out var message))
        {
            if (message.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String)
                text = contentEl.GetString();
            if (message.TryGetProperty("tool_calls", out var tcEl) && tcEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var tc in tcEl.EnumerateArray())
                {
                    if (!tc.TryGetProperty("function", out var fn)) continue;
                    toolCalls.Add(new AiToolCall
                    {
                        Id = tc.TryGetProperty("id", out var id) ? (id.GetString() ?? string.Empty) : string.Empty,
                        Name = fn.TryGetProperty("name", out var n) ? (n.GetString() ?? string.Empty) : string.Empty,
                        ArgumentsJson = fn.TryGetProperty("arguments", out var a) ? (a.GetString() ?? "{}") : "{}",
                    });
                }
            }
        }

        int inputTokens = 0, outputTokens = 0;
        if (root.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("prompt_tokens", out var p)) inputTokens = p.GetInt32();
            if (usage.TryGetProperty("completion_tokens", out var c)) outputTokens = c.GetInt32();
        }

        return new AiChatResult
        {
            Text = text,
            ToolCalls = toolCalls,
            Provider = provider,
            Model = model,
            Usage = new AiUsage { InputTokens = inputTokens, OutputTokens = outputTokens },
        };
    }
}
