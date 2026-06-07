namespace WebApi.Services.Ai;

/// <summary>
/// Tek bir LLM sağlayıcısına (Gemini, Groq, Bedrock-Claude...) adapte olan sözleşme.
/// <see cref="AiRouter"/> bu adapter'ları model politikasına göre sırayla dener: birincil
/// başarısız/kota-dolu olursa bir sonrakine düşer (fallback).
///
/// Her adapter, sonuçta token kullanımını (<see cref="AiUsage"/>) döndürmek ZORUNDADIR —
/// tenant-başı metering ve maliyet hesabı buna dayanır.
/// </summary>
public interface IAiProviderAdapter
{
    /// <summary>Bu adapter'ın temsil ettiği sağlayıcı.</summary>
    AiProvider Provider { get; }

    /// <summary>
    /// Sağlayıcı şu an kullanılabilir mi (API anahtarı tanımlı mı vb.). Router, konfigüre
    /// olmayan adapter'ları atlar — eksik anahtar tüm akışı düşürmez.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>
    /// İsteği bu sağlayıcıyla tamamlar. Hata, kota aşımı veya rate-limit durumunda exception
    /// fırlatır; router bunu yakalayıp bir sonraki sağlayıcıya geçer.
    /// </summary>
    Task<AiCompletionResult> CompleteAsync(AiCompletionRequest request, CancellationToken cancellationToken = default);
}
