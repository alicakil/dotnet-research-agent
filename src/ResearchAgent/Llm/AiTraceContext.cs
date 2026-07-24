namespace WebApi.Services.Ai;

/// <summary>
/// İstek-kapsamlı (scoped) AMBIENT kimlik bağlamı — AI trace satırının KİMLİK sütunlarını doldurur.
/// Üst katmanlar (endpoint, <see cref="AiRouter"/>, agent servisleri) burayı doldurur; en alttaki
/// <see cref="TracingAiProviderAdapter"/> okur. Böylece tenant/danışan kimliği modele giden ANONİM
/// payload'a KARIŞMADAN (golden rule: "kim" oturumdan, "ne" prompt'tan) trace'e AYRI sütun olarak yazılır.
///
/// Bir scope'ta birden çok AI çağrısı olabilir (agent tool-loop); <see cref="CorrelationId"/> aynı
/// mantıksal işlemin (fallback denemeleri) satırlarını gruplar — <see cref="BeginOperation"/> ile tazelenir.
/// </summary>
public sealed class AiTraceContext
{
    public string? TenantId { get; set; }
    public Guid? UserId { get; set; }
    public Guid? ClientId { get; set; }
    public string? ContextKey { get; set; }
    public string? Surface { get; set; }

    /// <summary>Aynı mantıksal işlemin (fallback denemeleri) satırlarını gruplayan kimlik.</summary>
    public Guid? CorrelationId { get; private set; }

    /// <summary>Yeni bir mantıksal AI işlemi başlat — taze correlation id ata (fallback turları paylaşır).</summary>
    public Guid BeginOperation()
    {
        var id = Guid.NewGuid();
        CorrelationId = id;
        return id;
    }
}
