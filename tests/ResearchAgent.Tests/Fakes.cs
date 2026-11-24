namespace ResearchAgent.Tests;

/// <summary>A provider adapter whose answers are scripted per call.</summary>
internal sealed class ScriptedAdapter(AiProvider provider, Func<AiCompletionRequest, string>? complete = null,
    Func<AiChatRequest, AiChatResult>? chat = null, bool images = false) : IAiProviderAdapter
{
    public List<AiCompletionRequest> Requests { get; } = [];
    public List<AiChatRequest> ChatRequests { get; } = [];

    public AiProvider Provider => provider;
    public bool IsConfigured => true;
    public bool SupportsImages => images;

    public Task<AiCompletionResult> CompleteAsync(AiCompletionRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        var text = (complete ?? (_ => "ok"))(request);   // may throw: that is how a provider failure is scripted
        return Task.FromResult(new AiCompletionResult
        {
            Text = text,
            Provider = provider,
            Model = $"{provider}-model",
            Usage = new AiUsage { InputTokens = 10, OutputTokens = 5 },
        });
    }

    public Task<AiChatResult> CompleteChatAsync(AiChatRequest request, CancellationToken cancellationToken = default)
    {
        // Keep a copy: the runner keeps appending to the same message list.
        ChatRequests.Add(new AiChatRequest
        {
            TaskType = request.TaskType, Messages = request.Messages.ToList(), Tools = request.Tools,
        });
        return Task.FromResult(chat!(request));
    }
}

/// <summary>An <see cref="IAiCompletionService"/> that answers by task type.</summary>
internal sealed class ScriptedAi(Func<AiCompletionRequest, string> answer) : IAiCompletionService
{
    public List<AiCompletionRequest> Requests { get; } = [];

    public Task<AiCompletionResult> CompleteAsync(AiCompletionRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        return Task.FromResult(new AiCompletionResult
        {
            Text = answer(request),
            Provider = AiProvider.OpenAI,
            Model = "fake",
            Usage = new AiUsage(),
        });
    }
}

internal sealed class FakeSearch(Func<string, IReadOnlyList<WebSearchResult>> results) : IWebSearchProvider
{
    public List<string> Queries { get; } = [];

    public bool IsConfigured => true;

    public Task<WebSearchOutcome> SearchAsync(string query, int maxResults, CancellationToken ct = default)
    {
        lock (Queries) Queries.Add(query);
        return Task.FromResult(WebSearchOutcome.Ok(results(query)));
    }
}

internal sealed class FakeFetcher(Dictionary<string, string> pages) : IWebPageFetcher
{
    public Task<FetchedPage> FetchAsync(string url, CancellationToken ct = default) =>
        Task.FromResult(pages.TryGetValue(url, out var text)
            ? new FetchedPage(url, true, text, null)
            : new FetchedPage(url, false, null, "http_404"));
}

internal sealed class RecordingMeter : IAiUsageMeter
{
    public int Successes { get; private set; }
    public int Failures { get; private set; }
    public int RemainingCredits { get; set; } = 100;

    public Task EnsureCanSpendAsync(string? scopeId, int cost, CancellationToken cancellationToken = default) =>
        cost > RemainingCredits ? throw new AiQuotaExceededException(cost, RemainingCredits) : Task.CompletedTask;

    public Task RecordSuccessAsync(string? scopeId, string? userId, string taskType, AiCompletionResult result, int cost, CancellationToken cancellationToken = default)
    {
        Successes++;
        RemainingCredits -= cost;
        return Task.CompletedTask;
    }

    public Task RecordFailureAsync(string? scopeId, string? userId, string taskType, string? error, CancellationToken cancellationToken = default)
    {
        Failures++;
        return Task.CompletedTask;
    }
}
