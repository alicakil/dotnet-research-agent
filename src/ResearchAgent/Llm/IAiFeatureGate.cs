namespace WebApi.Services.Ai;

/// <summary>
/// AI özelliklerinin ERİŞİM kapısı — kredi/kotadan AYRIDIR. Tek soru: "Bu tenant AI özelliğini
/// görebilir / çağırabilir mi?"
///
/// Faz 0/test: yalnızca test/demo hesapları (<c>IsTestAccount</c> veya <c>IsDemo</c>); varsayılan olarak
/// diğer herkese KAPALI. Faz 4'te ücretli tenant'lara açılır. Global kill-switch: <c>Ai:Enabled</c>.
///
/// NOT: Bu yalnızca erişimdir. Erişebilen tenant'lar (test/demo dâhil) yine de kredi/kotaya tabidir
/// (bkz. <see cref="IAiCreditService"/>) — böylece kredi mantığı test hesaplarında da test edilebilir.
/// </summary>
public interface IAiFeatureGate
{
    /// <summary>Mevcut oturumun tenant'ı AI özelliklerine erişebiliyor mu?</summary>
    Task<bool> IsEnabledForCurrentTenantAsync(CancellationToken cancellationToken = default);
}
