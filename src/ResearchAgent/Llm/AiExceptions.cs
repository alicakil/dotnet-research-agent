namespace ResearchAgent.Llm;

/// <summary>Thrown by an <see cref="IAiUsageMeter"/> when the scope cannot pay for a call.</summary>
public sealed class AiQuotaExceededException : Exception
{
    public int RequiredCredits { get; }
    public int RemainingCredits { get; }

    public AiQuotaExceededException(int requiredCredits, int remainingCredits)
        : base($"Not enough AI credits: {requiredCredits} required, {remainingCredits} left.")
    {
        RequiredCredits = requiredCredits;
        RemainingCredits = remainingCredits;
    }
}

/// <summary>Thrown when every provider in the policy, the primary and its fallbacks, failed.</summary>
public sealed class AiCompletionFailedException : Exception
{
    public AiCompletionFailedException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
