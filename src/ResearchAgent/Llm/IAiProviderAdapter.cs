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
    /// Bu sağlayıcı <see cref="AiCompletionRequest.Images"/> okuyabiliyor mu. Varsayılan false:
    /// görselli bir isteği görsel okuyamayan sağlayıcıya göndermek, görselleri sessizce düşürüp
    /// "boş sayfa" cevabı üretmek demek olurdu — çağıran servisler bu bayrağa bakıp eler.
    /// </summary>
    bool SupportsImages => false;

    /// <summary>
    /// İsteği bu sağlayıcıyla tamamlar. Hata, kota aşımı veya rate-limit durumunda exception
    /// fırlatır; router bunu yakalayıp bir sonraki sağlayıcıya geçer.
    /// </summary>
    Task<AiCompletionResult> CompleteAsync(AiCompletionRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tool-calling (function-calling) çok-turlu sohbet — agentic akış (support chatbot). Yalnızca
    /// OpenAI-uyumlu sağlayıcılar (OpenAI, Groq) implemente eder; diğerleri NotSupportedException fırlatır
    /// (router uygun sağlayıcıyı seçer). Single-shot <see cref="CompleteAsync"/>'i etkilemez.
    /// </summary>
    Task<AiChatResult> CompleteChatAsync(AiChatRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"{Provider} tool-calling desteklemiyor (CompleteChatAsync).");
}
