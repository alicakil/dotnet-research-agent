namespace WebApi.Services.Ai;

/// <summary>
/// Bir AI çağrısının token kullanımı. Tenant-başı metering ve gerçek maliyet hesabı için
/// her sağlayıcı adapter'ı bunu doldurmak zorundadır.
/// </summary>
public sealed class AiUsage
{
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public int TotalTokens => InputTokens + OutputTokens;
}
