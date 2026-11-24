using System.Text.Json;
using Microsoft.Extensions.Options;

namespace ResearchAgent.Agents;

/// <summary>
/// Runs the tool-calling (agentic) loop: pick a provider (policy order, with fallback) →
/// <see cref="IAiProviderAdapter.CompleteChatAsync"/> → when the model calls tools, dispatch them and
/// append their results → call again → final text, or stop at the iteration limit.
/// Metering and access checks are not done here: the calling service does them. Identity never enters
/// the runner; the tool dispatcher reads it from the session.
/// </summary>
public interface IAiAgentRunner
{
    Task<AiAgentResult> RunAsync(
        string taskType,
        string systemPrompt,
        IReadOnlyList<AiChatMessage> conversation,
        IReadOnlyList<AiToolDefinition> tools,
        Func<string, JsonElement, CancellationToken, Task<string>> dispatch,
        CancellationToken cancellationToken = default);
}

public sealed class AiAgentResult
{
    public required string Text { get; init; }
    public required AiProvider Provider { get; init; }
    public required string Model { get; init; }
    public required AiUsage Usage { get; init; }

    /// <summary>
    /// Name of the loop-ending tool when one was called (<see cref="AiToolDefinition.EndsLoop"/>, a hand-off);
    /// otherwise null. When set, <see cref="Text"/> is not the answer for the user: the caller hands the
    /// work over to someone else, for example another assistant.
    /// </summary>
    public string? EndedByTool { get; init; }
}

public sealed class AiAgentRunner : IAiAgentRunner
{
    private const int MaxIterations = 8;   // safety ceiling: cuts an endless tool loop

    private readonly IEnumerable<IAiProviderAdapter> _adapters;
    private readonly AiOptions _options;
    private readonly ILogger<AiAgentRunner> _logger;

    public AiAgentRunner(IEnumerable<IAiProviderAdapter> adapters, IOptions<AiOptions> options, ILogger<AiAgentRunner> logger)
    {
        _adapters = adapters;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<AiAgentResult> RunAsync(
        string taskType,
        string systemPrompt,
        IReadOnlyList<AiChatMessage> conversation,
        IReadOnlyList<AiToolDefinition> tools,
        Func<string, JsonElement, CancellationToken, Task<string>> dispatch,
        CancellationToken cancellationToken = default)
    {
        var policy = _options.GetPolicy(taskType);

        // Working message list: system + conversation; tool turns are appended here.
        var messages = new List<AiChatMessage>(conversation.Count + 1)
        {
            new() { Role = "system", Content = systemPrompt },
        };
        messages.AddRange(conversation);

        int totalIn = 0, totalOut = 0, totalCached = 0;
        AiProvider provider = default;
        var model = string.Empty;

        // Last link of a Responses API chain. The message list is still kept complete: without a chain
        // (chat/completions path, a response without an id) it is the only full history we have.
        string? previousResponseId = null;

        for (var iteration = 0; iteration < MaxIterations; iteration++)
        {
            var req = new AiChatRequest
            {
                TaskType = taskType,
                Messages = messages,
                Tools = tools.Count > 0 ? tools : null,
                MaxOutputTokens = policy.MaxOutputTokens,
                Model = policy.Model,
                ReasoningEffort = policy.ReasoningEffort,
                UseResponsesApi = policy.UseResponsesApi,
                PreviousResponseId = previousResponseId,
            };

            var result = await CallWithFallbackAsync(policy, req, cancellationToken);
            totalIn += result.Usage.InputTokens;
            totalOut += result.Usage.OutputTokens;
            totalCached += result.Usage.CachedInputTokens;
            provider = result.Provider;
            model = result.Model;

            // Null after a chat/completions turn: the next turn then sends the full list.
            previousResponseId = result.ResponseId;

            // No tool call: this is the final answer.
            if (result.ToolCalls.Count == 0)
                return Done(result.Text ?? string.Empty, provider, model, totalIn, totalOut, totalCached);

            // A loop-ending tool (hand-off): no dispatch and no further turn; other calls of the same turn
            // do not run either, because someone else takes the work over. This turn's cost still counts.
            var ending = result.ToolCalls.FirstOrDefault(c => tools.Any(t => t.EndsLoop && t.Name == c.Name));
            if (ending is not null)
                return Done(result.Text ?? string.Empty, provider, model, totalIn, totalOut, totalCached, ending.Name);

            // Append the assistant turn (with its tool calls) and one tool message per result.
            messages.Add(new AiChatMessage { Role = "assistant", Content = result.Text, ToolCalls = result.ToolCalls });
            foreach (var call in result.ToolCalls)
            {
                string toolResult;
                try
                {
                    toolResult = await dispatch(call.Name, ParseArgs(call.ArgumentsJson), cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Agent tool '{Tool}' failed.", call.Name);
                    toolResult = JsonSerializer.Serialize(new { error = "tool failed" });   // let the model recover
                }

                messages.Add(new AiChatMessage { Role = "tool", ToolCallId = call.Id, Content = toolResult });
            }
        }

        _logger.LogWarning("AiAgentRunner hit the iteration limit ({Max}) (task={Task}).", MaxIterations, taskType);
        return Done(
            "I could not finish this request. Please try again or ask a person for help.",
            provider, model, totalIn, totalOut, totalCached);
    }

    private static AiAgentResult Done(
        string text, AiProvider provider, string model, int inTok, int outTok, int cachedTok, string? endedByTool = null) => new()
    {
        Text = text,
        EndedByTool = endedByTool,
        Provider = provider,
        Model = model,
        Usage = new AiUsage { InputTokens = inTok, OutputTokens = outTok, CachedInputTokens = cachedTok },
    };

    private async Task<AiChatResult> CallWithFallbackAsync(AiTaskPolicy policy, AiChatRequest req, CancellationToken ct)
    {
        Exception? lastError = null;
        foreach (var providerType in policy.Providers)
        {
            var adapter = _adapters.FirstOrDefault(a => a.Provider == providerType && a.IsConfigured);
            if (adapter is null) continue;

            try { return await adapter.CompleteChatAsync(req, ct); }
            catch (Exception ex) { lastError = ex; _logger.LogWarning(ex, "Agent provider {Provider} failed; falling back.", providerType); }
        }

        throw new AiCompletionFailedException("The AI service is temporarily unavailable. Please try again shortly.", lastError);
    }

    private static JsonElement ParseArgs(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) argumentsJson = "{}";
        try { return JsonSerializer.Deserialize<JsonElement>(argumentsJson); }
        catch { return JsonSerializer.Deserialize<JsonElement>("{}"); }
    }
}
