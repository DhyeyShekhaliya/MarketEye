namespace MarketEye.AiEvals;

/// <summary>
/// The live tier's opt-in switch (PLAN.md §5.6, Step 9) -- the exact idiom
/// <c>tests/MarketEye.IntegrationTests/DockerGate.cs</c> uses for MARKETEYE_INTEGRATION, so a
/// reader who already knows that gate recognises this one immediately.
///
/// Off by default: the live tier spends real provider credits and wall-clock time (50 sequential
/// calls), so it must never run on a routine `dotnet test`/CI pass, only on request.
/// </summary>
public static class LiveEvalGate
{
    public static bool Enabled =>
        Environment.GetEnvironmentVariable("MARKETEYE_AI_EVALS") == "1";

    public static bool Recording =>
        Environment.GetEnvironmentVariable("MARKETEYE_AI_EVALS_RECORD") == "1";

    public const string SkipReason =
        "Live LLM eval. Set MARKETEYE_AI_EVALS=1 (and AI_API_KEY) to run.";
}
