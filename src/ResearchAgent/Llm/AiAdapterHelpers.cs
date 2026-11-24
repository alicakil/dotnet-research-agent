using System.Linq;
using System.Text;
using System.Text.Json;

namespace ResearchAgent.Llm;

/// <summary>Small helpers shared between the provider adapters (avoids duplicated code).</summary>
internal static class AiAdapterHelpers
{
    /// <summary>
    /// The `reasoning_effort` sent when the policy does not specify one. `minimal` CANNOT be sent: it was
    /// valid only for the gpt-5 / gpt-5-mini generation; the gpt-5.4+, gpt-5.6 and gpt-6 generations
    /// (sol/terra/luna) reject it —
    /// *"Unsupported value: 'reasoning_effort' does not support 'minimal' with this model.
    /// Supported values are: 'none', 'low', 'medium', 'high', 'xhigh'"* → OpenAI **400**. gpt-5 and
    /// gpt-5-mini are retired on 11 Dec 2026, so the `minimal` branch reserved for them was removed.
    ///
    /// With a fallback provider configured this was a SILENT failure: the router took the 400 for a reason
    /// to fall back, the fallback answered, and nobody noticed — the pinned expensive model was never
    /// actually used. (This exact thing happened: when gpt-5.6-terra was pinned, every chat fell through
    /// to the fallback's model; an eval caught it.) Without a fallback provider a wrong value fails the
    /// call outright.
    ///
    /// `low` is chosen, not `none`: a little reasoning helps answer quality, and the "speed + low cost"
    /// that the old `minimal` was meant for is largely kept with `low`.
    ///
    /// ONE EXCEPTION — <paramref name="hasTools"/>: the new generation does not accept reasoning together
    /// with function tools on `/v1/chat/completions`:
    /// *"Function tools with reasoning_effort are not supported for gpt-5.6-terra in
    /// /v1/chat/completions. To use function tools, use /v1/responses or set reasoning_effort
    /// to 'none'."* → the tool-calling path sends `none`. (The alternative, `/v1/responses`, is a separate
    /// API schema; it is offered as an opt-in path instead, see <see cref="AiChatRequest.UseResponsesApi"/>.)
    /// </summary>
    public static string ResolveReasoningEffort(bool hasTools = false) => hasTools ? "none" : "low";

    /// <summary>
    /// Builds the effective system prompt. When <see cref="AiCompletionRequest.ResponseJsonSchema"/> is set,
    /// it appends the "return only JSON that matches this schema" instruction, whatever the provider; the
    /// adapter also turns on the provider's JSON mode. Without a schema the system prompt is returned as is.
    /// </summary>
    public static string ComposeSystemPrompt(AiCompletionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ResponseJsonSchema))
            return request.SystemPrompt ?? string.Empty;

        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
            sb.AppendLine(request.SystemPrompt).AppendLine();
        sb.AppendLine("Respond with a single valid JSON object that matches the JSON schema below. Do not add any explanation, text or markdown outside the JSON:");
        sb.Append(request.ResponseJsonSchema);
        return sb.ToString();
    }

    /// <summary>Shortens the raw response body in error logs.</summary>
    public static string Truncate(string value, int max = 500) =>
        value.Length <= max ? value : value[..max];

    // ── Shared helpers for tool calling (OpenAI-compatible) ──

    /// <summary>Converts a list of <see cref="AiChatMessage"/> into an OpenAI chat-completions message array.</summary>
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

    /// <summary>Converts tool definitions into the OpenAI <c>tools</c> array (the parameter schema is embedded as JSON).</summary>
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

    /// <summary>Parses an OpenAI-compatible response: the final text OR tool_calls, plus usage.</summary>
    public static AiChatResult ParseChatResult(string json, AiProvider provider, string model)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            throw new HttpRequestException($"{provider} returned an empty response: {Truncate(json)}");

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
    /// The OpenAI-compatible <c>usage</c> block. Chat Completions: <c>prompt_tokens</c> +
    /// <c>prompt_tokens_details.cached_tokens</c>; Responses: <c>input_tokens</c> +
    /// <c>input_tokens_details.cached_tokens</c>. A missing field counts as 0.
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
