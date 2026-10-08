using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DbDataBuild.Core;
using DbDataBuild.Models.Yaml;
using DbDataBuild.State;

namespace DbDataBuild.Planning;

/// <summary>A data hook around a routine load, by name and file: the script is read from the project when the refresh runs, and must have the hash the plan recorded.</summary>
public sealed record RefreshHook(string Name, string Event, string File, string FileHash, string Effect, string Risk);

/// <param name="Source">`parameter` (a value the project's files give, recorded here) or `resolver` (read from the connection when the refresh runs).</param>
public sealed record RefreshParameter(string Name, string Type, string Source, string? Value);

/// <summary>
/// One routine load: the model's default operation. The scripts are the committed ones under `rendered/<connection>/`, named here with their hashes; a value that is only known when the refresh runs (a watermark)
/// is not in the plan, only the resolver that finds it and what to do when it finds nothing.
/// </summary>
public sealed record RefreshLoad(string Model, string Operation, string File, string FileHash, string? ResolverFile, string? ResolverHash, string DefinitionHash,
    IReadOnlyList<RefreshParameter> Parameters, string? OnNull, string? Initial, IReadOnlyList<RefreshHook> Before, IReadOnlyList<RefreshHook> After);

/// <summary>An object a refresh uses and the shape the compiled project expects it to have (the hash of its declared columns, as the engine reports them).</summary>
public sealed record RefreshRequirement(string Object, string ShapeHash);

/// <summary>A model with loads that is not in the plan, and why: a refresh runs routine loads only.</summary>
public sealed record RefreshExclusion(string Model, string Reason);

/// <summary>
/// The compiled plan of the routine loads for one connection (`rendered/<connection>/refresh.plan.yml`): committed, so it documents how the project operates every day, and deterministic, so a change to it
/// shows in review. <see cref="ProjectHash"/> is a hash over the structure the compiled project expects of the whole connection, which a deploy records, so a refresh can ask whether this project was deployed.
/// </summary>
public sealed record RefreshPlan(string Connection, string ProjectHash, IReadOnlyList<RefreshLoad> Loads, IReadOnlyList<RefreshRequirement> Requires, IReadOnlyList<RefreshExclusion> Excluded);

/// <summary>Reads and writes the refresh plan file. Like a deploy plan it carries a hash of its own content and is refused when it was edited.</summary>
public static class RefreshPlanDocument
{
    public const string FileName = "refresh.plan.yml";
    private const string HashPrefix = "  hash: ";
    private static readonly JsonSerializerOptions Quote = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static string Q(string s) => JsonSerializer.Serialize(s, Quote);

    public static string Serialize(RefreshPlan plan)
    {
        var body = Body(plan);
        return "refresh_plan:\n" + HashPrefix + Hashing.Sha256Hex(body) + "\n" + body;
    }

    private static void Hooks(StringBuilder sb, string key, IReadOnlyList<RefreshHook> hooks)
    {
        if (hooks.Count == 0) return;
        sb.Append("    ").Append(key).AppendLineLf(":");
        foreach (var h in hooks)
        {
            sb.Append("      - name: ").AppendLineLf(Q(h.Name));
            sb.Append("        event: ").AppendLineLf(Q(h.Event));
            sb.Append("        file: ").AppendLineLf(Q(h.File));
            sb.Append("        file_hash: ").AppendLineLf(Q(h.FileHash));
            sb.Append("        effect: ").AppendLineLf(Q(h.Effect));
            sb.Append("        risk: ").AppendLineLf(Q(h.Risk));
        }
    }

