using System.Diagnostics;
using System.Text.Json;
using DAL.Entites;
using Microsoft.Extensions.Options;

namespace WebApi.Services.Ai;

/// <summary>
/// MERKEZİ AI izlenebilirlik: her <see cref="IAiProviderAdapter"/>'ı sarar. Üç çağrı yolu da
/// (AiRouter single-shot · AiAgentRunner tool-loop · doğrudan-adapter EmailTriage/Invoice) eninde
/// sonunda bir adapter'a indiği için, TEK yer = tam kapsama; gelecekteki her yeni AI özelliği de
/// otomatik kapsanır. Modele giden/gelen ham metni + <see cref="AiTraceContext"/>'ten kimliği
/// (ANONİM payload'a dokunmadan) <see cref="AiTraceEntity"/>'ye yazar. Fallback: HER deneme ayrı satır.
/// </summary>
public sealed class TracingAiProviderAdapter : IAiProviderAdapter
{
    private readonly IAiProviderAdapter _inner;
    private readonly IAiTraceRecorder _recorder;
    private readonly AiTraceContext _context;
    private readonly AiOptions _options;

    public TracingAiProviderAdapter(
        IAiProviderAdapter inner, IAiTraceRecorder recorder, AiTraceContext context, IOptions<AiOptions> options)
    {
        _inner = inner;
        _recorder = recorder;
        _context = context;
        _options = options.Value;
    }

    public AiProvider Provider => _inner.Provider;
    public bool IsConfigured => _inner.IsConfigured;

    public async Task<AiCompletionResult> CompleteAsync(AiCompletionRequest request, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var systemText = AiAdapterHelpers.ComposeSystemPrompt(request);   // modele giden BİREBİR sistem metni
        try
        {
            var result = await _inner.CompleteAsync(request, cancellationToken);
            sw.Stop();
            await SafeRecord(NewTrace(request.TaskType, systemText, request.UserPrompt)
                .WithSuccess(result, sw.ElapsedMilliseconds, ComputeCost(result)), cancellationToken);
            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            await SafeRecord(NewTrace(request.TaskType, systemText, request.UserPrompt)
                .WithFailure(ex, sw.ElapsedMilliseconds), cancellationToken);
            throw;   // router fallback'i sürsün
        }
    }

    public async Task<AiChatResult> CompleteChatAsync(AiChatRequest request, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var systemText = request.Messages.FirstOrDefault(m => m.Role == "system")?.Content;
        var conversation = JsonSerializer.Serialize(request.Messages);   // tam çok-turlu bağlam
        try
        {
            var result = await _inner.CompleteChatAsync(request, cancellationToken);
            sw.Stop();
            var response = JsonSerializer.Serialize(new { text = result.Text, toolCalls = result.ToolCalls });
            var (inPrice, outPrice) = _options.GetModelPrice(result.Provider, result.Model);
            var cost = (result.Usage.InputTokens / 1_000_000m) * inPrice + (result.Usage.OutputTokens / 1_000_000m) * outPrice;
            var trace = NewTrace(request.TaskType, systemText, conversation);
            trace.Provider = result.Provider.ToString();
            trace.Model = result.Model;
            trace.ResponseText = response;
            trace.InputTokens = result.Usage.InputTokens;
            trace.OutputTokens = result.Usage.OutputTokens;
            trace.CostUsd = cost;
            trace.LatencyMs = (int)sw.ElapsedMilliseconds;
            trace.Success = true;
            await SafeRecord(trace, cancellationToken);
            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            await SafeRecord(NewTrace(request.TaskType, systemText, conversation)
                .WithFailure(ex, sw.ElapsedMilliseconds), cancellationToken);
            throw;
        }
    }

    private decimal ComputeCost(AiCompletionResult result)
    {
        var (inPrice, outPrice) = _options.GetModelPrice(result.Provider, result.Model);
        return (result.Usage.InputTokens / 1_000_000m) * inPrice + (result.Usage.OutputTokens / 1_000_000m) * outPrice;
    }

    /// <summary>Kimlik sütunlarını ambient bağlamdan doldurulmuş taze bir trace iskeleti.</summary>
    private AiTraceEntity NewTrace(AiTaskType taskType, string? systemPrompt, string? userPrompt) => new()
    {
        CorrelationId = _context.CorrelationId ?? Guid.NewGuid(),
        TenantId = _context.TenantId,
        UserId = _context.UserId,
        ClientId = _context.ClientId,
        ContextKey = _context.ContextKey,
        Surface = _context.Surface,
        TaskType = taskType.ToString(),
        Provider = _inner.Provider.ToString(),   // fallback denemesi başarısız olsa bile hangi sağlayıcı
        SystemPrompt = systemPrompt,
        UserPrompt = userPrompt,
        CreatedBy = _context.UserId?.ToString() ?? "system",
    };

    private async Task SafeRecord(AiTraceEntity trace, CancellationToken cancellationToken)
    {
        // Recorder zaten fail-safe; yine de trace yazımı istek iptalinden etkilenmesin (CancellationToken.None).
        await _recorder.RecordAsync(trace, CancellationToken.None);
    }
}

/// <summary>Trace iskeletini sonuçla doldurma yardımcıları (okunabilirlik).</summary>
internal static class AiTraceEntityExtensions
{
    public static AiTraceEntity WithSuccess(this AiTraceEntity trace, AiCompletionResult result, long latencyMs, decimal costUsd)
    {
        trace.Provider = result.Provider.ToString();
        trace.Model = result.Model;
        trace.ResponseText = result.Text;
        trace.InputTokens = result.Usage.InputTokens;
        trace.OutputTokens = result.Usage.OutputTokens;
        trace.CostUsd = costUsd;
        trace.LatencyMs = (int)latencyMs;
        trace.Success = true;
        return trace;
    }

    public static AiTraceEntity WithFailure(this AiTraceEntity trace, Exception ex, long latencyMs)
    {
        trace.LatencyMs = (int)latencyMs;
        trace.Success = false;
        var msg = ex.Message ?? ex.GetType().Name;
        trace.ErrorMessage = msg.Length > 2000 ? msg[..2000] : msg;
        return trace;
    }
}
