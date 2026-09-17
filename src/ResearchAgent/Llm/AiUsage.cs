namespace WebApi.Services.Ai;

/// <summary>
/// Bir AI çağrısının token kullanımı. Tenant-başı metering ve gerçek maliyet hesabı için
/// her sağlayıcı adapter'ı bunu doldurmak zorundadır.
/// </summary>
public sealed class AiUsage
{
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }

    /// <summary>
    /// <see cref="InputTokens"/>'ın sağlayıcı önbelleğinden gelen KISMI (ayrı bir sayı değil, alt küme).
    /// Sabit sistem promptu her çağrıda aynı önekle gittiği için bu kısım indirimli ücretlenir;
    /// sayılmazsa maliyet raporu gerçeğin birkaç katı çıkar ve prompt boyutu kararları yanlış rakama dayanır.
    /// </summary>
    public int CachedInputTokens { get; init; }
    public int TotalTokens => InputTokens + OutputTokens;
}
