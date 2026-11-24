namespace ResearchAgent.Llm;

/// <summary>
/// The single entry point application code uses for AI. The caller never knows which provider is
/// used; it only says "produce this".
///
/// Responsibilities (implemented by <see cref="AiRouter"/>):
///  1. Quota guard: may the current scope spend on this call?
///  2. Provider routing with fallback: pick providers in policy order, move on after an error.
///  3. Metering: record the real token use and cost of the call.
///
/// Identity (scope, user) is read from <see cref="AiTraceContext"/>, which the host fills from its
/// session; it is never part of the request (see the security note on <see cref="AiCompletionRequest"/>).
/// </summary>
public interface IAiCompletionService
{
    Task<AiCompletionResult> CompleteAsync(AiCompletionRequest request, CancellationToken cancellationToken = default);
}
