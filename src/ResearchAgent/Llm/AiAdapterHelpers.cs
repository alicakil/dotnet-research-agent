using System.Text;

namespace WebApi.Services.Ai;

/// <summary>Sağlayıcı adapter'ları arasında paylaşılan küçük yardımcılar (kod tekrarını önler).</summary>
internal static class AiAdapterHelpers
{
    /// <summary>
    /// Etkin sistem promptunu oluşturur. <see cref="AiCompletionRequest.ResponseJsonSchema"/> doluysa,
    /// sağlayıcıdan bağımsız olarak "yalnızca bu şemaya uygun JSON döndür" talimatını ekler — adapter
    /// ayrıca sağlayıcının JSON modunu da açar. Şema yoksa sistem promptu olduğu gibi döner.
    /// </summary>
    public static string ComposeSystemPrompt(AiCompletionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ResponseJsonSchema))
            return request.SystemPrompt ?? string.Empty;

        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
            sb.AppendLine(request.SystemPrompt).AppendLine();
        sb.AppendLine("Yalnızca aşağıdaki JSON şemasına uygun, geçerli tek bir JSON nesnesiyle yanıt ver. Şema dışında hiçbir açıklama, metin veya markdown ekleme:");
        sb.Append(request.ResponseJsonSchema);
        return sb.ToString();
    }

    /// <summary>Hata loglarında ham gövdeyi kısaltır.</summary>
    public static string Truncate(string value, int max = 500) =>
        value.Length <= max ? value : value[..max];
}
