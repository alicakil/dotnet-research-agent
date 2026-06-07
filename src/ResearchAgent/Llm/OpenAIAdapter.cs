using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace WebApi.Services.Ai;

/// <summary>
/// Groq (OpenAI-uyumlu chat completions) adapter'ı — test fazı fallback sağlayıcı.
/// Çok hızlı (300+ tok/s), ücretsiz tier; API anahtarı SSM→env (<c>GROQ_API_KEY</c>) ile gelir.
/// Gemini başarısız/kota-dolu olduğunda <see cref="AiRouter"/> buraya düşer.
/// </summary>
public sealed class GroqAdapter : IAiProviderAdapter
{
    private const string Endpoint = "https://api.groq.com/openai/v1/chat/completions";

    private readonly HttpClient _httpClient;
    private readonly AiOptions _options;
    private readonly ILogger<GroqAdapter> _logger;
    private readonly IAiRateLimitTracker _rateLimitTracker;
    private readonly string? _apiKey;

    public GroqAdapter(
        HttpClient httpClient,
        IOptions<AiOptions> options,
        IConfiguration configuration,
        IAiRateLimitTracker rateLimitTracker,
        ILogger<GroqAdapter> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
        _rateLimitTracker = rateLimitTracker;
        _apiKey = configuration["Ai:Groq:ApiKey"]
            ?? Environment.GetEnvironmentVariable("GROQ_API_KEY");
    }

    public AiProvider Provider => AiProvider.Groq;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);

    public async Task<AiCompletionResult> CompleteAsync(AiCompletionRequest request, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("Groq API anahtarı tanımlı değil (Ai:Groq:ApiKey / GROQ_API_KEY).");

        var model = _options.Groq.Model;
        var systemText = AiAdapterHelpers.ComposeSystemPrompt(request);

        var messages = new List<object>();
        if (!string.IsNullOrWhiteSpace(systemText))
            messages.Add(new { role = "system", content = systemText });
        messages.Add(new { role = "user", content = request.UserPrompt });

        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = messages,
        };
        if (request.MaxOutputTokens is int maxTokens)
            body["max_tokens"] = maxTokens;
        if (!string.IsNullOrWhiteSpace(request.ResponseJsonSchema))
            body["response_format"] = new { type = "json_object" };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey!); // IsConfigured guard ile non-null garanti
        httpRequest.Content = new StringContent(
            JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        _rateLimitTracker.Record(AiProvider.Groq, response.Headers); // canlı rate-limit (kalan/reset) yakala

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Groq çağrısı başarısız ({Status}): {Body}",
                (int)response.StatusCode, AiAdapterHelpers.Truncate(json));
            throw new HttpRequestException($"Groq {(int)response.StatusCode}: {AiAdapterHelpers.Truncate(json)}");
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            throw new HttpRequestException($"Groq boş yanıt döndürdü: {AiAdapterHelpers.Truncate(json)}");

        var text = string.Empty;
        if (choices[0].TryGetProperty("message", out var message) &&
            message.TryGetProperty("content", out var contentEl))
        {
            text = contentEl.GetString() ?? string.Empty;
        }

        int inputTokens = 0, outputTokens = 0;
        if (root.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("prompt_tokens", out var p)) inputTokens = p.GetInt32();
            if (usage.TryGetProperty("completion_tokens", out var c)) outputTokens = c.GetInt32();
        }

        return new AiCompletionResult
        {
            Text = text,
            Provider = AiProvider.Groq,
            Model = model,
            Usage = new AiUsage { InputTokens = inputTokens, OutputTokens = outputTokens },
        };
    }
}
