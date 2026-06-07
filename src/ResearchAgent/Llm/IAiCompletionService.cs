namespace WebApi.Services.Ai;

/// <summary>
/// Uygulama servislerinin (MealPlanService, NoteService...) kullandığı ÜST-SEVİYE AI giriş noktası.
/// Çağıran hangi sağlayıcının kullanıldığını bilmez; yalnızca "şunu üret" der.
///
/// Sorumluluklar (implementasyon = <see cref="AiRouter"/>):
///  1. Kota/kredi guard — çağrı öncesi tenant'ın AI hakkı var mı?
///  2. Provider routing + fallback — politikaya göre sağlayıcı seç, hata/kotada sıradakine geç.
///  3. Tenant-başı metering — gerçekleşen token kullanımını ve maliyeti kaydet, krediyi düş.
///
/// Kimlik (tenant/user) implementasyon içinde oturumdan (TenantResolver) alınır; istek nesnesinde
/// taşınmaz (bkz. <see cref="AiCompletionRequest"/> güvenlik notu).
/// </summary>
public interface IAiCompletionService
{
    Task<AiCompletionResult> CompleteAsync(AiCompletionRequest request, CancellationToken cancellationToken = default);
}