    private static string Body(RefreshPlan p)
    {
        var sb = new StringBuilder();
        sb.Append("  connection: ").AppendLineLf(Q(p.Connection));
        sb.Append("  project_hash: ").AppendLineLf(Q(p.ProjectHash));
        sb.Append("  loads: ").AppendLineLf(p.Loads.Count.ToString(CultureInfo.InvariantCulture));
        if (p.Loads.Count > 0) sb.AppendLineLf("loads:");
        foreach (var l in p.Loads)
        {
            sb.Append("  - model: ").AppendLineLf(Q(l.Model));
            sb.Append("    operation: ").AppendLineLf(Q(l.Operation));
            sb.Append("    file: ").AppendLineLf(Q(l.File));
            sb.Append("    file_hash: ").AppendLineLf(Q(l.FileHash));
            if (l.ResolverFile != null) { sb.Append("    resolver_file: ").AppendLineLf(Q(l.ResolverFile)); sb.Append("    resolver_hash: ").AppendLineLf(Q(l.ResolverHash ?? "")); }
            sb.Append("    definition_hash: ").AppendLineLf(Q(l.DefinitionHash));
            if (l.OnNull != null) sb.Append("    on_null: ").AppendLineLf(Q(l.OnNull));
            if (l.Initial != null) sb.Append("    initial: ").AppendLineLf(Q(l.Initial));
            if (l.Parameters.Count > 0)
            {
                sb.AppendLineLf("    parameters:");
                foreach (var prm in l.Parameters)
                {
                    sb.Append("      - name: ").AppendLineLf(Q(prm.Name));
                    sb.Append("        type: ").AppendLineLf(Q(prm.Type));
                    sb.Append("        source: ").AppendLineLf(Q(prm.Source));
                    if (prm.Value != null) sb.Append("        value: ").AppendLineLf(Q(prm.Value));
                }
            }
            Hooks(sb, "before", l.Before);
            Hooks(sb, "after", l.After);
        }
        if (p.Requires.Count > 0) sb.AppendLineLf("requires:");
        foreach (var r in p.Requires)
        {
            sb.Append("  - object: ").AppendLineLf(Q(r.Object));
            sb.Append("    shape_hash: ").AppendLineLf(Q(r.ShapeHash));
        }
        if (p.Excluded.Count > 0) sb.AppendLineLf("excluded:");
        foreach (var x in p.Excluded)
        {
            sb.Append("  - model: ").AppendLineLf(Q(x.Model));
            sb.Append("    reason: ").AppendLineLf(Q(x.Reason));
        }
        return sb.ToString();
    }

    private static readonly string[] TopKeys = ["refresh_plan", "loads", "requires", "excluded"];
    private static readonly string[] HeaderKeys = ["hash", "connection", "project_hash", "loads"];
    private static readonly string[] LoadKeys = ["model", "operation", "file", "file_hash", "resolver_file", "resolver_hash", "definition_hash", "on_null", "initial", "parameters", "before", "after"];
    private static readonly string[] HookKeys = ["name", "event", "file", "file_hash", "effect", "risk"];

