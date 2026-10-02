using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DbDataBuild.Core;
using DbDataBuild.Core.Questions;
using DbDataBuild.Models.Yaml;
using DbDataBuild.State;

namespace DbDataBuild.Planning;

/// <summary>
/// The plan as a file (DESIGN.md 10.2, 10.3): one machine-readable YAML that `apply` consumes, and a readable Markdown rendering of the same object.
/// Every string is written as a JSON-quoted scalar (valid YAML), so scripts with quotes, newlines, backslashes or non-ASCII text survive exactly.
/// The file carries a content hash over everything else; `Parse` refuses a file whose hash does not match.
/// </summary>
public static class PlanDocument
{
    private const string HashPrefix = "  hash: ";
    private static readonly JsonSerializerOptions Quote = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static string Q(string s) => JsonSerializer.Serialize(s, Quote);
    private static string B(bool b) => b ? "true" : "false";
    private static string Snake(Enum e) => string.Concat(e.ToString().Select((c, i) => char.IsUpper(c) && i > 0 ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));

    /// <summary>A short id: the date and the first characters of a hash over the plan's content (excluding its own id, commit and hash).</summary>
    public static string CreateId(DateOnly date, Plan plan)
    {
        var body = Body(plan with { Id = "", GitCommit = null, GitDirty = false });
        return $"{date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}-{Hashing.Sha256Hex(body)[..8]}";
    }

    public static string Serialize(Plan plan)
    {
        var body = Body(plan);
        return "plan:\n" + HashPrefix + Hashing.Sha256Hex(body) + "\n" + body;
    }

