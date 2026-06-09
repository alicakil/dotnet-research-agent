using Microsoft.Extensions.Options;
using Shared.Services;

namespace WebApi.Services.Ai;

/// <summary>
/// <see cref="IAiCompletionService"/> implementasyonu — Faz 0 router.
/// Sorumluluk zinciri: kimlik (oturumdan) → kota guard → sağlayıcı seç + fallback → metering.
/// Uygulama servisleri (MealPlanService vb.) yalnızca bunu enjekte eder; sağlayıcıyı bilmezler.
/// </summary>
public sealed class AiRouter : IAiCompletionService
{
    private readonly IEnumerable<IAiProviderAdapter> _adapters;
    private readonly AiOptions _options;
    private readonly TenantResolver _tenantResolver;
    private readonly IAiCreditService _creditService;
    private readonly ILogger<AiRouter> _logger;

    public AiRouter(
        IEnumerable<IAiProviderAdapter> adapters,
        IOptions<AiOptions> options,
        TenantResolver tenantResolver,
        IAiCreditService creditService,
        ILogger<AiRouter> logger)
    {
        _adapters = adapters;
        _options = options.Value;
        _tenantResolver = tenantResolver;
        _creditService = creditService;
        _logger = logger;
    }

    public async Task<AiCompletionResult> CompleteAsync(AiCompletionRequest request, CancellationToken cancellationToken = default)
    {
        // 1. Kimlik — GÜVENLİK: oturumdan (JWT/TenantResolver), istekten/prompt'tan DEĞİL.
        var tenant = _tenantResolver.GetCurrentTenant()
            ?? throw new InvalidOperationException("AI çağrısı için tenant context bulunamadı.");
        var tenantId = tenant.TenantId;
        var userId = _tenantResolver.GetUserId();

        var policy = _options.GetPolicy(request.TaskType);

        // 2. Kota guard — yetersizse AiQuotaExceededException (her tenant'ta zorunlu, test/demo dâhil).
        await _creditService.EnsureCanSpendAsync(tenantId, policy.CreditCost, cancellationToken);

        // Politika max-token'ını isteğe uygula (istek belirtmemişse).
        var effectiveRequest = new AiCompletionRequest
        {
            TaskType = request.TaskType,
            SystemPrompt = request.SystemPrompt,
            UserPrompt = request.UserPrompt,
            ResponseJsonSchema = request.ResponseJsonSchema,
            MaxOutputTokens = request.MaxOutputTokens ?? policy.MaxOutputTokens,
        };

        // 3. Sağlayıcı sırası + fallback (politikadaki sıraya göre).
        Exception? lastError = null;
        foreach (var providerType in policy.Providers)
        {
            var adapter = _adapters.FirstOrDefault(a => a.Provider == providerType && a.IsConfigured);
            if (adapter is null)
            {
                _logger.LogDebug("AI sağlayıcı {Provider} atlandı (kayıtlı değil veya API anahtarı yok).", providerType);
                continue;
            }

            try
            {
                var result = await adapter.CompleteAsync(effectiveRequest, cancellationToken);

                // 4. Metering — krediyi düş + ledger'a başarı satırı yaz.
                await _creditService.RecordSuccessAsync(tenantId, userId, request.TaskType, result, policy.CreditCost, cancellationToken);

                _logger.LogInformation(
                    "AI tamamlandı: tenant={TenantId}, task={Task}, provider={Provider}, in={In}, out={Out}, kredi={Credit}",
                    tenantId, request.TaskType, result.Provider, result.Usage.InputTokens, result.Usage.OutputTokens, policy.CreditCost);

                return result;
            }
            catch (Exception ex)
            {
                lastError = ex;
                _logger.LogWarning(ex, "AI sağlayıcı {Provider} başarısız; fallback deneniyor.", providerType);
            }
        }

        // 5. Hepsi başarısız → kredi DÜŞÜLMEZ, ledger'a hata satırı yaz, DOSTÇA hata fırlat (kullanıcıya gider).
        await _creditService.RecordFailureAsync(tenantId, userId, request.TaskType, lastError?.Message, cancellationToken);

        var errText = lastError?.Message ?? string.Empty;
        var isRateLimited = errText.Contains("429") || errText.Contains("rate_limit", StringComparison.OrdinalIgnoreCase);
        var isDailyQuota = errText.Contains("per day", StringComparison.OrdinalIgnoreCase) || errText.Contains("(TPD)", StringComparison.OrdinalIgnoreCase);
        throw new AiCompletionFailedException(
            isDailyQuota
                ? "AI servisinin günlük ücretsiz kotası doldu; yarın yenilenir. (Daha fazlası için kredi paketleri yakında.)"
                : isRateLimited
                    ? "AI servisi şu an çok yoğun (dakikalık hız sınırı). Lütfen birkaç saniye sonra tekrar deneyin."
                    : "AI servisi geçici olarak kullanılamıyor. Lütfen biraz sonra tekrar deneyin.",
            lastError);
    }
}
