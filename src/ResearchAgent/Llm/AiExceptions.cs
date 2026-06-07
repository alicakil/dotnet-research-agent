namespace WebApi.Services.Ai;

/// <summary>
/// Tenant'ın AI kredisi bu işi karşılamaya yetmediğinde fırlatılır. Üst katman bunu yakalayıp
/// kullanıcıya "kredi paketi al" (upsell) gösterir. Kredi HER tenant'ta zorunludur (test dâhil).
/// </summary>
public sealed class AiQuotaExceededException : Exception
{
    public int RequiredCredits { get; }
    public int RemainingCredits { get; }

    public AiQuotaExceededException(int requiredCredits, int remainingCredits)
        : base($"AI kredisi yetersiz: {requiredCredits} gerekli, {remainingCredits} kalan.")
    {
        RequiredCredits = requiredCredits;
        RemainingCredits = remainingCredits;
    }
}

/// <summary>Tüm AI sağlayıcılar (birincil + fallback) başarısız olduğunda fırlatılır.</summary>
public sealed class AiCompletionFailedException : Exception
{
    public AiCompletionFailedException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