    // everything after the `plan:` header's hash line; the hash is over exactly this text
    private static string Body(Plan p)
    {
        var sb = new StringBuilder();
        sb.Append("  id: ").AppendLineLf(Q(p.Id));
        sb.Append("  target: ").AppendLineLf(Q(p.Target));
        sb.Append("  tool_version: ").AppendLineLf(Q(p.ToolVersion));
        if (p.GitCommit != null) sb.Append("  git_commit: ").AppendLineLf(Q(p.GitCommit));
        sb.Append("  git_dirty: ").AppendLineLf(B(p.GitDirty));
        if (p.Bases.Count > 0) sb.AppendLineLf("bases:");
        foreach (var b in p.Bases)
        {
            sb.Append("  - object: ").AppendLineLf(Q(b.Object));
            sb.Append("    state: ").AppendLineLf(Snake(b.State));
            if (b.LiveShapeHash != null) sb.Append("    live_shape_hash: ").AppendLineLf(Q(b.LiveShapeHash));
            if (b.RecordedShapeHash != null) sb.Append("    recorded_shape_hash: ").AppendLineLf(Q(b.RecordedShapeHash));
        }
        if (p.Answers.Count > 0)
        {
            sb.AppendLineLf("answers:");
            foreach (var a in p.Answers.OrderBy(a => a.QuestionId, StringComparer.Ordinal))
            {
                sb.Append("  - id: ").AppendLineLf(Q(a.QuestionId));
                sb.Append("    choice: ").AppendLineLf(Q(a.Choice));
                if (a.Value != null) sb.Append("    value: ").AppendLineLf(Q(a.Value));
                if (a.Note != null) sb.Append("    note: ").AppendLineLf(Q(a.Note));
                sb.Append("    source: ").AppendLineLf(Snake(a.Source));
            }
        }
        if (p.Steps.Count > 0) sb.AppendLineLf("steps:");
        foreach (var s in p.Steps)
        {
            sb.Append("  - id: ").AppendLineLf(Q(s.Id));
            sb.Append("    type: ").AppendLineLf(Snake(s.Type));
            sb.Append("    object: ").AppendLineLf(Q(s.Object));
            sb.Append("    description: ").AppendLineLf(Q(s.Description));
            sb.Append("    risk: ").AppendLineLf(Snake(s.Risk));
            sb.Append("    reasons: [").Append(string.Join(", ", s.Reasons.Select(Q))).AppendLineLf("]");
            if (s.HashAfter != null) sb.Append("    hash_after: ").AppendLineLf(Q(s.HashAfter));
            if (s.Operation != null) sb.Append("    operation: ").AppendLineLf(Q(s.Operation));
            if (s.FileHash != null) sb.Append("    file_hash: ").AppendLineLf(Q(s.FileHash));
            if (s.ShapeSource != null) sb.Append("    shape_source: ").AppendLineLf(Q(s.ShapeSource));
            if (s.DefinitionHash != null) sb.Append("    definition_hash: ").AppendLineLf(Q(s.DefinitionHash));
            if (s.Expect != null) sb.Append("    expect: ").AppendLineLf(Q(s.Expect));
            if (s.Hook != null) sb.Append("    hook: ").AppendLineLf(Q(s.Hook));
            if (s.Effect != null) sb.Append("    effect: ").AppendLineLf(Q(s.Effect));
            if (s.HasResolver)
            {
                sb.AppendLineLf("    resolver:");
                sb.Append("      text: ").AppendLineLf(Q(s.ResolverText!));
                if (s.ResolverResult != null) sb.Append("      result: ").AppendLineLf(Q(s.ResolverResult));
            }
            if (s.Parameters.Count > 0)
            {
                sb.AppendLineLf("    parameters:");
                foreach (var prm in s.Parameters)
                {
                    sb.Append("      - name: ").AppendLineLf(Q(prm.Name));
                    sb.Append("        type: ").AppendLineLf(Q(prm.Type));
                    sb.Append("        source: ").AppendLineLf(Q(prm.Source));
                    if (prm.Value != null) sb.Append("        value: ").AppendLineLf(Q(prm.Value));
                }
            }
            sb.Append("    text: ").AppendLineLf(Q(s.Text));
        }
        if (p.Noticed.Count > 0)
        {
            sb.AppendLineLf("noticed:");
            foreach (var n in p.Noticed) sb.Append("  - ").AppendLineLf(Q(n));
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------------------------------------------------------------------------

    private static readonly string[] TopKeys = ["plan", "bases", "answers", "steps", "noticed"];
    private static readonly string[] PlanKeys = ["id", "hash", "target", "tool_version", "git_commit", "git_dirty"];
    private static readonly string[] BaseKeys = ["object", "state", "live_shape_hash", "recorded_shape_hash"];
    private static readonly string[] AnswerKeys = ["id", "choice", "value", "note", "source"];
    private static readonly string[] StepKeys = ["id", "type", "object", "description", "risk", "reasons", "hash_after", "operation", "file_hash", "shape_source", "definition_hash", "expect", "hook", "effect", "resolver", "parameters", "text"];
    private static readonly string[] ParamKeys = ["name", "type", "source", "value"];

    /// <summary>Reads a plan file. Returns null with diagnostics when it is malformed, has unknown keys, or its content hash does not match.</summary>
    public static Plan? Parse(string text, string file, List<Diagnostic> diags)
    {
        var before = diags.Count;
        var root = StrictYamlReader.Read(text, file, diags);
        if (root == null || diags.Count > before) return null;
        void Bad(YamlNode at, string found) => diags.Add(new Diagnostic(DiagnosticCatalog.PlanFileInvalid, new(file, at.Line, at.Column), found));

        if (root is not YamlMapping top) { Bad(root, "A plan file must be a mapping."); return null; }
        foreach (var e in top.Entries.Where(e => !TopKeys.Contains(e.Key.Value))) Bad(e.Key, $"Unknown key `{e.Key.Value}`.");
        if (top.Get("plan") is not YamlMapping header) { Bad(top, "The `plan:` section is missing."); return null; }
        foreach (var e in header.Entries.Where(e => !PlanKeys.Contains(e.Key.Value))) Bad(e.Key, $"Unknown key `{e.Key.Value}` in `plan:`.");

        string? S(YamlMapping m, string k) => (m.Get(k) as YamlScalar)?.Value;
        string Req(YamlMapping m, string k)
        {
            var v = S(m, k);
            if (v == null) Bad(m, $"Required key `{k}` is missing.");
            return v ?? "";
        }
        bool Bool(YamlMapping m, string k) => S(m, k) is "true" ? true : S(m, k) is "false" or null ? false : Fail(m, k);
        bool Fail(YamlMapping m, string k) { Bad(m, $"`{k}` must be true or false."); return false; }
        T EnumOf<T>(YamlMapping m, string k) where T : struct, Enum
        {
            var v = S(m, k);
            foreach (var candidate in Enum.GetValues<T>()) if (Snake(candidate) == v) return candidate;
            Bad(m, $"`{k}` is `{v}`, which is not a known value.");
            return default;
        }
        void Keys(YamlMapping m, string[] allowed, string where)
        {
            foreach (var e in m.Entries.Where(e => !allowed.Contains(e.Key.Value))) Bad(e.Key, $"Unknown key `{e.Key.Value}` in {where}.");
        }

        var bases = new List<ObjectBase>();
        foreach (var n in (top.Get("bases") as YamlSequence)?.Items ?? [])
            if (n is YamlMapping m) { Keys(m, BaseKeys, "a base"); bases.Add(new(Req(m, "object"), EnumOf<ObjectState>(m, "state"), S(m, "live_shape_hash"), S(m, "recorded_shape_hash"))); }
            else Bad(n, "Each base must be a mapping.");

        var answers = new List<ResolvedAnswer>();
        foreach (var n in (top.Get("answers") as YamlSequence)?.Items ?? [])
            if (n is YamlMapping m) { Keys(m, AnswerKeys, "an answer"); answers.Add(new(Req(m, "id"), Req(m, "choice"), S(m, "value"), S(m, "note"), EnumOf<AnswerSource>(m, "source"))); }
            else Bad(n, "Each answer must be a mapping.");

        var steps = new List<PlanStep>();
        if (top.Get("steps") is YamlSequence stepSeq)
            foreach (var n in stepSeq.Items)
            {
                if (n is not YamlMapping m) { Bad(n, "Each step must be a mapping."); continue; }
                Keys(m, StepKeys, "a step");
                var parameters = new List<PlanParameter>();
                foreach (var pn in (m.Get("parameters") as YamlSequence)?.Items ?? [])
                    if (pn is YamlMapping pm) { Keys(pm, ParamKeys, "a parameter"); parameters.Add(new(Req(pm, "name"), Req(pm, "type"), Req(pm, "source"), S(pm, "value"))); }
                    else Bad(pn, "Each parameter must be a mapping.");
                var reasons = (m.Get("reasons") as YamlSequence)?.Items.OfType<YamlScalar>().Select(x => x.Value).ToList() ?? [];
                string? resolverText = null, resolverResult = null;
                var hasResolver = false;
                if (m.Get("resolver") is YamlMapping rm)
                {
                    Keys(rm, ["text", "result"], "a resolver");
                    hasResolver = true; resolverText = Req(rm, "text"); resolverResult = S(rm, "result");
                }
                steps.Add(new PlanStep(Req(m, "id"), EnumOf<StepType>(m, "type"), Req(m, "object"), Req(m, "description"), Req(m, "text"), EnumOf<RiskClass>(m, "risk"), reasons,
                    S(m, "hash_after"), parameters, resolverText, resolverResult, hasResolver, S(m, "file_hash"), S(m, "operation"), S(m, "shape_source"), S(m, "definition_hash"), S(m, "expect"), S(m, "hook"), S(m, "effect")));
            }
        var noticed = (top.Get("noticed") as YamlSequence)?.Items.OfType<YamlScalar>().Select(x => x.Value).ToList() ?? [];

        var plan = new Plan(Req(header, "id"), Req(header, "target"), S(header, "git_commit"), Bool(header, "git_dirty"), Req(header, "tool_version"), bases, answers, steps, noticed);
        if (diags.Count > before) return null;

        // integrity: the stored hash must be the hash of what is in the file. Re-serializing is not enough (it would hide edits that normalize away),
        // so the hash is checked over the file's own text after its header, and the file must be byte-identical to a re-serialization.
        var stored = S(header, "hash");
        if (stored == null) { Bad(header, "The plan has no content hash."); return null; }
        var expected = Hashing.Sha256Hex(Body(plan));
        if (stored != expected || Serialize(plan).Replace("\r\n", "\n") != text.Replace("\r\n", "\n"))
        {
            Bad(header, "The plan's content does not match its hash: the file was edited or damaged.");
            return null;
        }
        return plan;
    }

    /// <summary>The plan's content hash, as stored in the file. Recorded in `migration_log`.</summary>
    public static string ContentHash(Plan plan) => Hashing.Sha256Hex(Body(plan));
}
