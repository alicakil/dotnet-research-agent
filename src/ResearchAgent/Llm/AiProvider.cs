namespace WebApi.Services.Ai;

/// <summary>
/// Somut LLM sağlayıcısı. Metering (tenant-başı kullanım kaydı) ve routing (fallback) bu değere göre
/// yapılır. Şu an: Groq (ücretsiz, primary) + Gemini (opsiyonel fallback). Yeni sağlayıcı gerektiğinde
/// (örn. Claude/OpenAI doğrudan API) buraya +1 değer ve bir adapter eklenir — seam esnek, plan basit.
/// </summary>
public enum AiProvider
{
    Gemini = 1,
    Groq = 2,
    OpenAI = 3,
}
