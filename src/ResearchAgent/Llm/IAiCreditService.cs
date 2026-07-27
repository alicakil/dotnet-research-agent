namespace WebApi.Services.Ai;

/// <summary>
/// AI kredi cüzdanı yönetimi: kota kontrolü + kullanım kaydı (kredi düşme + ledger satırı).
///
/// Kredi HER tenant'ta zorunludur — test/demo hesapları DÂHİL. Burada test-bypass YOKTUR
/// (kullanıcı kredi mekanizmasını test edebilsin diye). Erişim kontrolü AYRI bir kapıdır
/// (feature flag) ve bu servisin sorumluluğu değildir.
/// </summary>
public interface IAiCreditService
{
    /// <summary>
    /// Tenant'ın bu işi karşılayacak kredisi var mı? Yoksa <see cref="AiQuotaExceededException"/> fırlatır.
    /// Yeni tenant için cüzdanı oluşturur, aylık dönem geçtiyse allowance'ı tazeler.
    /// </summary>
    Task EnsureCanSpendAsync(string tenantId, int creditCost, CancellationToken cancellationToken = default);

    /// <summary>Başarılı çağrı: krediyi düş (önce allowance, biterse purchased) + ledger'a başarı satırı yaz.</summary>
    Task RecordSuccessAsync(string tenantId, Guid userId, AiTaskType taskType, AiCompletionResult result, int creditCost, CancellationToken cancellationToken = default);

    /// <summary>Başarısız çağrı: kredi DÜŞÜLMEZ; ledger'a 0-kredili hata satırı yazılır (audit/debug).</summary>
    Task RecordFailureAsync(string tenantId, Guid userId, AiTaskType taskType, string? error, CancellationToken cancellationToken = default);

    /// <summary>Metering'siz kullanım kaydı (public/admin chatbot — tenant YOK): kredi DÜŞÜLMEZ, sadece AiUsageLogs'a token/maliyet satırı (admin görünürlüğü).</summary>
    Task RecordUsageOnlyAsync(string tenantId, Guid userId, AiTaskType taskType, AiCompletionResult result, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tenant'ın kalan AI kredisi — iki kova AYRI döner (UI dürüstlüğü: "yanacak" ve "devreden" aynı
    /// sayıda birleştirilirse raf ömrü sözü kullanıcıya görünmez olur). Salt-okunur; cüzdan yoksa
    /// varsayılan aylık hak döner.
    /// </summary>
    Task<AiCreditBalanceSnapshot> GetBalanceAsync(string tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tek-seferlik top-up/grant: PurchasedRemaining'e kredi ekler (aylık reset yok, devreder; tarifeden
    /// bağımsız) ve raf ömrünü baştan kurar. Kural: tekrarlayan ödeme → yanan kredi, tek seferlik → devreden.
    /// </summary>
    Task GrantPurchasedCreditsAsync(string tenantId, int amount, CancellationToken cancellationToken = default);
}

/// <summary>
/// Cüzdanın salt-okunur görüntüsü. <paramref name="AllowanceRemaining"/> dönem sonunda YANAR;
/// <paramref name="PurchasedRemaining"/> devreder ama <paramref name="PurchasedExpiresAt"/>'te sona erer
/// (kova boşsa null). Bkz. docs/wiki/ai-mimarisi.md §6.1.
/// </summary>
public readonly record struct AiCreditBalanceSnapshot(
    int AllowanceRemaining,
    int PurchasedRemaining,
    DateTime? PurchasedExpiresAt)
{
    /// <summary>Harcanabilir toplam (her iki kova).</summary>
    public int Total => AllowanceRemaining + PurchasedRemaining;
}
