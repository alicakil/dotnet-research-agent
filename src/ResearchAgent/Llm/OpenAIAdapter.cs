using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace ResearchAgent.Llm;

/// <summary>
/// OpenAI (Chat Completions) adapter — the primary (paid) provider. The default model comes from
/// <see cref="AiOptions"/> (<c>OpenAI.Model</c>). The output ceiling is sent as <c>max_completion_tokens</c>
/// (GPT-5 / o-series reject max_tokens). The API key comes from configuration or the environment
/// (<c>Ai:OpenAI:ApiKey</c> / <c>OPENAI_API_KEY</c>, for example loaded from AWS SSM into the environment).
/// Without a key IsConfigured=false → <see cref="AiRouter"/> skips the provider; if the policy lists no other
/// provider, the call then fails.
/// </summary>
public sealed class OpenAIAdapter : IAiProviderAdapter
{
    private const string Endpoint = "https://api.openai.com/v1/chat/completions";
    /// <summary>The path for surfaces that use tools together with reasoning; selected in the policy with <c>UseResponsesApi</c>.</summary>
    private const string ResponsesEndpoint = "https://api.openai.com/v1/responses";

    private readonly HttpClient _httpClient;
    private readonly AiOptions _options;
    private readonly ILogger<OpenAIAdapter> _logger;
    private readonly string? _apiKey;

    public OpenAIAdapter(
        HttpClient httpClient,
        IOptions<AiOptions> options,
        IConfiguration configuration,
        ILogger<OpenAIAdapter> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
        _apiKey = configuration["Ai:OpenAI:ApiKey"]
            ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
    }

