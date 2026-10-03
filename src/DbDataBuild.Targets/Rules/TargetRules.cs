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
    public const string DoubleToInt = "double-to-int";
    public const string WeekdayIndependentOfDateFirst = "weekday-datefirst";
    public const string PadToLength = "pad-to-length";
    public const string DatePlusDays = "date-plus-days";
    public const string ConcatAsPlus = "concat-plus";
    public const string SplitPart = "split-part";
    public const string StringAggAsArrayToString = "string-agg-array";

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
        "sqlserver" or "fabric" => [LengthKeepsTrailingSpaces, RoundDouble, TryCastParse, DoubleToInt, WeekdayIndependentOfDateFirst, PadToLength, DatePlusDays, ConcatAsPlus],
        "postgres" => [RoundDouble, TryCastParse, SplitPart, StringAggAsArrayToString],
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
            case "function" when rules.Contains(WeekdayIndependentOfDateFirst) && IsWeekdayPart(body, out var isoWeekday, out var day):
                // DATEPART(weekday) counts from whatever @@DATEFIRST says; DuckDB's dow is Sunday = 0 and isodow Monday = 1, whatever the session. Counting days from a known Sunday (1900-01-07) or Monday does not depend on it.
                fired.Add(WeekdayIndependentOfDateFirst);
                return Template(isoWeekday ? "((date_diff('day', DATE '1900-01-01', CAST(__x AS DATE)) % 7) + 7) % 7 + 1" : "((date_diff('day', DATE '1900-01-07', CAST(__x AS DATE)) % 7) + 7) % 7", ("__x", day));
            case "function" when rules.Contains(PadToLength) && IsPad(body, out var padLeft, out var padded, out var padLength, out var padWith):
                // SQL Server has no LPAD or RPAD before 2025. DuckDB cuts a string that is too long to n characters and pads a short one from the left of the pad text, repeated and cut to fit.
                fired.Add(PadToLength);
                return Template(padLeft
                    ? "CASE WHEN __n <= length(__s || 'x') - 1 THEN left(__s, __n) ELSE left(repeat(__p, __n), __n - (length(__s || 'x') - 1)) || __s END"
                    : "CASE WHEN __n <= length(__s || 'x') - 1 THEN left(__s, __n) ELSE __s || left(repeat(__p, __n), __n - (length(__s || 'x') - 1)) END",
                    ("__s", padded), ("__n", padLength), ("__p", padWith));
            case "function" when rules.Contains(SplitPart) && IsArrayExtractOfSplit(body, out var splitText, out var splitBy, out var splitIndex):
                // DuckDB's split_part is a macro over string_split and array_extract in the plan; PostgreSQL has split_part itself, which also gives '' past the last piece. Its position is an integer.
                fired.Add(SplitPart);
                return Template("split_part(__a, __b, CAST(__n AS INTEGER))", ("__a", splitText), ("__b", splitBy), ("__n", splitIndex));
            case "function" when rules.Contains(RoundDouble) && IsRoundOfDouble(body, out var arg, out var digits):
                fired.Add(RoundDouble);
                return RoundTemplate(arg, digits);
            case "cast" when rules.Contains(DoubleToInt) && IsDoubleToInt(body, out var number):
                // SQL Server's cast to an integer truncates; DuckDB rounds to the nearest, a half to the even neighbour. Scaling by a half, rounding half away from zero and doubling does that.
                fired.Add(DoubleToInt);
                body["this"] = Template("CASE WHEN abs(__x - floor(__x)) = 0.5 THEN 2 * round(__x * 0.5, 0) ELSE round(__x, 0) END", ("__x", number));
                return o;
            case "concat" when rules.Contains(ConcatAsPlus) && body["left"] is { } concatLeft && body["right"] is { } concatRight:
                // The transpile turns `||` into `+` for SQL Server, except inside the arguments of the functions it rewrites into another shape (strpos becomes CHARINDEX with the arguments swapped, starts_with
                // becomes LEFT ... = ...): `strpos(a || b, 'x')` came out as `CHARINDEX('x', a || b)`, which T-SQL does not parse. Writing the plus here makes every `||` come out the same. The lowered query
                // casts a non-text operand to text itself, so a `+` never adds two numbers; both operands NULL-propagate like `||`.
                fired.Add(ConcatAsPlus);
                return Template("__a + __b", ("__a", concatLeft), ("__b", concatRight));
            case "string_agg" when rules.Contains(StringAggAsArrayToString) && body["separator"] is { } separator:
            {
                // polyglot writes LISTAGG for PostgreSQL, which has no such function. array_to_string(array_agg(x ORDER BY ...), sep) skips NULLs and gives NULL for no rows, as string_agg does.
                fired.Add(StringAggAsArrayToString);
                var agg = new JsonObject
                {
                    ["this"] = body["this"]!.DeepClone(), ["distinct"] = body["distinct"]?.DeepClone() ?? false, ["filter"] = body["filter"]?.DeepClone(),
                    ["order_by"] = body["order_by"]?.DeepClone() ?? new JsonArray(), ["name"] = "array_agg",
                };
                return new JsonObject
                {
                    ["function"] = new JsonObject
                    {
                        ["name"] = "array_to_string", ["args"] = new JsonArray(new JsonObject { ["array_agg"] = agg }, separator.DeepClone()),
                        ["distinct"] = false, ["use_bracket_syntax"] = false, ["no_parens"] = false,
                    },
                };
            }
            case "add" or "sub" when rules.Contains(DatePlusDays) && IsDatePlusDays(kind, body, out var dateSide, out var daysSide, out var subtract):
                // SQL Server refuses `date + int` (error 206). An interval of that many days is what DATEADD says.
                fired.Add(DatePlusDays);
                return Template(subtract ? "CAST(__d - INTERVAL (__n) DAY AS DATE)" : "CAST(__d + INTERVAL (__n) DAY AS DATE)", ("__d", dateSide), ("__n", daysSide));
            case "try_cast" when rules.Contains(TryCastParse) && TryCastTemplate(body, target) is { } replaced:
                fired.Add(TryCastParse);
                return replaced;
        }
        return null;
    }

    /// <summary>lpad(s, n, p) or rpad(s, n, p) with a whole-number literal n (up to 4000, what REPLICATE keeps of a non-MAX string) and a non-empty literal pad. Anything else is left for the matrix to refuse.</summary>
    private static bool IsPad(JsonObject f, out bool left, out JsonNode s, out JsonNode n, out JsonNode pad)
    {
        left = false; s = n = pad = null!;
        var name = f["name"]?.GetValue<string>();
        if (!string.Equals(name, "lpad", StringComparison.OrdinalIgnoreCase) && !string.Equals(name, "rpad", StringComparison.OrdinalIgnoreCase)) return false;
        if (f["args"] is not JsonArray { Count: 3 } args || args[0] is not { } text) return false;
        if (args[1]?["literal"] is not JsonObject count || count["literal_type"]?.GetValue<string>() != "number" ||
            !int.TryParse(count["value"]?.GetValue<string>(), NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length is < 1 or > 4000) return false;
        if (args[2]?["literal"] is not JsonObject with || with["literal_type"]?.GetValue<string>() != "string" || string.IsNullOrEmpty(with["value"]?.GetValue<string>())) return false;
        left = string.Equals(name, "lpad", StringComparison.OrdinalIgnoreCase);
        s = text; n = args[1]!; pad = args[2]!;
        return true;
    }

    private static bool IsArrayExtractOfSplit(JsonObject f, out JsonNode text, out JsonNode by, out JsonNode index)
    {
        text = by = index = null!;
        if (!string.Equals(f["name"]?.GetValue<string>(), "array_extract", StringComparison.OrdinalIgnoreCase) || f["args"] is not JsonArray { Count: 2 } outer) return false;
        if (outer[0] is not JsonObject { Count: 1 } inner || inner["function"] is not JsonObject split || !string.Equals(split["name"]?.GetValue<string>(), "string_split", StringComparison.OrdinalIgnoreCase) ||
            split["args"] is not JsonArray { Count: 2 } parts) return false;
        text = parts[0]!; by = parts[1]!; index = outer[1]!;
        return true;
    }

    /// <summary>`CAST(d AS DATE) + CAST(n AS INTEGER)` (either way round for +) or `CAST(d AS DATE) - CAST(n AS INTEGER)`, the shape the lowerer gives a date and a number of days.</summary>
    private static bool IsDatePlusDays(string kind, JsonObject b, out JsonNode date, out JsonNode days, out bool subtract)
    {
        date = days = null!; subtract = kind == "sub";
        if (b["left"] is not JsonObject { Count: 1 } l || b["right"] is not JsonObject { Count: 1 } r || l["cast"] is not JsonObject lc || r["cast"] is not JsonObject rc) return false;
        static string? Type(JsonObject c) => c["to"]?["data_type"]?.GetValue<string>();
        static bool Whole(string? t) => t is "tiny_int" or "small_int" or "int" or "big_int";
        if (Type(lc) == "date" && Whole(Type(rc))) { date = lc["this"]!; days = rc["this"]!; return true; }
        if (kind == "add" && Whole(Type(lc)) && Type(rc) == "date") { date = rc["this"]!; days = lc["this"]!; return true; }
        return false;
    }

    private static bool IsWeekdayPart(JsonObject f, out bool iso, out JsonNode day)
    {
        iso = false; day = null!;
        if (!string.Equals(f["name"]?.GetValue<string>(), "date_part", StringComparison.OrdinalIgnoreCase) || f["args"] is not JsonArray { Count: 2 } args) return false;
        var part = args[0]?["literal"]?["value"]?.GetValue<string>()?.ToLowerInvariant();
        if (part is not ("dow" or "dayofweek" or "isodow") || args[1] is not { } x) return false;
        iso = part == "isodow";
        day = x;
        return true;
    }

    private static bool IsDoubleToInt(JsonObject cast, out JsonNode x)
    {
        x = null!;
        if (cast["to"]?["data_type"]?.GetValue<string>() is not ("tiny_int" or "small_int" or "int" or "big_int")) return false;
        if (cast["this"] is not JsonObject { Count: 1 } mark || mark["cast"] is not JsonObject inner || inner["to"]?["data_type"]?.GetValue<string>() != "double" || inner["this"] is not { } value) return false;
        x = value;
        return true;
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
