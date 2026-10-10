using Microsoft.Extensions.Options;
using System.Text.Json;

namespace WebApi.Services.Ai;

/// <summary>
/// Tool-calling (agentic) loop'unu çalıştıran PAYLAŞILAN runner — in-app support + (ileride) public
/// marketing chatbot İKİSİ de kullanır. Akış: sağlayıcı seç (policy + fallback) → CompleteChatAsync →
/// model araç çağırırsa dispatch et + sonucu mesaja ekle → tekrar → final metin (veya iterasyon limiti).
/// Metering/gating BURADA YOK — çağıran servis (SupportAgentService vb.) yapar. Kimlik runner'a girmez;
/// tenant tool dispatcher'ın içinde OTURUMDAN gelir (golden rule).
/// </summary>
public interface IAiAgentRunner
{
    Task<AiAgentResult> RunAsync(
        AiTaskType taskType,
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
}

public sealed class AiAgentRunner : IAiAgentRunner
{
    private const int MaxIterations = 8;   // güvenlik tavanı (sonsuz araç-döngüsünü kes; admin SQL birkaç tur sürebilir)

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
        AiTaskType taskType,
        string systemPrompt,
        IReadOnlyList<AiChatMessage> conversation,
        IReadOnlyList<AiToolDefinition> tools,
        Func<string, JsonElement, CancellationToken, Task<string>> dispatch,
        CancellationToken cancellationToken = default)
    {
        var policy = _options.GetPolicy(taskType);

        // Çalışan mesaj listesi: system + konuşma; araç turları buraya eklenir.
        var messages = new List<AiChatMessage>(conversation.Count + 1)
        {
            new() { Role = "system", Content = systemPrompt },
        };
        messages.AddRange(conversation);

        int totalIn = 0, totalOut = 0, totalCached = 0;
        AiProvider provider = default;
        var model = string.Empty;

        // Responses zincirinin son halkası. Mesaj listesi buna RAĞMEN eksiksiz tutulur: zincir
        // yoksa (chat/completions yolu, id'siz yanıt) tek elimizde kalan tam geçmiştir.
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
            // chat/completions döndüyse null olur → sonraki tur tam listeyle gider.
            previousResponseId = result.ResponseId;

            // Araç çağrısı yoksa → final cevap.
            if (result.ToolCalls.Count == 0)
                return Done(result.Text ?? string.Empty, provider, model, totalIn, totalOut, totalCached);

            // assistant turn'ü (araç talepleriyle) + her aracın sonucunu (tool mesajı) ekle.
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
                    _logger.LogWarning(ex, "Agent tool '{Tool}' başarısız.", call.Name);
                    toolResult = JsonSerializer.Serialize(new { error = "tool failed" });   // model toparlasın
                }
                messages.Add(new AiChatMessage { Role = "tool", ToolCallId = call.Id, Content = toolResult });
            }
        }

        _logger.LogWarning("AiAgentRunner iterasyon limitine ({Max}) ulaştı (task={Task}).", MaxIterations, taskType);
        return Done(
            "Bu konuda tam yardımcı olamadım — lütfen WhatsApp ya da destek görüşmesiyle bize ulaşın.",
            provider, model, totalIn, totalOut, totalCached);
    }

    private static AiAgentResult Done(string text, AiProvider provider, string model, int inTok, int outTok, int cachedTok) => new()
    {
        Text = text,
        Provider = provider,
        Model = model,
        Usage = new AiUsage { InputTokens = inTok, OutputTokens = outTok, CachedInputTokens = cachedTok },
    };

    /// <summary>Politikadaki sağlayıcıları sırayla dener; ilk başarılı sonucu döner.</summary>
    private async Task<AiChatResult> CallWithFallbackAsync(AiTaskPolicy policy, AiChatRequest req, CancellationToken ct)
    {
        Exception? lastError = null;
        foreach (var providerType in policy.Providers)
        {
            var adapter = _adapters.FirstOrDefault(a => a.Provider == providerType && a.IsConfigured);
            if (adapter is null) continue;
            try { return await adapter.CompleteChatAsync(req, ct); }
            catch (Exception ex) { lastError = ex; _logger.LogWarning(ex, "Agent provider {Provider} başarısız; fallback.", providerType); }
        }
        throw new AiCompletionFailedException("AI servisi geçici olarak kullanılamıyor. Lütfen biraz sonra tekrar deneyin.", lastError);
    }

    private static JsonElement ParseArgs(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) argumentsJson = "{}";
        try { return JsonSerializer.Deserialize<JsonElement>(argumentsJson); }
        catch { return JsonSerializer.Deserialize<JsonElement>("{}"); }
    }
}