    public AiProvider Provider => AiProvider.OpenAI;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);

    /// <summary>The gpt-5 family is multimodal: images are added to the message content as `image_url` parts.</summary>
    public bool SupportsImages => true;

    public async Task<AiCompletionResult> CompleteAsync(AiCompletionRequest request, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("OpenAI API key is not configured (Ai:OpenAI:ApiKey / OPENAI_API_KEY).");

        // The policy model (if any) also applies on the single-shot path; otherwise the provider default. A
        // service that calls the adapter directly must carry the model itself (one such caller forgot it once).
        var model = request.Model ?? _options.OpenAI.Model;
        var systemText = AiAdapterHelpers.ComposeSystemPrompt(request);

        var messages = new List<object>();
        if (!string.IsNullOrWhiteSpace(systemText))
            messages.Add(new { role = "system", content = systemText });

        if (request.Images is { Count: > 0 })
        {
            // With images, the user content becomes an ARRAY: the text first, then the images.
            // detail "high": the images are not readable at low resolution, and the job costs only a few
            // cents per image anyway.
            var parts = new List<object> { new { type = "text", text = request.UserPrompt } };
            parts.AddRange(request.Images.Select(image => (object)new
            {
                type = "image_url",
                image_url = new { url = image, detail = "high" }
            }));
            messages.Add(new { role = "user", content = parts });
        }
        else
        {
            messages.Add(new { role = "user", content = request.UserPrompt });
        }

        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = messages,
            // Keep reasoning low → fast (under the 25 s timeout and, in production, the 30 s API Gateway limit)
            // and cheap; grounded work does not need reasoning. When the policy does not say, `low`: `minimal`
            // returns 400 on current models (see the helper).
            ["reasoning_effort"] = request.ReasoningEffort ?? AiAdapterHelpers.ResolveReasoningEffort(),
        };
        if (request.MaxOutputTokens is int maxTokens)
            body["max_completion_tokens"] = maxTokens;   // GPT-5 / o-series: NOT max_tokens
        if (!string.IsNullOrWhiteSpace(request.ResponseJsonSchema))
            body["response_format"] = new { type = "json_object" };   // the prompt must contain "JSON" (an OpenAI requirement)

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey!); // non-null is guaranteed by the IsConfigured guard
        httpRequest.Content = new StringContent(
            JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("OpenAI call failed ({Status}): {Body}",
                (int)response.StatusCode, AiAdapterHelpers.Truncate(json));
            throw new HttpRequestException($"OpenAI {(int)response.StatusCode}: {AiAdapterHelpers.Truncate(json)}");
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            throw new HttpRequestException($"OpenAI returned an empty response: {AiAdapterHelpers.Truncate(json)}");

        var text = string.Empty;
        if (choices[0].TryGetProperty("message", out var message) &&
            message.TryGetProperty("content", out var contentEl))
        {
            text = contentEl.GetString() ?? string.Empty;
        }

        // Reasoning tokens are spent from the `max_completion_tokens` ceiling. If the effort is raised
        // without raising the ceiling, the model spends the whole budget on thinking and the content comes
        // back EMPTY — HTTP 200, finish_reason "length", while the caller only reports "could not read the
        // output" and the cause stays invisible.
        var finishReason = choices[0].TryGetProperty("finish_reason", out var fr) ? fr.GetString() : null;
        if (string.IsNullOrEmpty(text) && finishReason == "length")
        {
            throw new HttpRequestException(
                $"OpenAI hit the output token ceiling and produced no content (model={model}, " +
                $"ceiling={request.MaxOutputTokens}, effort={request.ReasoningEffort ?? "default"}). " +
                "Reasoning may have consumed the budget — raise MaxOutputTokens or lower the reasoning effort.");
        }

        return new AiCompletionResult
        {
            Text = text,
            Provider = AiProvider.OpenAI,
            Model = model,
            Usage = AiAdapterHelpers.ParseUsage(root, "prompt_tokens", "completion_tokens", "prompt_tokens_details"),
        };
    }

    /// <summary>Multi-turn chat with tool calling (for example a support chatbot). Same endpoint and schema; tools + tool_calls are added.</summary>
    public async Task<AiChatResult> CompleteChatAsync(AiChatRequest request, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("OpenAI API key is not configured (Ai:OpenAI:ApiKey / OPENAI_API_KEY).");

        if (request.UseResponsesApi)
            return await CompleteViaResponsesAsync(request, cancellationToken);

        var model = request.Model ?? _options.OpenAI.Model;   // policy override (e.g. gpt-5 for one task)
        var hasTools = request.Tools is { Count: > 0 };
        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = AiAdapterHelpers.BuildChatMessages(request.Messages),
            // The policy override wins (for example a public chat may ask for "medium"); otherwise the
            // model-dependent default. The presence of tools affects the value — the new generation does
            // not accept reasoning together with tools (see the helper), so the override is ignored when
            // tools are present.
            ["reasoning_effort"] = hasTools
                ? AiAdapterHelpers.ResolveReasoningEffort(hasTools: true)
                : request.ReasoningEffort ?? AiAdapterHelpers.ResolveReasoningEffort(),
        };
        if (request.MaxOutputTokens is int maxTokens)
            body["max_completion_tokens"] = maxTokens;   // GPT-5 / o-series: NOT max_tokens
        if (hasTools)
        {
            body["tools"] = AiAdapterHelpers.BuildToolsArray(request.Tools!);
            body["tool_choice"] = "auto";
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey!);
        httpRequest.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("OpenAI chat call failed ({Status}): {Body}", (int)response.StatusCode, AiAdapterHelpers.Truncate(json));
            throw new HttpRequestException($"OpenAI {(int)response.StatusCode}: {AiAdapterHelpers.Truncate(json)}");
        }
        return AiAdapterHelpers.ParseChatResult(json, AiProvider.OpenAI, model);
    }

    /// <summary>
    /// The same contract, running over <c>/v1/responses</c>. The caller (the agent runner) does not know about
    /// this path — it passes a message list and gets a result back; the only difference is the returned
    /// <see cref="AiChatResult.ResponseId"/>.
    ///
    /// Two gains: (1) reasoning can be sent together with tools — on chat/completions gpt-5.6 rejects that
    /// with a 400; (2) with <c>previous_response_id</c> the history stays at OpenAI, so the model does not
    /// rebuild its own plan between tool turns.
    ///
    /// ⚠️ The chain carries the HISTORY but NOT <c>instructions</c> — the system prompt is sent again on
    /// every turn (see below, with the reason).
    ///
    /// ⚠️ The chain depends on <c>store</c>: with <c>store:false</c> the second turn fails with 400 "not found"
    /// (measured). So while the chain is on, the request and response are kept at OpenAI for 30 days — a
    /// deliberate decision; they can be deleted early with <c>DELETE /v1/responses/{id}</c> if needed.
    /// </summary>
    private async Task<AiChatResult> CompleteViaResponsesAsync(AiChatRequest request, CancellationToken cancellationToken)
    {
        var model = request.Model ?? _options.OpenAI.Model;
        var hasTools = request.Tools is { Count: > 0 };

        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            // With a chain ONLY the new items go (the tool results); otherwise the whole conversation.
            ["input"] = BuildResponsesInput(request),
        };

        // The system prompt is a separate field in Responses — and it is sent again on EVERY turn of the chain.
        // ⚠️ Counter-intuitive, but this is OpenAI's contract: `instructions` are NOT inherited through
        // `previous_response_id` (deliberately, so that you can change the system message between turns).
        // This used to be skipped here on the assumption that "the previous response already carries it", and
        // the result was that the answer after a tool turn was produced WITHOUT any rules. Measured on
        // 29 Aug 2026 in an eval of a support chat — the input of the 2nd turn drops from 6,077 to 460 tokens
        // (no prompt) and in exactly that turn the model asks for a screenshot, drops the action marker the
        // prompt requires and describes the screen; that is, all three behaviours the prompt forbids come
        // back at once. Repeating the prompt costs almost nothing: it is the same prefix, so it is served
        // from the cache.
        var system = request.Messages.FirstOrDefault(m => m.Role == "system")?.Content;
        if (!string.IsNullOrWhiteSpace(system)) body["instructions"] = system;

        if (request.PreviousResponseId is not null)
            body["previous_response_id"] = request.PreviousResponseId;

        if (request.MaxOutputTokens is int maxTokens) body["max_output_tokens"] = maxTokens;
        // The value is sent AS IS: the whole point of this API is that it allows reasoning together with
        // tools. "minimal" is valid only on the old generation and is not used here.
        if (!string.IsNullOrWhiteSpace(request.ReasoningEffort))
            body["reasoning"] = new { effort = request.ReasoningEffort };
        if (hasTools)
        {
            body["tools"] = BuildResponsesTools(request.Tools!);
            body["tool_choice"] = "auto";
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, ResponsesEndpoint);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey!);
        httpRequest.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("OpenAI responses call failed ({Status}): {Body}", (int)response.StatusCode, AiAdapterHelpers.Truncate(json));
            throw new HttpRequestException($"OpenAI {(int)response.StatusCode}: {AiAdapterHelpers.Truncate(json)}");
        }
        return ParseResponsesResult(json, model);
    }

    /// <summary>A tool definition in the Responses schema: the function fields are flat, not nested.</summary>
    private static List<object> BuildResponsesTools(IReadOnlyList<AiToolDefinition> tools) =>
        tools.Select(t => (object)new
        {
            type = "function",
            name = t.Name,
            description = t.Description,
            parameters = JsonSerializer.Deserialize<JsonElement>(t.ParametersJsonSchema),
        }).ToList();

    /// <summary>
    /// Message list → Responses "input" items. With a chain only the tool results added in the last turn
    /// are sent; sending the history again would both waste tokens and tell the model "the same thing
    /// twice".
    /// </summary>
    private static List<object> BuildResponsesInput(AiChatRequest request)
    {
        var items = new List<object>();

        if (request.PreviousResponseId is not null)
        {
            foreach (var m in request.Messages.Where(m => m.Role == "tool").TakeLast(CountTrailingToolMessages(request.Messages)))
                items.Add(new { type = "function_call_output", call_id = m.ToolCallId, output = m.Content ?? string.Empty });
            return items;
        }

        foreach (var m in request.Messages)
        {
            switch (m.Role)
            {
                case "system":
                    break;   // it went into the instructions field
                case "tool":
                    items.Add(new { type = "function_call_output", call_id = m.ToolCallId, output = m.Content ?? string.Empty });
                    break;
                case "assistant" when m.ToolCalls is { Count: > 0 }:
                    // Replay without a chain (when there is no id for the previous turn): the model's past tool
                    // requests have to be written out as items too, otherwise the results are left orphaned.
                    foreach (var call in m.ToolCalls)
                        items.Add(new { type = "function_call", call_id = call.Id, name = call.Name, arguments = call.ArgumentsJson });
                    if (!string.IsNullOrWhiteSpace(m.Content))
                        items.Add(new { role = "assistant", content = m.Content });
                    break;
                default:
                    if (!string.IsNullOrWhiteSpace(m.Content))
                        items.Add(new { role = m.Role, content = m.Content });
                    break;
            }
        }
        return items;
    }

    /// <summary>Number of uninterrupted tool messages at the end of the list = the results added in this turn.</summary>
    private static int CountTrailingToolMessages(IReadOnlyList<AiChatMessage> messages)
    {
        var n = 0;
        for (var i = messages.Count - 1; i >= 0 && messages[i].Role == "tool"; i--) n++;
        return n;
    }

    /// <summary>A Responses reply: the output array (reasoning · function_call · message) + usage.</summary>
    private static AiChatResult ParseResponsesResult(string json, string model)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var text = new StringBuilder();
        var toolCalls = new List<AiToolCall>();

        if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in output.EnumerateArray())
            {
                var type = item.TryGetProperty("type", out var t) ? t.GetString() : null;
                switch (type)
                {
                    case "function_call":
                        toolCalls.Add(new AiToolCall
                        {
                            Id = item.TryGetProperty("call_id", out var cid) ? cid.GetString() ?? string.Empty : string.Empty,
                            Name = item.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty,
                            ArgumentsJson = item.TryGetProperty("arguments", out var a) ? a.GetString() ?? "{}" : "{}",
                        });
                        break;
                    case "message":
                        if (item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                            foreach (var part in content.EnumerateArray())
                                if (part.TryGetProperty("text", out var pt)) text.Append(pt.GetString());
                        break;
                    // The "reasoning" item is deliberately not read: its content is not ours, it is carried
                    // on the OpenAI side in the chain. We would not want to log it either.
                }
            }
        }


        return new AiChatResult
        {
            Text = text.Length > 0 ? text.ToString() : null,
            ToolCalls = toolCalls,
            Provider = AiProvider.OpenAI,
            Model = model,
            Usage = AiAdapterHelpers.ParseUsage(root, "input_tokens", "output_tokens", "input_tokens_details"),
            ResponseId = root.TryGetProperty("id", out var id) ? id.GetString() : null,
        };
    }
}
