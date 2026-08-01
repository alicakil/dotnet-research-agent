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
    /// Görsel girdiler (data URI: <c>data:image/jpeg;base64,…</c>). Dolu olduğunda istek yalnızca
    /// GÖRÜNTÜ OKUYABİLEN bir sağlayıcıya gidebilir — bugün OpenAI. Diğer adapter'lar
    /// <see cref="IAiProviderAdapter.SupportsImages"/> ile kendilerini eler; fallback zinciri onları
    /// atlar, çünkü görselleri sessizce düşürüp metin göndermek "boş sayfa okumuş" bir cevap üretir
    /// ve bu, hatanın fark edilmediği en kötü türü olurdu.
    ///
    /// bu yüzden ekranın kendisi kanıt kaynağı oluyor.
    /// </summary>
    public IReadOnlyList<string>? Images { get; init; }

    /// <summary>
    /// Doluysa, sağlayıcıdan bu JSON şemasına uygun structured output istenir
    /// (örn. meal plan = recipe-id + porsiyon listesi). Boşsa serbest metin döner.
    /// Şemaya uyma garantisi sağlayıcıdan sağlayıcıya değişir; adapter en iyi çabayı gösterir,
    /// çıktıyı çağıran servis yine de doğrular.
    /// </summary>
    public string? ResponseJsonSchema { get; init; }

    /// <summary>
    /// Akıl yürütme seviyesi override'ı — politika belirler, adapter uygular. Null = model
    /// varsayılanı. Yalnız OpenAI adapter'ı dikkate alır.
    /// </summary>
    public string? ReasoningEffort { get; init; }

    /// <summary>Maksimum çıktı token'ı (maliyet tavanı). Null = sağlayıcı/politika varsayılanı.</summary>
    public int? MaxOutputTokens { get; init; }

    /// <summary>
    /// Model override — dolu ise sağlayıcının global varsayılanı yerine bu model kullanılır
    /// (ör. derin araştırma sentezi → gpt-5.6-terra). Çağıran servis genelde
    /// <c>policy.Model</c>'i buraya geçirir; boşsa sağlayıcı ayarındaki model kullanılır.
    ///
    /// NOT: yalnızca OpenAI adapter'ı dikkate alır — model adları sağlayıcıya özgüdür.
    /// Fallback'e düşüldüğünde (Groq/Gemini) o sağlayıcı KENDİ yapılandırılmış modelini
    /// kullanır; bu kasıtlı, aksi halde tanımadığı model adıyla 400 alırdı.
    /// </summary>
    public string? Model { get; init; }
}
