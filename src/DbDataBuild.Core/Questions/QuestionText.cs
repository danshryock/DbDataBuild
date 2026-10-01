using System.Text;

namespace DbDataBuild.Core.Questions;

/// <summary>Plain-text rendering of a question, shared by the interactive prompt and the non-interactive "unanswered" report.</summary>
public static class QuestionText
{
    public static string Describe(Question q)
    {
        var sb = new StringBuilder();
        sb.AppendLine(q.Id).Append("  ").AppendLine(q.Prompt);
        if (q.Context.Count > 0)
        {
            sb.AppendLine("  Context:");
            foreach (var c in q.Context) sb.Append("    - ").AppendLine(c);
        }
        sb.AppendLine("  Options:");
        foreach (var (o, i) in q.Options.Select((o, i) => (o, i)))
        {
            sb.Append($"    {i + 1}. {o.Key}  {o.Description}");
            if (o.Consequence != null) sb.Append($" (consequence: {o.Consequence})");
            sb.AppendLine();
        }
        if (q.Proposal is { } p)
        {
            sb.Append($"  Inferred ({p.Certainty.ToString().ToLowerInvariant()} certainty): {p.OptionKey}{(p.Value != null ? $" = {p.Value}" : "")}").AppendLine();
            foreach (var e in p.Evidence) sb.Append("    evidence: ").AppendLine(e);
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>The exact YAML that would answer the question, one paste-ready alternative per option (plus accepting the inferred proposal).</summary>
    public static string AnswerTemplate(Question q)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Add one of these to the answers file:");
        foreach (var o in q.Options)
        {
            sb.AppendLine($"  - id: {q.Id}");
            sb.AppendLine($"    choice: {o.Key}");
            if (o.TakesValue) sb.AppendLine($"    value: <{o.ValueHint ?? "value"}>");
            sb.AppendLine($"    # {o.Description}");
        }
        if (q.Proposal != null)
        {
            sb.AppendLine($"  - id: {q.Id}");
            sb.AppendLine("    accept: inferred");
        }
        return sb.ToString().TrimEnd();
    }
}
