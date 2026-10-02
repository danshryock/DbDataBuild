using System.Globalization;
using System.Text.Json.Nodes;
using DbDataBuild.Sql;

namespace DbDataBuild.Targets.Rules;

/// <summary>
/// The step between the lowered query and the transpile (DESIGN.md 7.6.1). The lowered query is in DuckDB's dialect and the same for every target; a few
/// functions behave differently on an engine, and polyglot can only translate the name. Each rule here rewrites the DuckDB-dialect AST into an equivalent
/// DuckDB-dialect expression built from functions that translate cleanly, so the rendered script for that target computes what DuckDB computes.
/// A rule fires only on a shape it recognises. Nothing here guesses at types: the lowerer, which knows them, marks the expressions that need a rule
/// (`round(CAST(x AS DOUBLE), n)`, `TRY_CAST(CAST(s AS VARCHAR) AS INTEGER)`), and the mark is removed when the rule fires.
/// </summary>
public static class TargetRules
{
    public sealed record Result(string Sql, IReadOnlyList<string> Rules);

    public const string LengthKeepsTrailingSpaces = "length-trailing-spaces";
    public const string RoundDouble = "round-double";
    public const string TryCastParse = "try-cast-parse";

    /// <summary>Applies the rules of <paramref name="target"/> to a DuckDB-dialect query. Returns the text unchanged, byte for byte, when no rule fires.</summary>
    public static Result Apply(string sql, string target)
    {
        var rules = RulesFor(target);
        if (rules.Count == 0) return new(sql, []);
        var parsed = Polyglot.Parse(sql, Dialects.Canonical);
        if (!parsed.Ok) return new(sql, []);
        var fired = new SortedSet<string>(StringComparer.Ordinal);
        var tree = Rewrite(JsonNode.Parse(parsed.Data!)!, target, rules, fired);
        if (fired.Count == 0) return new(sql, []);
        var text = Polyglot.Generate(tree.ToJsonString(), Dialects.Canonical);
        if (!text.Ok) throw new InvalidOperationException($"Target rules ({string.Join(", ", fired)}) produced an AST polyglot cannot print: {text.Error}. This is a tool bug.");
        var formatted = Polyglot.Format(Unwrap(text.Data!), Dialects.Canonical);
        return new(formatted.Ok ? Unwrap(formatted.Data!) : Unwrap(text.Data!), fired.ToList());
    }

    private static string Unwrap(string data)
    {
        // Generate and Format return a JSON array of statements
        var n = JsonNode.Parse(data);
        return n is JsonArray a ? string.Join(";\n", a.Select(x => x!.GetValue<string>())) : data;
    }

    private static HashSet<string> RulesFor(string target) => target switch
    {
        "sqlserver" or "fabric" => [LengthKeepsTrailingSpaces, RoundDouble, TryCastParse],
        "postgres" => [RoundDouble, TryCastParse],
        _ => [],
    };

    // ---------------------------------------------------------------------------------------------------------------------------------------------------------

    private static JsonNode Rewrite(JsonNode node, string target, HashSet<string> rules, SortedSet<string> fired)
    {
        switch (node)
        {
            case JsonArray a:
                for (var i = 0; i < a.Count; i++) if (a[i] is { } item && Rewrite(item, target, rules, fired) is var r && !ReferenceEquals(r, item)) a[i] = r;
                return a;
            case JsonObject o:
                foreach (var key in o.Select(p => p.Key).ToList())
                    if (o[key] is { } item && Rewrite(item, target, rules, fired) is var r && !ReferenceEquals(r, item)) o[key] = r;
                if (o.Count == 1) return Single(o, target, rules, fired) ?? o;
                return o;
            default:
                return node;
        }
    }

