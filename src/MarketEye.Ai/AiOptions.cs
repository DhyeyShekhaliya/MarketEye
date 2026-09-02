namespace MarketEye.Ai;

/// <summary>
/// Configuration for the intent parser (PLAN.md §3, §5.4).
///
/// Provider is NVIDIA NIM. The other values stay in <see cref="AiProvider"/> as config-only escape
/// hatches: NIM's free allotment is finite, and switching to another OpenAI-compatible endpoint
/// should be a setting rather than a code change.
/// </summary>
public sealed class AiOptions
{
    public const string SectionName = "Ai";

    public AiProvider Provider { get; set; } = AiProvider.NvidiaNim;

    /// <summary>OpenAI-compatible base URL. NIM's is https://integrate.api.nvidia.com/v1.</summary>
    public string Endpoint { get; set; } = "https://integrate.api.nvidia.com/v1";

    /// <summary>
    /// Empty means the app runs without AI: StubIntentParser is registered instead and the manual
    /// screener keeps working. Same posture as the missing-connection-string handling in
    /// InfrastructureServiceCollectionExtensions -- warn, do not crash.
    ///
    /// Local: dotnet user-secrets set "Ai:ApiKey" "nvapi-..." --project src/MarketEye.Api
    /// </summary>
    public string ApiKey { get; set; } = "";

    /// <summary>
    /// Verified against a live account on 2026-09-02: it correctly honours strict
    /// `response_format` JSON schema, asks a clarifying question rather than guessing when a
    /// request is ambiguous, and refuses a prompt-injection attempt by writing the refusal into
    /// `clarification` rather than breaking schema conformance.
    ///
    /// Two models tried and rejected: meta/llama-3.2-11b-vision-instruct never produced
    /// schema-conformant JSON under `response_format`; nvidia/nemotron-3-super-120b-a12b worked
    /// but returned intermittent 503s on this account and once misread "5000 crore" as the literal
    /// number 5000 rather than converting the unit -- see SystemPrompt's crore guidance, added for
    /// the same reason. Neither model accepted `nvext.guided_json`: NIM's account-listed models do
    /// not uniformly support it, so `response_format` is the safer default across models generally,
    /// not only for this one.
    /// </summary>
    public string Model { get; set; } = "openai/gpt-oss-20b";

    /// <summary>
    /// The reply is a short JSON object naming a handful of concepts. A generous cap here buys
    /// nothing and turns a runaway generation into a bill.
    /// </summary>
    public int MaxOutputTokens { get; set; } = 800;

    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Hard ceiling on model calls per day, enforced in the database via RequestBudget so it
    /// survives the process restarting (§5.4). Without authentication this -- not the per-IP rate
    /// limit -- is the real protection for a finite credit allotment.
    /// </summary>
    public int DailyCallCap { get; set; } = 200;

    /// <summary>
    /// How this model is told to obey the schema. NIM models expose one mechanism or the other and
    /// it varies by model, so it is configuration rather than a compile-time assumption.
    /// </summary>
    public StructuredOutputMode StructuredOutput { get; set; } = StructuredOutputMode.ResponseFormatJsonSchema;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);
}

public enum AiProvider
{
    NvidiaNim = 0,
    OpenAi = 1,
    AzureOpenAi = 2,
    GitHubModels = 3,
    Ollama = 4,
}

/// <summary>
/// The two ways a provider accepts a JSON schema.
///
/// <see cref="ResponseFormatJsonSchema"/> is the OpenAI-standard `response_format`.
/// <see cref="NvextGuidedJson"/> is NVIDIA's `nvext.guided_json` extension — a non-standard body
/// field, which is why the client cannot always be the stock SDK.
///
/// A model that supports NEITHER must not be used: it would ignore the schema silently and return
/// prose that fails resolution, which looks like the AI hallucinating rather than a config error.
/// </summary>
public enum StructuredOutputMode
{
    ResponseFormatJsonSchema = 0,
    NvextGuidedJson = 1,
}
