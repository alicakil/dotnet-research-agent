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
    /// <summary>Araçlı + muhakemeli yüzeylerin yolu; policy'de <c>UseResponsesApi</c> ile seçilir.</summary>
    private const string ResponsesEndpoint = "https://api.openai.com/v1/responses";

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
            ["reasoning_effort"] = request.ReasoningEffort ?? AiAdapterHelpers.ResolveReasoningEffort(model),
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

        // Akıl yürütme token'ları `max_completion_tokens` tavanından harcanır. Efor yükseltilip
        // tavan yükseltilmezse model düşünmeye tüm bütçeyi yer ve içerik BOŞ döner — HTTP 200,
        // finish_reason "length", çağıran taraf ise "çıktı okunamadı" der ve sebep görünmez.
        var finishReason = choices[0].TryGetProperty("finish_reason", out var fr) ? fr.GetString() : null;
        if (string.IsNullOrEmpty(text) && finishReason == "length")
        {
            throw new HttpRequestException(
                $"OpenAI çıktı token tavanına takıldı ve içerik üretemedi (model={model}, " +
                $"tavan={request.MaxOutputTokens}, efor={request.ReasoningEffort ?? "varsayılan"}). " +
                "Akıl yürütme bütçeyi tüketmiş olabilir — MaxOutputTokens'ı yükseltin ya da eforu düşürün.");
        }

        return new AiCompletionResult
        {
            Text = text,
            Provider = AiProvider.OpenAI,
            Model = model,
            Usage = AiAdapterHelpers.ParseUsage(root, "prompt_tokens", "completion_tokens", "prompt_tokens_details"),
        };
    }

    /// <summary>Tool-calling çok-turlu sohbet (support chatbot). Aynı endpoint/şema; tools + tool_calls eklenir.</summary>
    public async Task<AiChatResult> CompleteChatAsync(AiChatRequest request, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("OpenAI API anahtarı tanımlı değil (Ai:OpenAI:ApiKey / OPENAI_API_KEY).");

        if (request.UseResponsesApi)
            return await CompleteViaResponsesAsync(request, cancellationToken);

        var model = request.Model ?? _options.OpenAI.Model;   // policy override (ör. admin → gpt-5)
        var hasTools = request.Tools is { Count: > 0 };
        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = AiAdapterHelpers.BuildChatMessages(request.Messages),
            // Policy override öncelikli (v6.8: public chat "medium" ister); yoksa modele göre
            // varsayılan. Araç varlığı değeri etkiler — yeni kuşak, tool'larla reasoning kabul
            // etmiyor (bkz. helper), o yüzden tool varken override yok sayılır.
            ["reasoning_effort"] = hasTools
                ? AiAdapterHelpers.ResolveReasoningEffort(model, hasTools: true)
                : request.ReasoningEffort ?? AiAdapterHelpers.ResolveReasoningEffort(model),
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

    /// <summary>
    /// Aynı sözleşmenin <c>/v1/responses</c> üzerinden koşan hâli. Çağıran (runner) bu yolu bilmez —
    /// mesaj listesi verir, sonuç alır; tek fark dönen <see cref="AiChatResult.ResponseId"/>.
    ///
    /// İki kazanç: (1) araçlarla birlikte reasoning gönderilebiliyor — chat/completions'ta gpt-5.6
    /// bunu 400 ile reddediyor; (2) <c>previous_response_id</c> ile geçmiş OpenAI'da kalıyor, model
    /// araç turları arasında kendi planını yeniden kurmuyor.
    ///
    /// ⚠️ Zincir GEÇMİŞİ taşır ama <c>instructions</c>'ı TAŞIMAZ — sistem promptu her turda
    /// yeniden gönderilir (aşağıda, gerekçesiyle).
    ///
    /// ⚠️ Zincir <c>store</c>'a bağlı: <c>store:false</c> ile ikinci tur 400 "not found" veriyor
    /// (ölçüldü). Yani zincir açıkken istek/yanıt OpenAI'da 30 gün duruyor — bilinçli karar,
    /// gerekirse <c>DELETE /v1/responses/{id}</c> ile erken silinebilir.
    /// </summary>
    private async Task<AiChatResult> CompleteViaResponsesAsync(AiChatRequest request, CancellationToken cancellationToken)
    {
        var model = request.Model ?? _options.OpenAI.Model;
        var hasTools = request.Tools is { Count: > 0 };

        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            // Zincir varsa YALNIZ yeni kalemler gider (araç sonuçları); yoksa konuşmanın tamamı.
            ["input"] = BuildResponsesInput(request),
        };

        // System prompt Responses'ta ayrı bir alan — ve zincirin HER turunda tekrar gönderilir.
        // ⚠️ Sezgiye ters ama OpenAI'ın sözleşmesi bu: `previous_response_id` ile `instructions`
        // DEVRALINMAZ (kasıtlı — sistem mesajını turlar arası değiştirebilesin diye). Eskiden
        // burada "önceki yanıt onu zaten taşıyor" varsayımıyla atlanıyordu ve sonuç şuydu: araç
        // turundan sonraki cevap KURALSIZ üretiliyordu. 29 Ağu 2026'da support eval'inde ölçüldü —
        // 2. turun girdisi 6.077 → 460 token'a düşüyor (prompt yok) ve model tam o turda ekran
        // görüntüsü istiyor, [[action:...]] işaretini düşürüyor, ekran tarifi veriyor; yani promptun
        // yasakladığı üç davranış birden geri geliyor. Tekrarın bedeli neredeyse sıfır: aynı önek
        // olduğu için önbellekten geliyor.
        var system = request.Messages.FirstOrDefault(m => m.Role == "system")?.Content;
        if (!string.IsNullOrWhiteSpace(system)) body["instructions"] = system;

        if (request.PreviousResponseId is not null)
            body["previous_response_id"] = request.PreviousResponseId;

        if (request.MaxOutputTokens is int maxTokens) body["max_output_tokens"] = maxTokens;
        // Değer OLDUĞU GİBİ gider: bu API'nin varlık sebebi zaten araçla birlikte muhakemeye izin
        // vermesi. "minimal" yalnız eski kuşakta geçerli, burada kullanılmaz.
        if (!string.IsNullOrWhiteSpace(request.ReasoningEffort))
            body["reasoning"] = new { effort = request.ReasoningEffort };
        if (hasTools)
        {
            body["tools"] = BuildResponsesTools(request.Tools!);
            body["tool_choice"] = "auto";
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, ResponsesEndpoint);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey!);
        httpRequest.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("OpenAI responses çağrısı başarısız ({Status}): {Body}", (int)response.StatusCode, AiAdapterHelpers.Truncate(json));
            throw new HttpRequestException($"OpenAI {(int)response.StatusCode}: {AiAdapterHelpers.Truncate(json)}");
        }
        return ParseResponsesResult(json, model);
    }

    /// <summary>Responses şemasında araç tanımı: function alanları düz, iç içe değil.</summary>
    private static List<object> BuildResponsesTools(IReadOnlyList<AiToolDefinition> tools) =>
        tools.Select(t => (object)new
        {
            type = "function",
            name = t.Name,
            description = t.Description,
            parameters = JsonSerializer.Deserialize<JsonElement>(t.ParametersJsonSchema),
        }).ToList();

    /// <summary>
    /// Mesaj listesi → Responses "input" kalemleri. Zincir varsa yalnız son turda eklenen araç
    /// sonuçları gönderilir; geçmişi tekrar göndermek hem token israfı hem de "aynı şey iki kez
    /// anlatıldı" hâli demek.
    /// </summary>
    private static List<object> BuildResponsesInput(AiChatRequest request)
    {
        var items = new List<object>();

        if (request.PreviousResponseId is not null)
        {
            foreach (var m in request.Messages.Where(m => m.Role == "tool").TakeLast(CountTrailingToolMessages(request.Messages)))
                items.Add(new { type = "function_call_output", call_id = m.ToolCallId, output = m.Content ?? string.Empty });
            return items;
        }

        foreach (var m in request.Messages)
        {
            switch (m.Role)
            {
                case "system":
                    break;   // instructions alanına gitti
                case "tool":
                    items.Add(new { type = "function_call_output", call_id = m.ToolCallId, output = m.Content ?? string.Empty });
                    break;
                case "assistant" when m.ToolCalls is { Count: > 0 }:
                    // Zincirsiz yeniden oynatma (ör. Groq'a düşüp geri dönme): modelin geçmiş araç
                    // taleplerini de kaleme dökmek gerekir, yoksa sonuçlar sahipsiz kalır.
                    foreach (var call in m.ToolCalls)
                        items.Add(new { type = "function_call", call_id = call.Id, name = call.Name, arguments = call.ArgumentsJson });
                    if (!string.IsNullOrWhiteSpace(m.Content))
                        items.Add(new { role = "assistant", content = m.Content });
                    break;
                default:
                    if (!string.IsNullOrWhiteSpace(m.Content))
                        items.Add(new { role = m.Role, content = m.Content });
                    break;
            }
        }
        return items;
    }

    /// <summary>Listenin sonundaki kesintisiz araç mesajı sayısı = bu turda eklenen sonuçlar.</summary>
    private static int CountTrailingToolMessages(IReadOnlyList<AiChatMessage> messages)
    {
        var n = 0;
        for (var i = messages.Count - 1; i >= 0 && messages[i].Role == "tool"; i--) n++;
        return n;
    }

    /// <summary>Responses yanıtı: output dizisi (reasoning · function_call · message) + usage.</summary>
    private static AiChatResult ParseResponsesResult(string json, string model)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var text = new StringBuilder();
        var toolCalls = new List<AiToolCall>();

        if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in output.EnumerateArray())
            {
                var type = item.TryGetProperty("type", out var t) ? t.GetString() : null;
                switch (type)
                {
                    case "function_call":
                        toolCalls.Add(new AiToolCall
                        {
                            Id = item.TryGetProperty("call_id", out var cid) ? cid.GetString() ?? string.Empty : string.Empty,
                            Name = item.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty,
                            ArgumentsJson = item.TryGetProperty("arguments", out var a) ? a.GetString() ?? "{}" : "{}",
                        });
                        break;
                    case "message":
                        if (item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                            foreach (var part in content.EnumerateArray())
                                if (part.TryGetProperty("text", out var pt)) text.Append(pt.GetString());
                        break;
                    // "reasoning" kalemi bilerek okunmuyor: içeriği bize ait değil, zincirde
                    // OpenAI tarafında taşınıyor. Loglamak da istemeyiz.
                }
            }
        }


        return new AiChatResult
        {
            Text = text.Length > 0 ? text.ToString() : null,
            ToolCalls = toolCalls,
            Provider = AiProvider.OpenAI,
            Model = model,
            Usage = AiAdapterHelpers.ParseUsage(root, "input_tokens", "output_tokens", "input_tokens_details"),
            ResponseId = root.TryGetProperty("id", out var id) ? id.GetString() : null,
        };
    }
}
