using MarketEye.Domain.Screening;

namespace MarketEye.Application.Ai;

/// <summary>
/// Turns a user's prose into a <see cref="ParsedIntent"/> (PLAN.md §5.4).
///
/// The seam that makes §2's central claim true — "the model can be swapped, removed, or fail
/// entirely and the system below it still works". Everything load-bearing lives behind this
/// interface: resolution, validation and compilation never learn which provider answered, or
/// whether one answered at all.
///
/// Lives in Application, not Ai, for the same reason IMarketDataProvider does: Infrastructure must
/// be able to orchestrate a parse without referencing the project that talks to the model.
/// </summary>
public interface IIntentParser
{
    /// <summary>A short name for logs and the interpretation panel, e.g. "nvidia:llama-3.3-70b".</summary>
    string Describe { get; }

    /// <summary>
    /// False for a parser that never calls an external model (the keyword-match stub). §5.4's
    /// daily budget exists to protect a paid or rate-limited call; charging it against a free
    /// local fallback would let the stub silently disable itself once the counter fills up.
    /// </summary>
    bool ConsumesBudget => true;

    Task<ParseOutcome> ParseAsync(string prompt, CancellationToken ct);
}

/// <summary>
/// What a parse attempt produced.
///
/// Note there is no "clarification" case here: a clarifying question is a SUCCESSFUL parse whose
/// intent carries <see cref="ParsedIntent.Clarification"/>. Modelling it twice would create two
/// ways to say the same thing and, sooner or later, a path that handles one and not the other.
/// </summary>
public abstract record ParseOutcome
{
    private ParseOutcome() { }

    /// <summary>The model answered. The intent may still be a clarifying question.</summary>
    public sealed record Parsed(ParsedIntent Intent) : ParseOutcome;

    /// <summary>
    /// No answer available — no key configured, budget exhausted, provider down, response
    /// unreadable. §5.6 forbids degrading to a guessed screen, so callers surface this as
    /// "parsing is unavailable" and leave the manual screener working.
    /// </summary>
    public sealed record Unavailable(string Reason) : ParseOutcome;
}
