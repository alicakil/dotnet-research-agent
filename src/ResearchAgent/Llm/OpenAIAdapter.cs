using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace WebApi.Services.Ai;

/// <summary>
/// OpenAI (Chat Completions) adapter'ı — go-live primary sağlayıcı (paid). Varsayılan model gpt-5-mini.
/// OpenAI-uyumlu şema (GroqAdapter ile aynı body); farklar: api.openai.com endpoint + <c>max_completion_tokens</c>
/// (GPT-5/o-serisi max_tokens kabul etmez). API anahtarı SSM→env (<c>Ai:OpenAI:ApiKey</c> / <c>OPENAI_API_KEY</c>).
/// Key yoksa IsConfigured=false → AiRouter otomatik Groq (ücretsiz) fallback'ine düşer.
/// </summary>
public sealed class OpenAIAdapter : IAiProviderAdapter
{
    private const string Endpoint = "https://api.openai.com/v1/chat/completions";

    private readonly HttpClient _httpClient;
    private readonly AiOptions _options;
    private readonly ILogger<OpenAIAdapter> _logger;
    private readonly string? _apiKey;

    public OpenAIAdapter(
        HttpClient httpClient,
        IOptions<AiOptions> options,
        IConfiguration configuration,
        ILogger<OpenAIAdapter> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
        _apiKey = configuration["Ai:OpenAI:ApiKey"]
            ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
    }

    public AiProvider Provider => AiProvider.OpenAI;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);

    /// <summary>gpt-5 ailesi çok-modlu: görseller mesaj içeriğine `image_url` parçası olarak eklenir.</summary>
    public bool SupportsImages => true;

    public async Task<AiCompletionResult> CompleteAsync(AiCompletionRequest request, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("OpenAI API anahtarı tanımlı değil (Ai:OpenAI:ApiKey / OPENAI_API_KEY).");

        // Politika modeli (varsa) tek-atış yolunda da geçerli — ör. derin araştırma sentezi
        // gpt-5.6-terra ister, hızlı triyaj global gpt-5-mini'de kalır.
        var model = request.Model ?? _options.OpenAI.Model;
        var systemText = AiAdapterHelpers.ComposeSystemPrompt(request);

        var messages = new List<object>();
        if (!string.IsNullOrWhiteSpace(systemText))
            messages.Add(new { role = "system", content = systemText });

        if (request.Images is { Count: > 0 })
        {
            // Çok-modlu istekte user content bir DİZİ olur: önce metin, sonra görseller.
            // düşük çözünürlükte okunmuyor; bu iş zaten görüntü başına birkaç sent.
            var parts = new List<object> { new { type = "text", text = request.UserPrompt } };
            parts.AddRange(request.Images.Select(image => (object)new
            {
                type = "image_url",
                image_url = new { url = image, detail = "high" }
            }));
            messages.Add(new { role = "user", content = parts });
        }
        else
        {
            messages.Add(new { role = "user", content = request.UserPrompt });
        }

        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = messages,
            // Reasoning'i kıs → hızlı (25s timeout + prod 30s API-GW altında) + ucuz; grounded iş
            // reasoning gerektirmez. Değer MODELE GÖRE: `minimal` yeni kuşakta 400 veriyor (bkz. helper).
            ["reasoning_effort"] = AiAdapterHelpers.ResolveReasoningEffort(model),
        };
        if (request.MaxOutputTokens is int maxTokens)
            body["max_completion_tokens"] = maxTokens;   // GPT-5/o-serisi: max_tokens DEĞİL
        if (!string.IsNullOrWhiteSpace(request.ResponseJsonSchema))
            body["response_format"] = new { type = "json_object" };   // prompt "JSON" içermeli (OpenAI şartı)

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey!); // IsConfigured guard ile non-null garanti
        httpRequest.Content = new StringContent(
            JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("OpenAI çağrısı başarısız ({Status}): {Body}",
                (int)response.StatusCode, AiAdapterHelpers.Truncate(json));
            throw new HttpRequestException($"OpenAI {(int)response.StatusCode}: {AiAdapterHelpers.Truncate(json)}");
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            throw new HttpRequestException($"OpenAI boş yanıt döndürdü: {AiAdapterHelpers.Truncate(json)}");

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
            Provider = AiProvider.OpenAI,
            Model = model,
            Usage = new AiUsage { InputTokens = inputTokens, OutputTokens = outputTokens },
        };
    }

    /// <summary>Tool-calling çok-turlu sohbet (support chatbot). Aynı endpoint/şema; tools + tool_calls eklenir.</summary>
    public async Task<AiChatResult> CompleteChatAsync(AiChatRequest request, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("OpenAI API anahtarı tanımlı değil (Ai:OpenAI:ApiKey / OPENAI_API_KEY).");

        var model = request.Model ?? _options.OpenAI.Model;   // policy override (ör. admin → gpt-5)
        var hasTools = request.Tools is { Count: > 0 };
        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = AiAdapterHelpers.BuildChatMessages(request.Messages),
            // Araç varlığı değeri etkiler — yeni kuşak, tool'larla reasoning kabul etmiyor (bkz. helper).
            ["reasoning_effort"] = AiAdapterHelpers.ResolveReasoningEffort(model, hasTools),
        };
        if (request.MaxOutputTokens is int maxTokens)
            body["max_completion_tokens"] = maxTokens;   // GPT-5/o-serisi: max_tokens DEĞİL
        if (hasTools)
        {
            body["tools"] = AiAdapterHelpers.BuildToolsArray(request.Tools!);
            body["tool_choice"] = "auto";
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey!);
        httpRequest.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("OpenAI chat çağrısı başarısız ({Status}): {Body}", (int)response.StatusCode, AiAdapterHelpers.Truncate(json));
            throw new HttpRequestException($"OpenAI {(int)response.StatusCode}: {AiAdapterHelpers.Truncate(json)}");
        }
        return AiAdapterHelpers.ParseChatResult(json, AiProvider.OpenAI, model);
    }
}
