using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace WebApi.Services.Ai;

/// <summary>
/// Google Gemini (Generative Language API) adapter'ı — test fazı primary sağlayıcı.
/// Ücretsiz tier üzerinden çalışır; API anahtarı SSM→env (<c>GEMINI_API_KEY</c>) ile gelir.
/// Yalnızca SENTETİK / test-tenant verisiyle kullanılır (GDPR: gerçek hasta verisi prod'da Bedrock-Claude'a gider).
/// </summary>
public sealed class GeminiAdapter : IAiProviderAdapter
{
    private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta";

    private readonly HttpClient _httpClient;
    private readonly AiOptions _options;
    private readonly ILogger<GeminiAdapter> _logger;
    private readonly string? _apiKey;

    public GeminiAdapter(
        HttpClient httpClient,
        IOptions<AiOptions> options,
        IConfiguration configuration,
        ILogger<GeminiAdapter> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
        _apiKey = configuration["Ai:Gemini:ApiKey"]
            ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY");
    }

    public AiProvider Provider => AiProvider.Gemini;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);

    public async Task<AiCompletionResult> CompleteAsync(AiCompletionRequest request, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("Gemini API anahtarı tanımlı değil (Ai:Gemini:ApiKey / GEMINI_API_KEY).");

        var model = _options.Gemini.Model;
        var systemText = AiAdapterHelpers.ComposeSystemPrompt(request);

        var generationConfig = new Dictionary<string, object?>();
        if (request.MaxOutputTokens is int maxTokens)
            generationConfig["maxOutputTokens"] = maxTokens;
        if (!string.IsNullOrWhiteSpace(request.ResponseJsonSchema))
            generationConfig["responseMimeType"] = "application/json";

        var body = new Dictionary<string, object?>
        {
            ["contents"] = new[]
            {
                new { role = "user", parts = new[] { new { text = request.UserPrompt } } },
            },
            ["generationConfig"] = generationConfig,
        };
        if (!string.IsNullOrWhiteSpace(systemText))
            body["system_instruction"] = new { parts = new[] { new { text = systemText } } };

        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"{BaseUrl}/models/{model}:generateContent");
        httpRequest.Headers.Add("x-goog-api-key", _apiKey!); // IsConfigured guard ile non-null garanti
        httpRequest.Content = new StringContent(
            JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Gemini çağrısı başarısız ({Status}): {Body}",
                (int)response.StatusCode, AiAdapterHelpers.Truncate(json));
            throw new HttpRequestException($"Gemini {(int)response.StatusCode}: {AiAdapterHelpers.Truncate(json)}");
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
            throw new HttpRequestException($"Gemini boş/engellenmiş yanıt döndürdü: {AiAdapterHelpers.Truncate(json)}");

        var text = string.Empty;
        if (candidates[0].TryGetProperty("content", out var content) &&
            content.TryGetProperty("parts", out var parts) &&
            parts.GetArrayLength() > 0 &&
            parts[0].TryGetProperty("text", out var textEl))
        {
            text = textEl.GetString() ?? string.Empty;
        }

        int inputTokens = 0, outputTokens = 0;
        if (root.TryGetProperty("usageMetadata", out var usage))
        {
            if (usage.TryGetProperty("promptTokenCount", out var p)) inputTokens = p.GetInt32();
            if (usage.TryGetProperty("candidatesTokenCount", out var c)) outputTokens = c.GetInt32();
        }

        return new AiCompletionResult
        {
            Text = text,
            Provider = AiProvider.Gemini,
            Model = model,
            Usage = new AiUsage { InputTokens = inputTokens, OutputTokens = outputTokens },
        };
    }
}