    private static JsonNode? Single(JsonObject o, string target, HashSet<string> rules, SortedSet<string> fired)
    {
        var (kind, body) = (o.First().Key, o.First().Value as JsonObject);
        if (body == null) return null;
        switch (kind)
        {
            case "length" when rules.Contains(LengthKeepsTrailingSpaces) && body["this"] is { } x:
                // LEN ignores trailing spaces; appending a character and taking one off counts them. NULL stays NULL.
                fired.Add(LengthKeepsTrailingSpaces);
                return Template("length(__x || 'x') - 1", ("__x", x));
            case "function" when rules.Contains(RoundDouble) && IsRoundOfDouble(body, out var arg, out var digits):
                fired.Add(RoundDouble);
                return RoundTemplate(arg, digits);
            case "try_cast" when rules.Contains(TryCastParse) && TryCastTemplate(body, target) is { } replaced:
                fired.Add(TryCastParse);
                return replaced;
        }
        return null;
    }

    // ---- round(double, n) -----------------------------------------------------------------------------------------------------------------------------------
    // DuckDB rounds the scaled double half away from zero: round(2.675, 2) = 2.68 and round(0.285, 2) = 0.28. SQL Server rounds the decimal text (2.67), and PostgreSQL has no
    // round(double precision, integer) at all (and rounds a lone double half to even). Scaling, rounding half away from zero and unscaling is what DuckDB does.

    private static bool IsRoundOfDouble(JsonObject f, out JsonNode arg, out int digits)
    {
        arg = null!; digits = 0;
        if (!string.Equals(f["name"]?.GetValue<string>(), "round", StringComparison.OrdinalIgnoreCase) || f["args"] is not JsonArray args || args.Count is < 1 or > 2) return false;
        if (args[0] is not JsonObject { Count: 1 } c || c["cast"] is not JsonObject cast || cast["to"]?["data_type"]?.GetValue<string>() != "double" || cast["this"] is not { } inner) return false;
        if (args.Count == 2)
        {
            var negative = args[1] is JsonObject { Count: 1 } n && n["neg"] is JsonObject neg;
            var literal = negative ? ((JsonObject)args[1]!)["neg"]!["this"] : args[1];
            if (literal?["literal"] is not JsonObject lit || lit["literal_type"]?.GetValue<string>() != "number" || !int.TryParse(lit["value"]?.GetValue<string>(), NumberStyles.None, CultureInfo.InvariantCulture, out digits)) return false;
            if (negative) digits = -digits;
            if (Math.Abs(digits) > 15) return false;
        }
        arg = inner;
        return true;
    }

    private static JsonNode RoundTemplate(JsonNode x, int digits)
    {
        var p = Math.Pow(10, Math.Abs(digits)).ToString("R", CultureInfo.InvariantCulture) + (Math.Abs(digits) < 15 ? ".0" : "");
        var sql = digits switch
        {
            0 => "sign(__x) * floor(abs(__x) + 0.5)",
            > 0 => $"(sign(__x * {p}) * floor(abs(__x * {p}) + 0.5)) / {p}",
            _ => $"(sign(__x / {p}) * floor(abs(__x / {p}) + 0.5)) * {p}",
        };
        return Template(sql, ("__x", x));
    }

    // ---- TRY_CAST of a string ------------------------------------------------------------------------------------------------------------------------------
    // DuckDB: '' gives NULL, surrounding spaces are ignored. SQL Server: TRY_CAST('' AS INT) is 0 and TRY_CAST('' AS DATE) is 1900-01-01. PostgreSQL has no TRY_CAST and raises on bad data.
    // Where DuckDB accepts a number the engines refuse ('12.7' as INTEGER is 13, '1e3' as INTEGER is 1000) the result stays NULL on the engines; the matrix row says so.

    private static readonly Dictionary<string, (long Lo, long Hi)> IntegerRanges = new()
    {
        ["tiny_int"] = (sbyte.MinValue, sbyte.MaxValue), ["small_int"] = (short.MinValue, short.MaxValue), ["int"] = (int.MinValue, int.MaxValue), ["big_int"] = (long.MinValue, long.MaxValue),
    };

    private const string IntegerPattern = @"^\s*[+-]?[0-9]+\s*$";
    private const string NumberPattern = @"^\s*[+-]?([0-9]+\.?[0-9]*|\.[0-9]+)([eE][+-]?[0-9]+)?\s*$";

