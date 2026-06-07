namespace WebApi.Services.Ai;

/// <summary>
/// Bir AI tamamlama isteğinin sonucu: üretilen metin + isteği gerçekte hangi sağlayıcı/model
/// karşıladı + ne kadar token harcandı. Provider/Model alanları fallback nedeniyle istenen
/// birincil sağlayıcıdan farklı olabilir; metering bu gerçekleşen değerleri kaydeder.
/// </summary>
public sealed class AiCompletionResult
{
    /// <summary>Üretilen metin. <c>ResponseJsonSchema</c> verildiyse şemaya uygun JSON string.</summary>
    public required string Text { get; init; }

    /// <summary>İsteği gerçekte karşılayan sağlayıcı (fallback sonrası değişmiş olabilir).</summary>
    public required AiProvider Provider { get; init; }

    /// <summary>Kullanılan model adı (ör. "gemini-2.5-flash").</summary>
    public required string Model { get; init; }

    /// <summary>Token kullanımı.</summary>
    public required AiUsage Usage { get; init; }
}
