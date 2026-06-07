namespace WebApi.Services.Ai;

/// <summary>
/// Sağlayıcıdan bağımsız tek bir AI tamamlama isteği.
///
/// GÜVENLİK: Kimlik (tenant / user / rol) BURADA YOKTUR. Kimlik, çağrı sırasında oturumdan
/// (TenantResolver, JWT) alınır — asla çağıran tarafından veya prompt içinden verilmez.
/// Bu yüzden bir tenant başka bir tenant'ın verisini "isteyemez": model "ne" üreteceğini söyler,
/// "kim" olduğunu belirleyemez. Grounding verisi (örn. o tenant'a ait tarifler) çağıran servis
/// tarafından önceden, yetkili biçimde çekilip <see cref="SystemPrompt"/> / <see cref="UserPrompt"/>
/// içine konur.
/// </summary>
public sealed class AiCompletionRequest
{
    /// <summary>İş türü — model politikasını ve kredi maliyetini belirler.</summary>
    public required AiTaskType TaskType { get; init; }

    /// <summary>Sistem talimatı: rol, kurallar ve grounding context (önceden çekilmiş veri) buraya konur.</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>Kullanıcı/görev istemi.</summary>
    public required string UserPrompt { get; init; }

    /// <summary>
    /// Doluysa, sağlayıcıdan bu JSON şemasına uygun structured output istenir
    /// (örn. meal plan = recipe-id + porsiyon listesi). Boşsa serbest metin döner.
    /// Şemaya uyma garantisi sağlayıcıdan sağlayıcıya değişir; adapter en iyi çabayı gösterir,
    /// çıktıyı çağıran servis yine de doğrular.
    /// </summary>
    public string? ResponseJsonSchema { get; init; }

    /// <summary>Maksimum çıktı token'ı (maliyet tavanı). Null = sağlayıcı/politika varsayılanı.</summary>
    public int? MaxOutputTokens { get; init; }
}
