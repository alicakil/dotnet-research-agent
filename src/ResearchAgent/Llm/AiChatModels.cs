namespace WebApi.Services.Ai;

// ── Tool-calling (agentic) sohbet modelleri ──
// Mevcut single-shot AiCompletionRequest/Result'tan AYRI; çok-turlu + araç (function) destekler.
// KİMLİK İÇERMEZ (single-shot ile aynı güvenlik invaryantı: "ne" prompt'tan, "kim" oturumdan).

/// <summary>Tool-calling sohbetinde tek bir mesaj (system/user/assistant/tool).</summary>
public sealed class AiChatMessage
{
    /// <summary>"system" | "user" | "assistant" | "tool".</summary>
    public required string Role { get; init; }

    /// <summary>Metin içerik (assistant araç çağırıyorsa null olabilir).</summary>
    public string? Content { get; init; }

    /// <summary>assistant'ın araç çağrı talepleri (varsa).</summary>
    public IReadOnlyList<AiToolCall>? ToolCalls { get; init; }

    /// <summary>role="tool" için: hangi araç çağrısına (Id) yanıt olduğu.</summary>
    public string? ToolCallId { get; init; }

    /// <summary>
    /// İSTEMCİDEN gelen sohbet mesajı → modele gidecek mesaj. İstemci konuşmanın yalnız iki tarafını
    /// yollayabilir: kendi yazdığı ("user") ve asistanın önceki cevabı (panelin adıyla "ai";
    /// "assistant" da kabul). Büyük-küçük harfe bakmaz. Başka her rol ve boş içerik null döner:
    /// çağıran mesajı atar, isteği reddetmez.
    ///
    /// Sohbet uçları mesajı YALNIZ buradan kurar. Rol aynen taşınırken anonim ziyaretçi "developer"
    /// ya da "system" rolüyle talimat düzeyinde mesaj yollayabiliyordu; "tool" rolü de modele
    /// sahipsiz bir araç sonucu olarak gidiyordu.
    /// </summary>
    public static AiChatMessage? FromClient(string? role, string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;

        var modelRole = role?.ToLowerInvariant() switch
        {
            "user" => "user",
            "ai" or "assistant" => "assistant",
            _ => null,
        };
        return modelRole is null ? null : new AiChatMessage { Role = modelRole, Content = content };
    }
}

/// <summary>Modelin bir aracı (function) çağırma talebi.</summary>
public sealed class AiToolCall
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>Modelin verdiği argümanlar (ham JSON string).</summary>
    public required string ArgumentsJson { get; init; }
}

/// <summary>Modele sunulan araç tanımı (function schema).</summary>
public sealed class AiToolDefinition
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    /// <summary>JSON Schema (object). Parametresiz araç: {"type":"object","properties":{}}.</summary>
    public required string ParametersJsonSchema { get; init; }
}

/// <summary>Tool-calling sohbet isteği (multi-turn + araçlar). KİMLİK YOK.</summary>
public sealed class AiChatRequest
{
    public required AiTaskType TaskType { get; init; }
    public required IReadOnlyList<AiChatMessage> Messages { get; init; }
    public IReadOnlyList<AiToolDefinition>? Tools { get; init; }
    public int? MaxOutputTokens { get; init; }
    /// <summary>OpenAI model override (policy'den; null → adapter varsayılanı).</summary>
    public string? Model { get; init; }

    /// <summary>
    /// reasoning_effort override (policy'den; null → modele göre varsayılan, bkz.
    /// <see cref="AiAdapterHelpers.ResolveReasoningEffort"/>).
    ///
    /// v6.8'e kadar bunu YALNIZCA tek-atışlık <c>CompleteAsync</c> okuyordu; sohbet yüzeyleri
    /// (public / support / admin chat) policy'de ne yazarsa yazsın sessizce modelin varsayılanına
    /// düşüyordu. gpt-5-mini için o varsayılan "minimal" — yani satış asistanımız fiilen akıl
    /// yürütmeden konuşuyordu ve zor sorularda düşünme bütçesini yiyip BOŞ cevap dönüyordu.
    /// </summary>
    public string? ReasoningEffort { get; init; }

    /// <summary>
    /// OpenAI çağrısı <c>/v1/responses</c> üzerinden yapılsın mı (policy'den gelir). Diğer
    /// sağlayıcılar bunu yok sayar — onlarda tek şema var.
    /// </summary>
    public bool UseResponsesApi { get; init; }

    /// <summary>
    /// Responses zincirinin bir önceki adımı. Doluysa adapter yalnız YENİ kalemleri (araç
    /// sonuçlarını) gönderir; geçmiş — modelin kendi muhakeme kalemi dahil — OpenAI tarafında durur.
    ///
    /// Muhakeme kalemini taşımanın anlamı: model ilk turda "önce fiyat aracını çağırayım" diye bir
    /// plan kuruyor. O plan taşınmazsa ikinci turda eline dönen araç verisi bağlamsız kalır ve model
    /// baştan düşünmeye başlar — daha yavaş, daha pahalı ve tur turdan bağımsız olduğu için daha
    /// tutarsız. Zincirle ikinci turda muhakeme token'ı 0 ölçüldü.
    ///
    /// Null ise tam mesaj listesi gönderilir — zincir bir hızlandırma,
    /// bir bağımlılık değil.
    /// </summary>
    public string? PreviousResponseId { get; init; }
}

/// <summary>Tool-calling sonucu: ya final <see cref="Text"/>, ya da <see cref="ToolCalls"/> (araç talebi).</summary>
public sealed class AiChatResult
{
    public string? Text { get; init; }
    public IReadOnlyList<AiToolCall> ToolCalls { get; init; } = Array.Empty<AiToolCall>();
    public required AiProvider Provider { get; init; }
    public required string Model { get; init; }
    public required AiUsage Usage { get; init; }

    /// <summary>
    /// Responses yolunda bu turun id'si; runner bir sonraki tura <c>PreviousResponseId</c> olarak
    /// verir. chat/completions yolunda null — runner o zaman tam mesaj listesiyle devam eder.
    /// </summary>
    public string? ResponseId { get; init; }
}
