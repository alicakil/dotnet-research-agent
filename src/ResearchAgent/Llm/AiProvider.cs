namespace WebApi.Services.Ai;

/// <summary>
/// Somut LLM sağlayıcısı. Metering (tenant-başı kullanım kaydı) ve routing (fallback) bu değere göre
/// yapılır. Şu an bütün metin görevleri OpenAI'de; yedek sağlayıcı yok. Yeni sağlayıcı gerektiğinde
/// (örn. Claude doğrudan API) buraya +1 değer ve bir adapter eklenir — seam esnek, plan basit.
/// </summary>
public enum AiProvider
{
    Gemini = 1,
    Groq = 2,   // Kaldırıldı (VIT-39): adapter'ı da politikası da yok. Değer duruyor: eski AiTrace / AiUsageLogs satırları bu adı taşıyor.
    OpenAI = 3,
}
