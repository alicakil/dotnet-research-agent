namespace WebApi.Services.Ai;

/// <summary>
/// Somut LLM sağlayıcısı. Metering (tenant-başı kullanım kaydı) ve routing (fallback) bu değere
/// göre yapılır. Test fazı: Gemini (primary) + Groq (fallback). Prod'da gerçek hasta verisi olan
/// özellikler için Bedrock-Claude (AB ikametgâhı, GDPR) Faz 4'te eklenir.
/// </summary>
public enum AiProvider
{
    Gemini = 1,
    Groq = 2,
    BedrockClaude = 3,
}
