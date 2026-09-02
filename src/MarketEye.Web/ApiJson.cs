using System.Net.Http.Json;
using System.Text.Json;

namespace MarketEye.Web;

/// <summary>
/// The shared reader for the 400 ValidationProblem shape every rejection in the API uses
/// (Program.cs's ValidationProblem helper: <c>{"errors": {"path": ["message"]}}</c>).
///
/// Every page that writes to the API -- Screen.razor's manual builder, its interpretation panel,
/// its inline concept editor, and Vocabulary.razor's editor -- needs to turn a rejected response
/// into the same "path: message" list <c>ValidationErrors.razor</c> renders. One reader means
/// tightening that mapping once fixes it everywhere, rather than four copies drifting apart.
/// </summary>
public static class ApiJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private sealed record ProblemDto(Dictionary<string, string[]>? Errors);

    /// <summary>
    /// Reads a rejected response's validation errors. Never throws: a response that is not the
    /// expected shape becomes a single "error" entry carrying the raw body, because a malformed
    /// error response is still worth showing the user rather than swallowing.
    /// </summary>
    public static async Task<Dictionary<string, string[]>> ReadErrorsAsync(HttpResponseMessage response)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<ProblemDto>(Options);
            if (problem?.Errors is { Count: > 0 }) return problem.Errors;
        }
        catch
        {
            // Fall through: an unparseable body is still shown, just not itemised by path.
        }

        return new() { ["error"] = [await response.Content.ReadAsStringAsync()] };
    }
}
