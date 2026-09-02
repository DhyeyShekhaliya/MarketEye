using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MarketEye.AiEvals;

/// <summary>
/// Loads the 50-case suite and locates each case's recording (PLAN.md §5.6).
///
/// Cases ship as content-copied JSON (see the .csproj), so they read from beside the built test
/// assembly the same way in a local run and in CI -- no working-directory assumptions.
/// </summary>
public static class EvalCases
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<EvalCase> LoadAll()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "evals", "cases.json");
        var cases = JsonSerializer.Deserialize<List<EvalCase>>(File.ReadAllText(path), Options)
            ?? throw new InvalidOperationException($"{path} deserialised to null.");

        var duplicateIds = cases.GroupBy(c => c.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicateIds.Count > 0)
        {
            throw new InvalidOperationException(
                $"Duplicate case ids in cases.json: {string.Join(", ", duplicateIds)}.");
        }

        return cases;
    }

    /// <summary>
    /// Where a case's recorded model response lives, keyed by the prompt text exactly as written
    /// in cases.json -- matching PLAN.md §5.6's <c>evals/recorded/{sha256(prompt)}.json</c>.
    /// </summary>
    public static string RecordingPath(string prompt)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(prompt));
        var name = Convert.ToHexString(hash).ToLowerInvariant();
        return Path.Combine(AppContext.BaseDirectory, "evals", "recorded", $"{name}.json");
    }

    /// <summary>
    /// The SOURCE path (not the build output copy) recordings are written to when recording. The
    /// build-output copy only refreshes on the next build, so a record-then-replay in one process
    /// run must write here for the replay half of that same run to see it.
    /// </summary>
    public static string SourceRecordingPath(string prompt)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(prompt));
        var name = Convert.ToHexString(hash).ToLowerInvariant();
        return Path.Combine(SourceEvalsRoot(), "recorded", $"{name}.json");
    }

    private static string SourceEvalsRoot()
    {
        // AppContext.BaseDirectory is .../MarketEye.AiEvals/bin/{Config}/net10.0/. Three hops up
        // (net10.0 -> Config -> bin) lands on the project root, where evals/ actually lives.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 3 && dir.Parent is not null; i++) dir = dir.Parent;
        return Path.Combine(dir.FullName, "evals");
    }
}
