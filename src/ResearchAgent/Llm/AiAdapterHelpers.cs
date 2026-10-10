using System.Linq;
using System.Text;
using System.Text.Json;

namespace WebApi.Services.Ai;

/// <summary>Sağlayıcı adapter'ları arasında paylaşılan küçük yardımcılar (kod tekrarını önler).</summary>
internal static class AiAdapterHelpers
{
    /// <summary>
    /// Modele göre `reasoning_effort` değeri. SABİT DEĞER GÖNDERİLEMEZ: `minimal` yalnızca gpt-5 /
    /// gpt-5-mini kuşağında geçerli; gpt-5.4+ ve gpt-5.6 (sol/terra/luna) onu reddeder —
    /// *"Unsupported value: 'reasoning_effort' does not support 'minimal' with this model.
    /// Supported values are: 'none', 'low', 'medium', 'high', 'xhigh'"* → OpenAI **400**.
    ///
    /// Yedek sağlayıcı varken bu SESSİZ bir arızaydı: router 400'ü fallback sanıp yedeğe düşüyor,
    /// yanıt geliyor, kimse fark etmiyordu — pinlenen pahalı model hiç kullanılmamış oluyordu.
    /// (Tam olarak bu yaşandı: gpt-5.6-terra pinlendiğinde her faturalama sohbeti yedeğin modeline
    /// düştü; eval yakaladı.) Bugün yedek yok: yanlış değer çağrıyı doğrudan düşürür.
    ///
    /// Yeni kuşakta `low` seçiliyor, `none` değil: bir tık akıl yürütme yanıt kalitesine yarıyor,
    /// `minimal`in amacı olan "hız + ucuzluk" ise `low` ile büyük ölçüde korunuyor.
    ///
    /// TEK İSTİSNA — <paramref name="hasTools"/>: yeni kuşak, function tool'larla birlikte
    /// `/v1/chat/completions` üzerinde reasoning KABUL ETMİYOR:
    /// *"Function tools with reasoning_effort are not supported for gpt-5.6-terra in
    /// /v1/chat/completions. To use function tools, use /v1/responses or set reasoning_effort
    /// to 'none'."* → tool-calling yolunda `none` gönderilir. (Alternatif `/v1/responses`'a
    /// taşınmaktı; ayrı bir API şeması demek — kazancı yokken yapılmadı.)
    /// </summary>
    public static string ResolveReasoningEffort(string? model, bool hasTools = false)
    {
        // gpt-5 ve gpt-5-mini (2025 kuşağı) `minimal` destekler; 5.1+ / 5.4+ / 5.6 desteklemez.
        var supportsMinimal = string.IsNullOrEmpty(model)
            || model.StartsWith("gpt-5-", StringComparison.OrdinalIgnoreCase)
            || model.Equals("gpt-5", StringComparison.OrdinalIgnoreCase);
        if (supportsMinimal) return "minimal";
        return hasTools ? "none" : "low";
    }

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

    // ── Tool-calling (OpenAI-uyumlu) ortak yardımcıları ──

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

        return new AiChatResult
        {
            Text = text,
            ToolCalls = toolCalls,
            Provider = provider,
            Model = model,
            Usage = ParseUsage(root, "prompt_tokens", "completion_tokens", "prompt_tokens_details"),
        };
    }

    /// <summary>
    /// OpenAI-uyumlu <c>usage</c> bloğu. Chat Completions: <c>prompt_tokens</c> +
    /// <c>prompt_tokens_details.cached_tokens</c>; Responses: <c>input_tokens</c> +
    /// <c>input_tokens_details.cached_tokens</c>. Alan yoksa 0.
    /// </summary>
    public static AiUsage ParseUsage(JsonElement root, string inputProp, string outputProp, string detailsProp)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return new AiUsage();

        int Read(JsonElement el, string prop) =>
            el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

        var cached = usage.TryGetProperty(detailsProp, out var details) && details.ValueKind == JsonValueKind.Object
            ? Read(details, "cached_tokens")
            : 0;

        return new AiUsage
        {
            InputTokens = Read(usage, inputProp),
            OutputTokens = Read(usage, outputProp),
            CachedInputTokens = cached,
        };
    }
}
