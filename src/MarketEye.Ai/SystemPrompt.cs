using System.Text;
using MarketEye.Domain.Screening.Vocabulary;

namespace MarketEye.Ai;

/// <summary>
/// Builds the system prompt from the live vocabulary (PLAN.md §5.1, §5.2).
///
/// Generated rather than written out, for the same reason the schema is: the vocabulary is
/// user-editable, so any hand-maintained copy of it in a prompt would drift the first time someone
/// added a concept — and the model would then be told about a concept the resolver rejects, or
/// kept ignorant of one it should use.
///
/// The prompt states §5.1's rule, but note it is not what ENFORCES it. The schema makes an unknown
/// concept unemittable and the resolver fails closed regardless. The prompt is there to improve
/// the model's choices, not to be trusted with correctness.
/// </summary>
public static class SystemPrompt
{
    public static string Build(
        IStrategyConceptVocabulary strategies, IMetricConceptVocabulary metrics)
    {
        var sb = new StringBuilder();

        sb.AppendLine(
            "You translate a stock-screening request written in plain English into a structured " +
            "intent. You do not screen stocks, give advice, or invent numbers.");
        sb.AppendLine();

        sb.AppendLine("RULES");
        sb.AppendLine(
            "1. Name the CONCEPTS that apply, from the vocabulary below. A concept carries its own " +
            "thresholds, defined by the user -- you never supply them.");
        sb.AppendLine(
            "2. Put a number in explicit_filters ONLY when the user stated that number themselves. " +
            "\"cheap\" is a concept and gets no number. \"P/E below 12\" is an explicit filter.");
        sb.AppendLine(
            "3. If the request is too vague or ambiguous to map onto these concepts, set " +
            "clarification to a single short question and leave the other fields empty. Asking is " +
            "always better than guessing.");
        sb.AppendLine(
            "4. Use only the concept and metric names listed. Nothing else exists.");
        sb.AppendLine(
            "5. MarketCap is measured in INR CRORE, not raw rupees -- the same unit Indian " +
            "companies report financial results in. \"5000 crore\" is the value 5000. \"1 lakh " +
            "crore\" is 100000 (1 lakh = 100,000, and 1 lakh crore = 100,000 crore). If a request " +
            "gives a plain rupee figure with no lakh or crore, divide by 1,00,00,000 to get the " +
            "crore value before writing it. If unsure how to convert what was stated, ask via " +
            "clarification rather than guessing the unit.");
        sb.AppendLine();

        sb.AppendLine("STRATEGY CONCEPTS");
        foreach (var c in strategies.Enabled.OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            sb.Append("- ").Append(c.Name);
            if (c.Aliases.Count > 0)
            {
                sb.Append(" (also: ").Append(string.Join(", ", c.Aliases)).Append(')');
            }
            if (!string.IsNullOrWhiteSpace(c.Description))
            {
                sb.Append(" — ").Append(c.Description);
            }
            sb.AppendLine();
        }
        sb.AppendLine();

        sb.AppendLine("METRICS (for explicit_filters only, when the user gave the number)");
        foreach (var m in metrics.All.OrderBy(m => m.Name, StringComparer.Ordinal))
        {
            sb.Append("- ").Append(m.Name).Append(" — ").Append(m.DisplayName);
            if (!string.IsNullOrWhiteSpace(m.Unit)) sb.Append(" (").Append(m.Unit).Append(')');
            sb.Append(", valid range ").Append(Trim(m.MinValue)).Append(" to ").Append(Trim(m.MaxValue));
            sb.AppendLine();
        }
        sb.AppendLine();

        // A §12 non-negotiable: this line appears in every system prompt and on every results view.
        sb.AppendLine(
            "This tool is for educational purposes only and does not give investment advice. " +
            "Never present a screen as a recommendation to buy or sell.");

        return sb.ToString();
    }

    private static string Trim(decimal value) =>
        value.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
}