    private static JsonNode? TryCastTemplate(JsonObject tc, string target)
    {
        // only the marked form: TRY_CAST(CAST(s AS VARCHAR) AS <number or date>)
        if (tc["this"] is not JsonObject { Count: 1 } mark || mark["cast"] is not JsonObject mc || mc["to"]?["data_type"]?.GetValue<string>() != "var_char" || mc["this"] is not { } x) return null;
        if (tc["to"] is not JsonObject to || to["data_type"]?.GetValue<string>() is not { } type) return null;
        var numeric = IntegerRanges.ContainsKey(type) || type is "decimal" or "double" or "float";
        var temporal = type is "date" or "timestamp";
        if (!numeric && !temporal) return null;

        if (target is "sqlserver" or "fabric")
        {
            var blank = Template("nullif(trim(__x), '')", ("__x", x));
            var copy = (JsonObject)JsonNode.Parse(tc.ToJsonString())!;
            copy["this"] = blank;
            return new JsonObject { ["try_cast"] = copy };
        }
        if (target != "postgres" || temporal) return null;     // PostgreSQL cannot test a date without raising

        var cast = new JsonObject { ["cast"] = new JsonObject { ["this"] = x.DeepClone(), ["to"] = to.DeepClone(), ["double_colon_syntax"] = false } };
        if (IntegerRanges.TryGetValue(type, out var range))
            return Template($"CASE WHEN regexp_matches(__x, '{IntegerPattern}') THEN CASE WHEN CAST(__x AS DECIMAL(30, 0)) BETWEEN {range.Lo} AND {range.Hi} THEN __cast END END", ("__x", x), ("__cast", cast));
        if (type == "decimal")
        {
            var (p, s) = (to["precision"]?.GetValue<int>() ?? 18, to["scale"]?.GetValue<int>() ?? 3);
            var bound = "1" + new string('0', Math.Max(p - s, 0));
            if (s > 37) return null;
            return Template($"CASE WHEN regexp_matches(__x, '{NumberPattern}') THEN CASE WHEN abs(CAST(__x AS DECIMAL(38, {s}))) < {bound} THEN __cast END END", ("__x", x), ("__cast", cast));
        }
        return Template($"CASE WHEN regexp_matches(__x, '{NumberPattern}') THEN __cast END", ("__x", x), ("__cast", cast));
    }

    // ---- templates -------------------------------------------------------------------------------------------------------------------------------------------

    /// <summary>Parses a DuckDB-dialect expression and puts clones of the given nodes where the placeholder columns are.</summary>
    private static JsonNode Template(string expression, params (string Name, JsonNode Value)[] holes)
    {
        var parsed = Polyglot.Parse("SELECT " + expression, Dialects.Canonical);
        if (!parsed.Ok) throw new InvalidOperationException($"Target rule template `{expression}` does not parse: {parsed.Error}. This is a tool bug.");
        var tree = JsonNode.Parse(parsed.Data!)!;
        var expr = tree[0]!["select"]!["expressions"]![0]!;
        expr.Parent!.AsArray().Remove(expr);
        return Fill(expr, holes);
    }

    private static JsonNode Fill(JsonNode node, (string Name, JsonNode Value)[] holes)
    {
        switch (node)
        {
            case JsonArray a:
                for (var i = 0; i < a.Count; i++) if (a[i] is { } item && Fill(item, holes) is var r && !ReferenceEquals(r, item)) a[i] = r;
                return a;
            case JsonObject o:
                if (o.Count == 1 && o["column"] is JsonObject col && col["name"]?["name"]?.GetValue<string>() is { } n && holes.FirstOrDefault(h => h.Name == n) is { Value: not null } hole)
                    return hole.Value.DeepClone();
                foreach (var key in o.Select(p => p.Key).ToList())
                    if (o[key] is { } item && Fill(item, holes) is var r && !ReferenceEquals(r, item)) o[key] = r;
                return o;
            default:
                return node;
        }
    }
}