    /// <summary>Reads a refresh plan. Returns null with diagnostics when it is malformed, has unknown keys, or does not match its own hash.</summary>
    public static RefreshPlan? Parse(string text, string file, List<Diagnostic> diags)
    {
        var before = diags.Count;
        var root = StrictYamlReader.Read(text, file, diags);
        if (root == null && diags.Count == before) diags.Add(new Diagnostic(DiagnosticCatalog.PlanFileInvalid, new(file, 0, 0), "The refresh plan file is empty."));
        if (root == null || diags.Count > before) return null;
        void Bad(YamlNode at, string found) => diags.Add(new Diagnostic(DiagnosticCatalog.PlanFileInvalid, new(file, at.Line, at.Column), found));
        if (root is not YamlMapping top) { Bad(root, "A refresh plan must be a mapping."); return null; }
        foreach (var e in top.Entries.Where(e => !TopKeys.Contains(e.Key.Value))) Bad(e.Key, $"Unknown key `{e.Key.Value}`.");
        if (top.Get("refresh_plan") is not YamlMapping header) { Bad(top, "The `refresh_plan:` section is missing."); return null; }
        foreach (var e in header.Entries.Where(e => !HeaderKeys.Contains(e.Key.Value))) Bad(e.Key, $"Unknown key `{e.Key.Value}` in `refresh_plan:`.");

        string? S(YamlMapping m, string k) => (m.Get(k) as YamlScalar)?.Value;
        string Req(YamlMapping m, string k) { var v = S(m, k); if (v == null) Bad(m, $"Required key `{k}` is missing."); return v ?? ""; }
        void Keys(YamlMapping m, string[] allowed, string where) { foreach (var e in m.Entries.Where(e => !allowed.Contains(e.Key.Value))) Bad(e.Key, $"Unknown key `{e.Key.Value}` in {where}."); }
        List<RefreshHook> HookList(YamlMapping owner, string key)
        {
            var list = new List<RefreshHook>();
            foreach (var n in (owner.Get(key) as YamlSequence)?.Items ?? [])
                if (n is YamlMapping hm) { Keys(hm, HookKeys, "a hook"); list.Add(new(Req(hm, "name"), Req(hm, "event"), Req(hm, "file"), Req(hm, "file_hash"), Req(hm, "effect"), Req(hm, "risk"))); }
                else Bad(n, "Each hook must be a mapping.");
            return list;
        }

        var loads = new List<RefreshLoad>();
        foreach (var n in (top.Get("loads") as YamlSequence)?.Items ?? [])
        {
            if (n is not YamlMapping m) { Bad(n, "Each load must be a mapping."); continue; }
            Keys(m, LoadKeys, "a load");
            var parameters = new List<RefreshParameter>();
            foreach (var pn in (m.Get("parameters") as YamlSequence)?.Items ?? [])
                if (pn is YamlMapping pm) { Keys(pm, ["name", "type", "source", "value"], "a parameter"); parameters.Add(new(Req(pm, "name"), Req(pm, "type"), Req(pm, "source"), S(pm, "value"))); }
                else Bad(pn, "Each parameter must be a mapping.");
            loads.Add(new(Req(m, "model"), Req(m, "operation"), Req(m, "file"), Req(m, "file_hash"), S(m, "resolver_file"), S(m, "resolver_hash"), Req(m, "definition_hash"), parameters, S(m, "on_null"), S(m, "initial"), HookList(m, "before"), HookList(m, "after")));
        }
        var requires = new List<RefreshRequirement>();
        foreach (var n in (top.Get("requires") as YamlSequence)?.Items ?? [])
            if (n is YamlMapping m) { Keys(m, ["object", "shape_hash"], "a requirement"); requires.Add(new(Req(m, "object"), Req(m, "shape_hash"))); }
            else Bad(n, "Each requirement must be a mapping.");
        var excluded = new List<RefreshExclusion>();
        foreach (var n in (top.Get("excluded") as YamlSequence)?.Items ?? [])
            if (n is YamlMapping m) { Keys(m, ["model", "reason"], "an exclusion"); excluded.Add(new(Req(m, "model"), Req(m, "reason"))); }
            else Bad(n, "Each exclusion must be a mapping.");

        var plan = new RefreshPlan(Req(header, "connection"), Req(header, "project_hash"), loads, requires, excluded);
        if (diags.Count > before) return null;
        var stored = S(header, "hash");
        if (stored == null) { Bad(header, "The refresh plan has no content hash."); return null; }
        if (stored != Hashing.Sha256Hex(Body(plan)) || Serialize(plan).Replace("\r\n", "\n") != text.Replace("\r\n", "\n"))
        {
            Bad(header, "The refresh plan's content does not match its hash: the file was edited or damaged. `project compile` writes it again.");
            return null;
        }
        return plan;
    }

    /// <summary>The hash a file carries over its own content (what the event records as the plan it ran).</summary>
    public static string ContentHash(RefreshPlan plan) => Hashing.Sha256Hex(Body(plan));
}
