using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DbDataBuild.Lowering;

public sealed record LoweredColumn(string Name, string DuckDbType);

/// <param name="Sql">One readable query in DuckDB's dialect: explicit columns, explicit casts, no macros, `*`, PIVOT or `GROUP BY ALL`.</param>
/// <param name="Columns">The output columns and the types DuckDB resolved for them.</param>
/// <param name="Rules">The type-pinning rules that changed the text, by name (for reports and tests).</param>
public sealed record LoweredQuery(string Sql, IReadOnlyList<LoweredColumn> Columns, IReadOnlyList<string> Rules);

/// <summary>The query cannot be lowered. The message says what in the plan has no lowering yet; nothing is guessed (DESIGN.md: no silent fallbacks).</summary>
public sealed class LoweringException(string reason, string kind = "unsupported") : Exception(reason)
{
    /// <summary>`parser`, `binder` or `catalog` (or another DuckDB error type) when DuckDB itself rejected the query (its message is in the text), otherwise `unsupported`.</summary>
    public string Kind { get; } = kind;
}

/// <summary>
/// Turns DuckDB's bound, unoptimized logical plan into one readable SELECT in DuckDB's dialect (docs/research/duckdb-plan-lowering). Every plan operator becomes a
/// <c>Rel</c>, a SELECT block under construction; operators merge into their child's block when SQL allows it and otherwise wrap the child as a subquery. A column is
/// qualified only in a block with more than one source. Anything the lowerer does not know is a <see cref="LoweringException"/>, never a guess.
/// </summary>
public sealed class PlanLowerer
{
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "order", "group", "select", "from", "where", "table", "user", "key", "index", "primary", "end", "by", "case", "when", "then", "else", "all", "as", "to", "limit", "offset",
        "having", "union", "join", "on", "and", "or", "not", "null", "true", "false", "in", "is", "like", "between", "exists", "distinct", "values", "year", "date", "time",
    };

    private static readonly Dictionary<string, string> LikeFunctions = new() { ["~~"] = "LIKE", ["~~*"] = "ILIKE", ["!~~"] = "NOT LIKE", ["!~~*"] = "NOT ILIKE" };
    private static readonly HashSet<string> Infix = ["+", "-", "*", "/", "%", "||", "//", "**"];
    private static readonly Dictionary<string, string> AggregateNames = new() { ["count_star"] = "count", ["sum_no_overflow"] = "sum" };
    private static readonly Dictionary<string, string> Comparisons = new()
    {
        ["COMPARE_EQUAL"] = "=", ["COMPARE_NOTEQUAL"] = "<>", ["COMPARE_LESSTHAN"] = "<", ["COMPARE_GREATERTHAN"] = ">", ["COMPARE_LESSTHANOREQUALTO"] = "<=",
        ["COMPARE_GREATERTHANOREQUALTO"] = ">=", ["COMPARE_NOT_DISTINCT_FROM"] = "IS NOT DISTINCT FROM", ["COMPARE_DISTINCT_FROM"] = "IS DISTINCT FROM",
    };
    private static readonly HashSet<string> IntegerTypes = ["TINYINT", "SMALLINT", "INTEGER", "BIGINT", "HUGEINT", "UTINYINT", "USMALLINT", "UINTEGER", "UBIGINT"];

    private Func<string, IReadOnlyList<string>>? grainOf;
    private readonly Dictionary<string, string> aliasTables = new(StringComparer.Ordinal);
    private int aliasCounter;
    private readonly Dictionary<string, int> uses = [];
    private readonly Dictionary<int, (string Name, Rel Definition)> ctes = [];
    private readonly List<string> rules = [];

    /// <param name="outputNames">
    /// The names DuckDB gives the query's output columns (from `DESCRIBE`). The plan does not always carry them: when no projection sits at the top (a bare aggregate, for example)
    /// the author's aliases exist only in DuckDB's binder state, so they are applied to the lowered query by position.
    /// </param>
    /// <summary>Throws the <see cref="LoweringException"/> for a plan document that is DuckDB's own error (a parse or bind failure), so callers can classify it before doing anything else.</summary>
    public static void ThrowIfError(string planJson)
    {
        using var doc = JsonDocument.Parse(planJson);
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.True)
            throw new LoweringException(root.TryGetProperty("error_message", out var m) ? m.GetString()! : "unknown error", root.TryGetProperty("error_type", out var et) ? et.GetString()! : "binder");
    }

    /// <param name="grainOf">
    /// The columns that identify one row of a table (its declared grain), by the name the query uses (`staging.orders`). `DISTINCT ON` keeps one arbitrary row per key unless its
    /// ordering decides which, so it is lowered only when the ordering includes the grain of every table it reads; an unknown or empty grain means "cannot prove it".
    /// </param>
    public static LoweredQuery Lower(string planJson, IReadOnlyList<string>? outputNames = null, Func<string, IReadOnlyList<string>>? grainOf = null)
    {
        using var doc = JsonDocument.Parse(PlanNormalizer.Normalize(planJson));
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.True)
            throw new LoweringException(root.TryGetProperty("error_message", out var m) ? m.GetString()! : "unknown error", root.TryGetProperty("error_type", out var et) ? et.GetString()! : "binder");
        var plans = root.GetProperty("plans");
        if (plans.GetArrayLength() != 1) throw new LoweringException($"expected one plan, got {plans.GetArrayLength()}");
        var lowerer = new PlanLowerer { grainOf = grainOf };
        var rel = lowerer.Node(plans[0]);
        if (outputNames != null && rel.SetOp == null)
        {
            // DuckDB appends hidden columns (a DISTINCT ON key, an ORDER BY key) after the visible ones and drops them from the result: the first N are the query's columns
            if (outputNames.Count < rel.Sel.Count) rel.Sel = rel.Sel.Take(outputNames.Count).ToList();
            if (outputNames.Count != rel.Sel.Count) throw new LoweringException($"the plan has {rel.Sel.Count} output columns, but DuckDB describes {outputNames.Count}");
            rel.Sel = rel.Sel.Select((s, i) => s with { Alias = outputNames[i] }).ToList();
        }
        var sql = rel.Cte(lowerer.ctesInOrder) + rel.Sql();
        var names = rel.SetOp != null ? rel.SetOpNames! : rel.Aliases();
        var columns = names.Select((n, i) => new LoweredColumn(n, rel.Sel.Count > i ? rel.Sel[i].Type ?? "UNKNOWN" : "UNKNOWN")).ToList();
        return new LoweredQuery(sql, columns, lowerer.rules.Distinct().Order(StringComparer.Ordinal).ToList());
    }

    private readonly List<(string Name, string Sql)> ctesInOrder = [];

    // ---------------------------------------------------------------------------------------------------------------------------------------------------------

    private static string Ident(string n) => Regex.IsMatch(n, "^[a-z_][a-z0-9_]*$") && !Reserved.Contains(n) ? n : "\"" + n.Replace("\"", "\"\"") + "\"";

    private static string TypeName(JsonElement t)
    {
        var id = t.GetProperty("id").GetString()!;
        if (id == "DECIMAL" && t.TryGetProperty("type_info", out var ti))
            return $"DECIMAL({(ti.TryGetProperty("width", out var w) ? w.GetInt32() : 18)}, {(ti.TryGetProperty("scale", out var s) ? s.GetInt32() : 3)})";
        return id;
    }

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static IEnumerable<JsonElement> Arr(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];
    private static string? TypeId(JsonElement e) => e.TryGetProperty("return_type", out var t) ? t.GetProperty("id").GetString() : null;

    /// <summary>The marker for a column of a source; <see cref="Rel.Render"/> turns it into `source.column` or a bare name.</summary>
    private static string Token(string alias, string name) => $"\0{alias}\u0001{name}\0";

    private static string Literal(JsonElement v)
    {
        if (v.TryGetProperty("is_null", out var isNull) && isNull.ValueKind == JsonValueKind.True) return "NULL";
        var type = v.GetProperty("type").GetProperty("id").GetString()!;
        var x = v.GetProperty("value");
        switch (type)
        {
            case "DECIMAL":
            {
                var scale = v.GetProperty("type").TryGetProperty("type_info", out var ti) && ti.TryGetProperty("scale", out var sc) ? sc.GetInt32() : 0;
                var n = BigInteger.Parse(x.GetRawText());
                var digits = BigInteger.Abs(n).ToString().PadLeft(scale + 1, '0');
                return (n.Sign < 0 ? "-" : "") + (scale > 0 ? digits[..^scale] + "." + digits[^scale..] : digits);
            }
            case "VARCHAR": return "'" + x.GetString()!.Replace("'", "''") + "'";
            case "BOOLEAN": return x.ValueKind == JsonValueKind.True || (x.ValueKind == JsonValueKind.String && x.GetString() is "t" or "true") ? "TRUE" : "FALSE";
            case "DATE": return x.ValueKind == JsonValueKind.String ? $"DATE '{x.GetString()}'" : $"CAST({x.GetRawText()} AS DATE)";
            case "DOUBLE" or "FLOAT":
            {
                var d = double.Parse(x.GetRawText(), System.Globalization.CultureInfo.InvariantCulture).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                return d.Contains('.') || d.Contains('E') ? d : d + ".0";
            }
            default:
                if (IntegerTypes.Contains(type)) return x.GetRawText();
                throw new LoweringException($"a literal of type {type}");
        }
    }

    // ---------------------------------------------------------------------------------------------------------------------------------------------------------
    // expressions

    private string Expr(JsonElement e, IReadOnlyList<string> outs)
    {
        var t = e.GetProperty("type").GetString()!;
        switch (t)
        {
            case "BOUND_REF": return outs[e.GetProperty("index").GetInt32()];
            case "VALUE_CONSTANT": return Literal(e.GetProperty("value"));
            case "OPERATOR_CAST": return Cast(e, outs);
            case "BOUND_FUNCTION": return Function(e, outs);
            case "BOUND_AGGREGATE": return Aggregate(e, outs);
            case "COMPARE_BETWEEN" or "COMPARE_NOT_BETWEEN":
            {
                // the binder keeps BETWEEN as one node when its input is not a plain column or constant (a subquery, for example)
                var input = Expr(e.GetProperty("input"), outs);
                var lower = Expr(e.GetProperty("lower"), outs);
                var upper = Expr(e.GetProperty("upper"), outs);
                var both = e.GetProperty("lower_inclusive").ValueKind == JsonValueKind.True && e.GetProperty("upper_inclusive").ValueKind == JsonValueKind.True;
                if (!both) throw new LoweringException("a BETWEEN with an exclusive bound");
                return $"({input} {(t == "COMPARE_NOT_BETWEEN" ? "NOT " : "")}BETWEEN {lower} AND {upper})";
            }
            case "COMPARE_IN" or "COMPARE_NOT_IN":
            {
                var ch = Arr(e, "children").Select(c => Expr(c, outs)).ToList();
                return $"({ch[0]} {(t == "COMPARE_NOT_IN" ? "NOT " : "")}IN ({string.Join(", ", ch.Skip(1))}))";
            }
            case "OPERATOR_COALESCE": return $"coalesce({string.Join(", ", Arr(e, "children").Select(c => Expr(c, outs)))})";
            case "OPERATOR_IS_NULL": return $"({Expr(Arr(e, "children").First(), outs)} IS NULL)";
            case "OPERATOR_IS_NOT_NULL": return $"({Expr(Arr(e, "children").First(), outs)} IS NOT NULL)";
            case "OPERATOR_NOT": return $"(NOT {Expr(Arr(e, "children").First(), outs)})";
            case "CONJUNCTION_AND": return "(" + string.Join(" AND ", Arr(e, "children").Select(c => Expr(c, outs))) + ")";
            case "CONJUNCTION_OR": return "(" + string.Join(" OR ", Arr(e, "children").Select(c => Expr(c, outs))) + ")";
            case "CASE_EXPR":
            {
                var sb = new StringBuilder("CASE ");
                var checks = Arr(e, "case_checks").ToList();
                foreach (var c in checks) sb.Append($"WHEN {Expr(c.GetProperty("when_expr"), outs)} THEN {Expr(c.GetProperty("then_expr"), outs)} ");
                if (e.TryGetProperty("else_expr", out var el)) sb.Append($"ELSE {Expr(el, outs)} ");
                // DuckDB's guard for count(*) over a correlated subquery that matched no rows: `CASE WHEN count(*) IS NULL THEN 0 ELSE count(*) END`. A count is never NULL here.
                if (checks.Count == 1 && e.TryGetProperty("else_expr", out var guarded) && Expr(guarded, outs) is var inner && inner.StartsWith("count(", StringComparison.Ordinal) &&
                    Expr(checks[0].GetProperty("when_expr"), outs) == $"({inner} IS NULL)" && Expr(checks[0].GetProperty("then_expr"), outs) == "0")
                    return inner;
                return sb.Append("END").ToString();
            }
        }
        if (Comparisons.TryGetValue(t, out var op)) return $"({Expr(e.GetProperty("left"), outs)} {op} {Expr(e.GetProperty("right"), outs)})";
        if (t.StartsWith("WINDOW_", StringComparison.Ordinal)) return Window(e, outs);
        throw new LoweringException($"expression kind {t}");
    }

    private string Cast(JsonElement e, IReadOnlyList<string> outs)
    {
        var child = e.GetProperty("child");
        var to = TypeId(e)!;
        // constants the binder widened to the context type read better as plain literals; the engine types them by context
        if (child.GetProperty("type").GetString() == "VALUE_CONSTANT" && !(child.GetProperty("value").TryGetProperty("is_null", out var n) && n.ValueKind == JsonValueKind.True))
        {
            var v = child.GetProperty("value");
            var lit = v.GetProperty("type").GetProperty("id").GetString()!;
            if (lit == to && to is "VARCHAR" or "INTEGER" or "BIGINT" or "SMALLINT" or "BOOLEAN" or "DOUBLE") return Literal(v);
            if (lit is "INTEGER" or "BIGINT" or "SMALLINT" && to is "INTEGER" or "BIGINT" or "SMALLINT" or "DECIMAL" or "DOUBLE" or "HUGEINT") return Literal(v);
            if (lit == "VARCHAR" && to == "BOOLEAN" && v.GetProperty("value").GetString() is "t" or "f" or "true" or "false") return v.GetProperty("value").GetString() is "t" or "true" ? "TRUE" : "FALSE";
            if (lit == "VARCHAR" && to is "DATE" or "TIMESTAMP" or "TIME") return $"{to} {Literal(v)}";
        }
        var fn = e.TryGetProperty("try_cast", out var tc) && tc.ValueKind == JsonValueKind.True ? "TRY_CAST" : "CAST";
        var inner = Expr(child, outs);
        // RULE try-cast-parse: parsing a string differs per engine ('' is NULL here, 0 on SQL Server, an error on PostgreSQL). The explicit VARCHAR cast marks the string source for the target step.
        if (fn == "TRY_CAST" && TypeId(child) == "VARCHAR" && to is "TINYINT" or "SMALLINT" or "INTEGER" or "BIGINT" or "DECIMAL" or "DOUBLE" or "FLOAT" or "DATE" or "TIMESTAMP")
        {
            inner = $"CAST({inner} AS VARCHAR)";
            rules.Add("try-cast-parse");
        }
        return $"{fn}({inner} AS {TypeName(e.GetProperty("return_type"))})";
    }

    private string Function(JsonElement e, IReadOnlyList<string> outs)
    {
        var name = Str(e, "name")!;
        var children = Arr(e, "children").ToList();
        if (name is "to_days" or "to_hours" or "to_minutes" or "to_seconds" or "to_months" or "to_years" && children.Count == 1)
        {
            // the binder turns INTERVAL 3 DAY into to_days(CAST(trunc(CAST(3 AS DOUBLE)) AS INTEGER)); fold the constant back
            var c = children[0];
            while (c.GetProperty("type").GetString() == "OPERATOR_CAST" || (c.GetProperty("type").GetString() == "BOUND_FUNCTION" && Str(c, "name") == "trunc"))
                c = c.GetProperty("type").GetString() == "OPERATOR_CAST" ? c.GetProperty("child") : Arr(c, "children").First();
            if (c.GetProperty("type").GetString() == "VALUE_CONSTANT") return $"INTERVAL {Literal(c.GetProperty("value"))} {name[3..^1].ToUpperInvariant()}";
            throw new LoweringException("an interval built from an expression");
        }
        if (name is "list_value" or "struct_pack" or "map") throw new LoweringException($"the nested-type function {name}");
        var a = children.Select(c => Expr(c, outs)).ToList();
        var returns = TypeId(e);
        // RULE round-double: DuckDB rounds the scaled double half away from zero; the engines do something else. The explicit DOUBLE cast marks it for the target step.
        if (name == "round" && a.Count is 1 or 2 && TypeId(children[0]) == "DOUBLE" && (a.Count == 1 || children[1].GetProperty("type").GetString() == "VALUE_CONSTANT"))
        {
            a[0] = $"CAST({a[0]} AS DOUBLE)";
            rules.Add("round-double");
        }
        var anyDate = children.Any(c => TypeId(c) == "DATE");
        string text;
        if (LikeFunctions.TryGetValue(name, out var like) && a.Count == 2) return $"({a[0]} {like} {a[1]})";
        if (Infix.Contains(name) && a.Count == 2) text = $"({a[0]} {name} {a[1]})";
        else if (name == "-" && a.Count == 1) return $"(-{a[0]})";
        else text = $"{name}({string.Join(", ", a)})";
        // RULE date-to-timestamp: DuckDB widens a DATE to TIMESTAMP in date_trunc and in interval arithmetic; the engines keep a DATE. Say so in the query.
        if (returns == "TIMESTAMP" && anyDate && name is "date_trunc" or "datetrunc" or "+" or "-")
        {
            rules.Add("date-to-timestamp");
            return $"CAST({text} AS TIMESTAMP)";
        }
        return text;
    }

    private static string? SumWidening(string? type) => type switch { "TINYINT" or "SMALLINT" or "INTEGER" => "BIGINT", "BIGINT" => "DECIMAL(38, 0)", _ => null };

    private string Aggregate(JsonElement e, IReadOnlyList<string> outs)
    {
        var name = Str(e, "name")!;
        var children = Arr(e, "children").ToList();
        var a = children.Select(c => Expr(c, outs)).ToList();
        // RULE avg-double: the result type is DOUBLE; the engines' own AVG over integers or decimals is not
        if (name == "avg" && TypeId(e) == "DOUBLE" && children.Count > 0 && TypeId(children[0]) != "DOUBLE")
        {
            a[0] = $"CAST({a[0]} AS DOUBLE)";
            rules.Add("avg-double");
        }
        // RULE sum-widen: DuckDB sums integers into a HUGEINT; SQL Server's SUM of an INT is an INT and raises an overflow error past 2^31 (a BIGINT's past 2^63). Widen the argument.
        if (name == "sum" && children.Count == 1 && SumWidening(TypeId(children[0])) is { } widened)
        {
            a[0] = $"CAST({a[0]} AS {widened})";
            rules.Add("sum-widen");
        }
        var shown = AggregateNames.GetValueOrDefault(name, name);
        var body = name == "count_star" ? "*" : (Str(e, "aggregate_type") == "DISTINCT" ? "DISTINCT " : "") + string.Join(", ", a);
        var s = $"{shown}({body})";
        return e.TryGetProperty("filter", out var f) ? $"{s} FILTER (WHERE {Expr(f, outs)})" : s;
    }

    private static readonly Dictionary<string, string> FrameBounds = new()
    {
        ["UNBOUNDED_PRECEDING"] = "UNBOUNDED PRECEDING", ["CURRENT_ROW_ROWS"] = "CURRENT ROW", ["CURRENT_ROW_RANGE"] = "CURRENT ROW", ["UNBOUNDED_FOLLOWING"] = "UNBOUNDED FOLLOWING",
    };

    private string Window(JsonElement e, IReadOnlyList<string> outs)
    {
        var type = e.GetProperty("type").GetString()!;
        var name = type switch
        {
            "WINDOW_ROW_NUMBER" => "row_number", "WINDOW_RANK" => "rank", "WINDOW_RANK_DENSE" => "dense_rank", "WINDOW_LEAD" => "lead", "WINDOW_LAG" => "lag",
            "WINDOW_FIRST_VALUE" => "first_value", "WINDOW_LAST_VALUE" => "last_value", "WINDOW_NTILE" => "ntile", "WINDOW_PERCENT_RANK" => "percent_rank", "WINDOW_CUME_DIST" => "cume_dist",
            _ => Str(e, "name"),
        } ?? throw new LoweringException($"the window function {type}");
        var windowChildren = Arr(e, "children").ToList();
        var args = windowChildren.Select(c => Expr(c, outs)).ToList();
        if (type == "WINDOW_AGGREGATE" && name == "sum" && windowChildren.Count == 1 && SumWidening(TypeId(windowChildren[0])) is { } widened)
        {
            args[0] = $"CAST({args[0]} AS {widened})";
            rules.Add("sum-widen");
        }
        // RULE avg-double also applies to avg(x) OVER (...)
        if (type == "WINDOW_AGGREGATE" && name == "avg" && TypeId(e) == "DOUBLE" && windowChildren.Count > 0 && TypeId(windowChildren[0]) != "DOUBLE")
        {
            args[0] = $"CAST({args[0]} AS DOUBLE)";
            rules.Add("avg-double");
        }
        if (type is "WINDOW_LEAD" or "WINDOW_LAG" && (e.TryGetProperty("offset_expr", out _) || e.TryGetProperty("default_expr", out _)))
        {
            args.Add(e.TryGetProperty("offset_expr", out var off) ? Expr(off, outs) : "1");
            if (e.TryGetProperty("default_expr", out var def)) args.Add(Expr(def, outs));
        }
        var parts = new List<string>();
        var partitions = Arr(e, "partitions").ToList();
        if (partitions.Count > 0) parts.Add("PARTITION BY " + string.Join(", ", partitions.Select(p => Expr(p, outs))));
        var orders = Arr(e, "orders").ToList();
        if (orders.Count > 0) parts.Add("ORDER BY " + string.Join(", ", orders.Select(o => OrderItem(o, outs))));
        var start = Str(e, "start");
        var end = Str(e, "end");
        // only RANGE ... CURRENT ROW is the default frame; ROWS ... CURRENT ROW is not (they differ when rows tie), so it is printed
        var isDefault = start is null or "UNBOUNDED_PRECEDING" && end is null or "CURRENT_ROW_RANGE" && !e.TryGetProperty("start_expr", out _) && !e.TryGetProperty("end_expr", out _);
        if (start != null && end != null && !isDefault)
        {
            string Bound(string kind, string field)
            {
                if (FrameBounds.TryGetValue(kind, out var fixedText)) return fixedText;
                if (kind.StartsWith("EXPR_PRECEDING", StringComparison.Ordinal)) return $"{Expr(e.GetProperty(field), outs)} PRECEDING";
                if (kind.StartsWith("EXPR_FOLLOWING", StringComparison.Ordinal)) return $"{Expr(e.GetProperty(field), outs)} FOLLOWING";
                throw new LoweringException($"the window frame bound {kind}");
            }
            var unit = start.EndsWith("ROWS", StringComparison.Ordinal) || end.EndsWith("ROWS", StringComparison.Ordinal) ? "ROWS" : "RANGE";
            parts.Add($"{unit} BETWEEN {Bound(start, "start_expr")} AND {Bound(end, "end_expr")}");
        }
        return $"{name}({string.Join(", ", args)}) OVER ({string.Join(" ", parts)})";
    }

    /// <summary>
    /// Null ordering is always written: DuckDB puts NULLs last, SQL Server first for ascending and PostgreSQL first for descending, so leaving it implicit would
    /// let the engines disagree about row order. A plain ascending key prints as `x NULLS LAST`.
    /// </summary>
    private string OrderItem(JsonElement o, IReadOnlyList<string> outs)
    {
        var desc = o.GetProperty("type").GetString() == "DESCENDING" ? " DESC" : "";
        var nulls = (Str(o, "null_order") ?? "NULLS_LAST").Replace('_', ' ');
        return $"{Expr(o.GetProperty("expression"), outs)}{desc} {nulls}";
    }

    // ---------------------------------------------------------------------------------------------------------------------------------------------------------
    // relations

    /// <param name="Outer">The column holds a value of the enclosing query (a correlated reference) or a pass-through of one.</param>
    private sealed record Item(string Sql, string? Alias, string? Type, bool Outer = false);

    private sealed class Rel
    {
        public List<Item> Sel { get; set; } = [];
        public string Frm { get; set; } = "";
        public List<string> Sources { get; set; } = [];
        public List<string> Where { get; } = [];
        public List<string> Group { get; set; } = [];
        public List<string> Having { get; } = [];
        public List<string> Order { get; set; } = [];
        public long? Limit { get; set; }
        public long? Offset { get; set; }
        public bool Distinct { get; set; }
        public bool HasWindow { get; set; }
        public bool HasAgg { get; set; }
        public bool Plain { get; set; }
        public bool IsDelim { get; set; }
        /// <summary>A correlated `LIMIT k` that DuckDB wrote as `row_number() OVER (PARTITION BY correlated values ORDER BY ...) <= k`: the select item and the ordering to restore.</summary>
        public (int Index, List<string> Orders)? LimitWindow { get; set; }
        public bool SuppressAliases { get; set; }
        public string? SetOp { get; set; }
        public bool SetOpDistinct { get; set; }
        public IReadOnlyList<string>? SetOpNames { get; set; }

        public IReadOnlyList<string> Outs() => Sel.Select(s => s.Sql).ToList();
        public bool Mergeable() => Limit == null && Offset == null && !Distinct && SetOp == null;

        // a marker is `\0source\u0001column\0`, qualified only in a block with several sources; `\u0002...\u0002` is a reference to the enclosing query and is always qualified
        private bool qualifyAll;

        public string Render(string text)
        {
            var multi = Sources.Count > 1 || qualifyAll;
            return Regex.Replace(text, "([\0\u0002])([^\u0001\0\u0002]*)\u0001([^\0\u0002]*)[\0\u0002]", m => (multi || m.Groups[1].Value == "\u0002" ? Ident(m.Groups[2].Value) + "." : "") + Ident(m.Groups[3].Value));
        }

        private bool HasOuterReference() =>
            Sel.Any(i => i.Sql.Contains('\u0002')) || Frm.Contains('\u0002') || Where.Any(x => x.Contains('\u0002')) || Group.Any(x => x.Contains('\u0002')) ||
            Having.Any(x => x.Contains('\u0002')) || Order.Any(x => x.Contains('\u0002'));

        public Rel Clone() => new Rel()
        {
            Sel = [.. Sel], Frm = Frm, Sources = [.. Sources], Group = [.. Group], Order = [.. Order], Limit = Limit, Offset = Offset, Distinct = Distinct, HasWindow = HasWindow,
            HasAgg = HasAgg, Plain = Plain, IsDelim = IsDelim, LimitWindow = LimitWindow, SuppressAliases = SuppressAliases, SetOp = SetOp, SetOpDistinct = SetOpDistinct, SetOpNames = SetOpNames,
        }.CopyPredicates(this);

        private Rel CopyPredicates(Rel from) { Where.AddRange(from.Where); Having.AddRange(from.Having); return this; }

        private static readonly Regex Plainref = new("^[\0\u0002][^\u0001\0\u0002]*\u0001([^\0\u0002]*)[\0\u0002]$");

        public IReadOnlyList<string> Aliases()
        {
            var used = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<string>();
            for (var i = 0; i < Sel.Count; i++)
            {
                var m = Plainref.Match(Sel[i].Sql);
                var n = Sel[i].Alias ?? (m.Success ? m.Groups[1].Value : $"col{i + 1}");
                var baseName = n; var k = 1;
                while (!used.Add(n)) n = $"{baseName}_{++k}";
                result.Add(n);
            }
            return result;
        }

        public string Sql()
        {
            if (SetOp != null) return SetOp;
            qualifyAll = HasOuterReference();
            var names = Aliases();
            var items = new List<string>();
            for (var i = 0; i < Sel.Count; i++)
            {
                var m = Plainref.Match(Sel[i].Sql);
                var needsAs = (!m.Success && !SuppressAliases) || (m.Success && m.Groups[1].Value != names[i]);
                items.Add(Render(Sel[i].Sql) + (needsAs ? $" AS {Ident(names[i])}" : ""));
            }
            var sb = new StringBuilder($"SELECT {(Distinct ? "DISTINCT " : "")}{string.Join(", ", items)}");
            if (Frm.Length > 0) sb.Append("\nFROM ").Append(Render(Frm));
            if (Where.Count > 0) sb.Append("\nWHERE ").Append(Render(string.Join(" AND ", Where)));
            if (Group.Count > 0) sb.Append("\nGROUP BY ").Append(Render(string.Join(", ", Group)));
            if (Having.Count > 0) sb.Append("\nHAVING ").Append(Render(string.Join(" AND ", Having)));
            if (Order.Count > 0) sb.Append("\nORDER BY ").Append(Render(string.Join(", ", Order)));
            if (Limit != null) sb.Append("\nLIMIT ").Append(Limit);
            if (Offset is > 0) sb.Append(" OFFSET ").Append(Offset);
            return sb.ToString();
        }

        public string Cte(List<(string Name, string Sql)> ctes) =>
            ctes.Count == 0 ? "" : "WITH " + string.Join(",\n", ctes.Select(c => $"{c.Name} AS (\n{Indent(c.Sql)}\n)")) + "\n";

        private static string Indent(string s) => string.Join('\n', s.Split('\n').Select(l => "  " + l));
    }

    private static string IndentText(string s) => string.Join('\n', s.Split('\n').Select(l => "  " + l));

    private string Fresh() => $"s{++aliasCounter}";

    private Rel Wrap(Rel rel)
    {
        var a = Fresh();
        var names = rel.SetOp != null ? rel.SetOpNames! : rel.Aliases();
        var types = rel.Sel.Select(s => s.Type).ToList();
        return new Rel
        {
            Frm = $"(\n{IndentText(rel.Sql())}\n) AS {a}",
            Sources = [a],
            Sel = names.Select((n, i) => new Item(Token(a, n), n, i < types.Count ? types[i] : null, rel.Sel.Count > i && rel.Sel[i].Outer)).ToList(),
        };
    }

    /// <summary>
    /// `generate_series(a, b[, s])` and `range(...)` over integer constants become the engines' own GENERATE_SERIES (SQL Server 2022, PostgreSQL), with
    /// `range`'s exclusive end turned into an inclusive one. The column is cast to BIGINT because DuckDB's is, and SQL Server's would otherwise be INT.
    /// Date and timestamp series, UNNEST and any other table function are refused: the engines have no equal.
    /// </summary>
    private Rel Series(JsonElement p)
    {
        var name = Str(p, "name");
        if (name is not ("generate_series" or "range")) throw new LoweringException($"the table function {name ?? "(unnamed)"}");
        var args = new List<long>();
        foreach (var a in Arr(p, "parameters"))
        {
            var type = a.TryGetProperty("type", out var ty) && ty.TryGetProperty("id", out var id) ? id.GetString() : null;
            if (type is null || !IntegerTypes.Contains(type) || type is "HUGEINT" or "UBIGINT" || (a.TryGetProperty("is_null", out var n) && n.ValueKind == JsonValueKind.True))
                throw new LoweringException($"{name} over {type ?? "a non-constant"} values (only integer series are lowered)");
            args.Add(a.GetProperty("value").GetInt64());
        }
        if (args.Count is < 1 or > 3 || (name == "generate_series" && args.Count < 2)) throw new LoweringException($"{name} with {args.Count} argument(s)");
        var (start, stop, step) = args.Count switch { 1 => (0L, args[0], 1L), 2 => (args[0], args[1], 1L), _ => (args[0], args[1], args[2]) };
        if (step == 0) throw new LoweringException($"{name} with a step of zero");
        if (name == "range") stop += step > 0 ? -1 : 1;     // range excludes its end
        var column = p.GetProperty("names").EnumerateArray().First().GetString()!;
        uses["series"] = uses.GetValueOrDefault("series") + 1;     // range is a keyword on SQL Server, so both functions share one alias
        var alias = uses["series"] == 1 ? "series" : $"series_{uses["series"]}";
        var call = $"generate_series({start}, {stop}{(step == 1 ? "" : $", {step}")})";
        return new Rel
        {
            // the column is named `value`, which is what SQL Server calls it: it takes no column list after a function, so the renderer drops this one there
            Frm = $"{call} AS {alias}(value)",
            Sources = [alias],
            Sel = [new Item($"CAST({Token(alias, "value")} AS BIGINT)", column, "BIGINT")],
        };
    }

    private Rel Node(JsonElement p)
    {
        var t = p.GetProperty("type").GetString()!;
        var kids = Arr(p, "children").ToList();
        switch (t)
        {
            case "LOGICAL_GET":
            {
                var fd = p.TryGetProperty("function_data", out var f) ? f : default;
                if (fd.ValueKind != JsonValueKind.Object || !fd.TryGetProperty("table", out var table)) return Series(p);
                var names = p.GetProperty("names").EnumerateArray().Select(x => x.GetString()!).ToList();
                var types = p.GetProperty("returned_types").EnumerateArray().Select(TypeName).ToList();
                // an index past the columns is DuckDB's virtual row-id column, which a query that selects no column of the table (EXISTS (SELECT 1 FROM u)) still scans
                var idx = Arr(p, "column_indexes").Select(c => c.GetProperty("index").TryGetInt32(out var i) && i < names.Count ? i : -1).ToList();
                if (idx.Count == 0) idx = Enumerable.Range(0, names.Count).ToList();
                var baseName = table.GetString()!;
                uses[baseName] = uses.GetValueOrDefault(baseName) + 1;
                var alias = uses[baseName] == 1 ? baseName : $"{baseName}_{uses[baseName]}";
                var schema = Str(fd, "schema");
                aliasTables[alias] = schema is null or "main" ? baseName : $"{schema}.{baseName}";
                var rel = new Rel
                {
                    Frm = (schema is null or "main" ? baseName : $"{schema}.{baseName}") + (alias == baseName ? "" : $" AS {alias}"),
                    Sources = [alias],
                    Plain = true,
                    Sel = idx.Select(i => i < 0 ? new Item("NULL", null, "BIGINT") : new Item(Token(alias, names[i]), names[i], types[i])).ToList(),
                };
                return rel;
            }
            case "LOGICAL_DUMMY_SCAN": return new Rel();
            case "LOGICAL_PROJECTION" when TryUncorrelatedScalar(p, kids) is { } constant: return constant;
            case "LOGICAL_PROJECTION" when TryCountPatch(p, kids) is { } counted: return counted;
            case "LOGICAL_PROJECTION":
            {
                var c = Node(kids[0]);
                if (!c.Mergeable()) c = Wrap(c);
                var outs = c.Outs();
                var childItems = c.Sel;
                c.Sel = Arr(p, "expressions").Select(e => new Item(Expr(e, outs), Str(e, "alias"), TypeNameOf(e), IsOuterRef(e, childItems))).ToList();
                c.Plain = false;
                if (c.Sel.Any(s => s.Sql.Contains(" OVER (", StringComparison.Ordinal))) c.HasWindow = true;
                return c;
            }
            case "LOGICAL_FILTER":
            {
                var c = Node(kids[0]);
                if (c.LimitWindow is { } lw)
                {
                    var preds0 = Arr(p, "expressions").ToList();
                    var ok = preds0.Count == 1 && Str(preds0[0], "type") == "COMPARE_LESSTHANOREQUALTO" && preds0[0].GetProperty("left").GetProperty("type").GetString() == "BOUND_REF" &&
                             preds0[0].GetProperty("left").GetProperty("index").GetInt32() == lw.Index && preds0[0].GetProperty("right").GetProperty("type").GetString() == "VALUE_CONSTANT";
                    if (!ok) throw new LoweringException("a correlated LIMIT with an offset or another shape of row filter");
                    c.Order = lw.Orders;
                    c.Limit = preds0[0].GetProperty("right").GetProperty("value").GetProperty("value").GetInt64();
                    c.LimitWindow = null;
                    return c;
                }
                if (c.HasWindow || !c.Mergeable()) c = Wrap(c);
                var preds = Arr(p, "expressions").Select(e => Expr(e, c.Outs())).ToList();
                if (c.HasAgg && c.Having.Count == 0 && !c.Plain) c.Having.AddRange(preds); else c.Where.AddRange(preds);
                return c;
            }
            case "LOGICAL_AGGREGATE_AND_GROUP_BY":
            {
                var c = Node(kids[0]);
                if (!c.Mergeable() || c.HasWindow || c.HasAgg || c.Group.Count > 0 || c.Having.Count > 0) c = Wrap(c);
                var outs = c.Outs();
                var childItems = c.Sel;
                var groups = Arr(p, "groups").Select(g => (Sql: Expr(g, outs), Type: TypeNameOf(g), Outer: IsOuterRef(g, childItems))).ToList();
                var aggs = Arr(p, "expressions").Select(a => (Sql: Expr(a, outs), Type: TypeNameOf(a), Outer: false)).ToList();
                if (groups.Count == 0 && aggs.Count == 0) throw new LoweringException("an empty aggregate");
                // a group on a value of the enclosing query is constant for each outer row (DuckDB added it when it decorrelated the subquery), so it is not a GROUP BY any more
                if (groups.Count > 0 && groups.All(g => g.Outer) && aggs.Count == 0) throw new LoweringException("an aggregate that only groups by correlated values");
                c.Group = groups.Where(g => !g.Outer).Select(g => g.Sql).ToList();
                c.Sel = groups.Concat(aggs).Select(x => new Item(x.Sql, null, x.Type, x.Outer)).ToList();
                c.HasAgg = true;
                c.Plain = false;
                return c;
            }
            case "LOGICAL_ORDER_BY":
            {
                var c = Node(kids[0]);
                if (c.Limit != null) c = Wrap(c);
                c.Order = Arr(p, "orders").Select(o => OrderItem(o, c.Outs())).ToList();
                return c;
            }
            case "LOGICAL_LIMIT":
            {
                var c = Node(kids[0]);
                if (c.Limit != null || c.Offset != null) c = Wrap(c);
                long? Val(string k)
                {
                    if (!p.TryGetProperty(k, out var v) || Str(v, "type") == "UNSET") return null;
                    if (Str(v, "type") == "CONSTANT_VALUE" && v.TryGetProperty("constant_percentage", out var pc) && pc.GetDouble() == -1) return v.GetProperty("constant_integer").GetInt64();
                    throw new LoweringException("a limit that is not a plain constant (a percentage or an expression)");
                }
                c.Limit = Val("limit_val");
                c.Offset = Val("offset_val");
                return c;
            }
            case "LOGICAL_DISTINCT":
            {
                var c = Node(kids[0]);
                if (Str(p, "distinct_type") == "DISTINCT_ON") return DistinctOn(p, c);
                if (Str(p, "distinct_type") != "DISTINCT") throw new LoweringException($"the DISTINCT form {Str(p, "distinct_type")}");
                if (c.SetOp != null && c.SetOpDistinct) return c; // a set operation without ALL is already distinct
                if (!c.Mergeable() || c.HasWindow) c = Wrap(c);
                c.Distinct = true;
                return c;
            }
            case "LOGICAL_DELIM_GET":
            {
                if (delimFrames.Count == 0) throw new LoweringException("a correlated reference outside a subquery");
                return new Rel { IsDelim = true, Sel = delimFrames.Peek().Select(i => i with { Outer = true }).ToList() };
            }
            case "LOGICAL_DELIM_JOIN": return DelimJoin(p, kids);
            case "LOGICAL_COMPARISON_JOIN" when Str(p, "join_type") == "MARK": return MarkJoin(p, kids);
            case "LOGICAL_COMPARISON_JOIN" or "LOGICAL_CROSS_PRODUCT":
            {
                var l = Node(kids[0]);
                var r = Node(kids[1]);
                if (l.IsDelim || r.IsDelim) return JoinWithDelim(p, t, l, r);
                if (t == "LOGICAL_CROSS_PRODUCT" && (IsConstantRow(l) || IsConstantRow(r)))
                {
                    // a subquery that returns one value: it is an expression of the other side, not a table
                    var host = IsConstantRow(l) ? r : l;
                    host.Sel = l.Sel.Concat(r.Sel).ToList();
                    host.Plain = false;
                    return host;
                }
                // an aggregate without groups returns exactly one row: next to another relation it is a scalar subquery, and reads like the one the author wrote
                if (t == "LOGICAL_CROSS_PRODUCT" && (IsSingleRow(r) || IsSingleRow(l)) && !(IsSingleRow(r) && IsSingleRow(l)))
                {
                    var hostIsLeft = IsSingleRow(r);
                    var (host, single) = hostIsLeft ? (l, r) : (r, l);
                    if (!host.Mergeable() || host.HasWindow) host = Wrap(host);
                    var scalars = single.Sel.Select(i => new Item($"(\n{SubqueryWith(single, i.Sql)}\n)", null, i.Type)).ToList();
                    host.Sel = (hostIsLeft ? host.Sel.Concat(scalars) : scalars.Concat(host.Sel)).ToList();
                    host.Plain = false;
                    return host;
                }
                if (!l.Plain) l = Wrap(l);
                if (!r.Plain) r = Wrap(r);
                var rel = new Rel { Sources = l.Sources.Concat(r.Sources).ToList(), Sel = l.Sel.Concat(RightColumns(p, r.Sel)).ToList() };
                if (t == "LOGICAL_CROSS_PRODUCT") { rel.Frm = $"{l.Frm}\nCROSS JOIN {r.Frm}"; return rel; }
                var joinType = Str(p, "join_type") switch { "INNER" => "JOIN", "LEFT" => "LEFT JOIN", "RIGHT" => "RIGHT JOIN", "OUTER" or "FULL" => "FULL JOIN", var other => throw new LoweringException($"the join type {other}") };
                if (p.TryGetProperty("expression", out _)) throw new LoweringException("a join with an extra expression");
                var lo = l.Outs(); var ro = r.Outs();
                var conditions = Arr(p, "conditions").Select(c => $"({Expr(c.GetProperty("left"), lo)} {Comparisons[Str(c, "comparison")!]} {Expr(c.GetProperty("right"), ro)})");
                rel.Frm = $"{l.Frm}\n{joinType} {r.Frm}\n  ON {string.Join(" AND ", conditions)}";
                return rel;
            }
            case "LOGICAL_WINDOW":
            {
                var c = Node(kids[0]);
                if (!c.Mergeable()) c = Wrap(c);
                var outs = c.Outs();
                var childItems = c.Sel;
                var first = c.Sel.Count;
                var items = new List<Item>();
                foreach (var e in Arr(p, "expressions"))
                {
                    var partitions = Arr(e, "partitions").ToList();
                    var correlated = partitions.Count(x => IsOuterRef(x, childItems));
                    if (correlated > 0)
                    {
                        // `LIMIT k` inside a correlated subquery: DuckDB numbers the rows per correlated value. Undo that when it is only that.
                        if (correlated != partitions.Count || e.GetProperty("type").GetString() != "WINDOW_ROW_NUMBER") throw new LoweringException("a window function partitioned by a correlated value");
                        c.LimitWindow = (first + items.Count, Arr(e, "orders").Select(o => OrderItem(o, outs)).ToList());
                        items.Add(new Item("NULL", null, TypeNameOf(e)));
                    }
                    else items.Add(new Item(Window(e, outs), null, TypeNameOf(e)));
                }
                c.Sel = c.Sel.Concat(items).ToList();
                if (items.Any(i => i.Sql != "NULL")) c.HasWindow = true;
                return c;
            }
            case "LOGICAL_MATERIALIZED_CTE":
            {
                var definition = Node(kids[0]);
                ctes[p.GetProperty("table_index").GetInt32()] = (Str(p, "ctename")!, definition);
                ctesInOrder.Add((Str(p, "ctename")!, definition.Sql()));   // before the main query, which may define more CTEs that read this one
                return Node(kids[1]);
            }
            case "LOGICAL_CTE_REF":
            {
                var (name, definition) = ctes[p.GetProperty("cte_index").GetInt32()];
                var key = "cte:" + name;
                uses[key] = uses.GetValueOrDefault(key) + 1;
                var alias = uses[key] == 1 ? name : $"{name}_{uses[key]}";
                var names = definition.SetOp != null ? definition.SetOpNames! : definition.Aliases();
                return new Rel
                {
                    Frm = name + (alias == name ? "" : $" AS {alias}"),
                    Sources = [alias],
                    Plain = true,
                    Sel = names.Select((n, i) => new Item(Token(alias, n), n, i < definition.Sel.Count ? definition.Sel[i].Type : null)).ToList(),
                };
            }
            case "LOGICAL_UNION" or "LOGICAL_INTERSECT" or "LOGICAL_EXCEPT":
            {
                var a = Node(kids[0]);
                var b = Node(kids[1]);
                var op = t switch { "LOGICAL_UNION" => "UNION", "LOGICAL_INTERSECT" => "INTERSECT", _ => "EXCEPT" };
                var all = p.TryGetProperty("setop_all", out var sa) && sa.ValueKind == JsonValueKind.True;
                var names = a.SetOp != null ? a.SetOpNames! : a.Aliases();
                return new Rel
                {
                    SetOp = $"{a.Sql()}\n{op}{(all ? " ALL" : "")}\n{b.Sql()}",
                    SetOpDistinct = !all,
                    SetOpNames = names,
                    Sel = names.Select((n, i) => new Item(Token("u", n), n, i < a.Sel.Count ? a.Sel[i].Type : null)).ToList(),
                };
            }
        }
        throw new LoweringException($"the plan operator {t[8..]}");
    }

    // ---------------------------------------------------------------------------------------------------------------------------------------------------------
    // subqueries
    //
    // DuckDB's binder has already decorrelated every subquery: an EXISTS is a DELIM_JOIN of type MARK, a scalar subquery a SINGLE join, a LATERAL an INNER or LEFT one, and the
    // right side reads the outer query's values through DELIM_GET. Here that is undone. Each DELIM_GET column is a reference to the outer expression that feeds it, the
    // joins back to the duplicate-eliminated values and the GROUP BYs on them are dropped (they are constant per outer row), and the right side is printed as a nested
    // query that mentions the outer columns, which is what the author wrote.

    private readonly Stack<List<Item>> delimFrames = new();

    private static bool IsOuterRef(JsonElement e, List<Item> childItems) =>
        e.GetProperty("type").GetString() == "BOUND_REF" && e.GetProperty("index").GetInt32() is var i && i < childItems.Count && childItems[i].Outer;

    /// <summary>A reference to a column of an enclosing block: printed qualified wherever it appears.</summary>
    private static string Requalify(string text) => text.Replace('\0', '\u0002');
    private static string Unqualify(string text) => text.Replace('\u0002', '\0');

    /// <summary>A block that is an aggregate with no grouping and nothing after it: it returns exactly one row.</summary>
    private static bool IsSingleRow(Rel r) => r.HasAgg && r.Group.Count == 0 && r.Having.Count == 0 && !r.HasWindow && r.SetOp == null && !r.Distinct && r.Limit == null && r.Offset == null && r.Sel.All(i => !i.Outer);

    private static string Nested(Rel r)
    {
        var sql = r.Sql();
        return string.Join('\n', sql.Split('\n').Select(l => "  " + l));
    }

    private static string SubqueryWith(Rel r, string selectSql)
    {
        var copy = r.Clone();
        copy.Sel = [new Item(selectSql, null, null)];
        copy.SuppressAliases = true;                      // a subquery's column has no name worth writing
        return Nested(copy);
    }

    private Rel DelimJoin(JsonElement p, List<JsonElement> kids)
    {
        var left = Node(kids[0]);
        // the subquery becomes part of the select list or WHERE of the outer block, so that block must be able to hold it: not a DISTINCT, LIMIT, set operation or window
        if (!left.Mergeable() || left.HasWindow) left = Wrap(left);
        var lo = left.Outs();
        var duplicate = Arr(p, "duplicate_eliminated_columns").Select(e => new Item(Requalify(Expr(e, lo)), null, TypeNameOf(e), Outer: true)).ToList();
        if (left.HasAgg && duplicate.Any(d => !Regex.IsMatch(d.Sql, "^\u0002[^\u0001\0\u0002]*\u0001[^\0\u0002]*\u0002$")))
        {
            left = Wrap(left);         // correlated on an aggregate: it has to be a column of something
            lo = left.Outs();
            duplicate = Arr(p, "duplicate_eliminated_columns").Select(e => new Item(Requalify(Expr(e, lo)), null, TypeNameOf(e), Outer: true)).ToList();
        }
        delimFrames.Push(duplicate);
        Rel right;
        try { right = Node(kids[1]); }
        finally { delimFrames.Pop(); }

        var joinType = Str(p, "join_type")!;
        if (right.SetOp != null) throw new LoweringException("a correlated subquery over a set operation (UNION, INTERSECT, EXCEPT)");
        var ro = right.Outs();
        // conditions that tie a duplicate-eliminated column to its carried copy on the right are plumbing; any other condition is the comparison of an IN
        var real = new List<(string Left, string Op, string Right, int RightIndex)>();
        foreach (var c in Arr(p, "conditions"))
        {
            var r = c.GetProperty("right");
            var carried = r.GetProperty("type").GetString() == "BOUND_REF" && right.Sel[r.GetProperty("index").GetInt32()].Outer;
            if (carried) continue;
            real.Add((Expr(c.GetProperty("left"), lo), Str(c, "comparison")!, Expr(r, ro), r.TryGetProperty("index", out var ix) ? ix.GetInt32() : -1));
        }

        switch (joinType)
        {
            case "MARK":
            {
                string mark;
                if (real.Count == 0) mark = $"EXISTS (\n{SubqueryWith(right, "1")}\n)";
                else if (real.Count == 1 && real[0].Op == "COMPARE_EQUAL") mark = $"({real[0].Left} IN (\n{SubqueryWith(right, real[0].Right)}\n))";
                else throw new LoweringException("a subquery comparison other than IN and EXISTS");
                left.Sel = left.Sel.Append(new Item(mark, null, "BOOLEAN")).ToList();
                left.Plain = false;
                return left;
            }
            case "SINGLE":
            {
                var items = new List<Item>();
                for (var i = 0; i < right.Sel.Count; i++)
                {
                    if (!Projected(p, i)) continue;
                    var item = right.Sel[i];
                    if (item.Outer)
                    {
                        // the carried copy of an outer value is that outer value
                        var link = Arr(p, "conditions").FirstOrDefault(c => c.GetProperty("right").GetProperty("type").GetString() == "BOUND_REF" && c.GetProperty("right").GetProperty("index").GetInt32() == i);
                        items.Add(new Item(link.ValueKind == JsonValueKind.Object ? Expr(link.GetProperty("left"), lo) : Unqualify(item.Sql), null, item.Type, Outer: false));
                    }
                    else items.Add(new Item($"(\n{SubqueryWith(right, item.Sql)}\n)", null, item.Type));
                }
                left.Sel = left.Sel.Concat(items).ToList();
                left.Plain = false;
                return left;
            }
            case "INNER" or "LEFT":
            {
                if (real.Count > 0) throw new LoweringException("a lateral join with a comparison");
                if (!left.Plain && (joinType == "LEFT" || !left.Mergeable() || left.HasAgg || left.HasWindow || left.Where.Count > 0)) left = Wrap(left);
                var alias = Fresh();
                var rightNames = right.Aliases();
                var lateral = Nested(right);
                var rel = new Rel
                {
                    Sources = left.Sources.Append(alias).ToList(),
                    Frm = $"{left.Frm}\n{(joinType == "LEFT" ? "LEFT JOIN" : "CROSS JOIN")} LATERAL (\n{lateral}\n) AS {alias}{(joinType == "LEFT" ? " ON TRUE" : "")}",
                    Sel = left.Sel.Concat(rightNames.Select((n, i) => (n, i)).Where(x => Projected(p, x.i)).Select(x => new Item(Token(alias, x.n), x.n, right.Sel[x.i].Type))).ToList(),
                };
                rel.Where.AddRange(left.Where);
                return rel;
            }
        }
        throw new LoweringException($"a subquery of join type {joinType}");
    }

    /// <summary>
    /// DuckDB 2.0 guards a correlated `count` against the COUNT bug: an outer key with no rows must give 0, not NULL. Its plan is the grouped count LEFT-joined to the
    /// distinct outer keys, LEFT-joined again to a count over an empty result, and a CASE that picks the empty count when the key found nothing:
    /// <code>PROJECTION [CASE WHEN marker IS NULL THEN empty_count ELSE count END, key]  over  LEFT(LEFT(DELIM_GET, AGG(count, count GROUP BY key)), AGG(count OVER EMPTY))</code>
    /// A scalar subquery `(SELECT count(*) ...)` already yields 0 over no rows, so the whole shape is just the grouped aggregate: this returns its count and the carried key,
    /// which is what the 1.x plan had.
    /// </summary>
    /// <summary>
    /// DuckDB 2.0 lists the columns of the right side that a join outputs in `right_projection_map` (it drops the carried copy of a correlated key, for example). Absent, as in
    /// 1.x, every column is output.
    /// </summary>
    private static IReadOnlyList<Item> RightColumns(JsonElement p, IReadOnlyList<Item> right) =>
        p.TryGetProperty("right_projection_map", out var map) && map.ValueKind == JsonValueKind.Array && map.GetArrayLength() > 0
            ? map.EnumerateArray().Select(i => right[i.GetInt32()]).ToList() : right;

    private static bool Projected(JsonElement p, int rightIndex) =>
        !(p.TryGetProperty("right_projection_map", out var map) && map.ValueKind == JsonValueKind.Array && map.GetArrayLength() > 0) || map.EnumerateArray().Any(i => i.GetInt32() == rightIndex);

    private Rel? TryCountPatch(JsonElement p, List<JsonElement> kids)
    {
        if (kids.Count != 1 || Str(kids[0], "type") != "LOGICAL_COMPARISON_JOIN" || Str(kids[0], "join_type") != "LEFT" || Arr(kids[0], "conditions").Any()) return null;
        var outer = Arr(kids[0], "children").ToList();
        if (outer.Count != 2 || Str(outer[1], "type") != "LOGICAL_AGGREGATE_AND_GROUP_BY" || Arr(outer[1], "groups").Any()) return null;
        var emptyKids = Arr(outer[1], "children").ToList();
        if (emptyKids.Count != 1 || Str(emptyKids[0], "type") != "LOGICAL_EMPTY_RESULT") return null;
        var inner = outer[0];
        if (Str(inner, "type") != "LOGICAL_COMPARISON_JOIN" || Str(inner, "join_type") != "LEFT" || Arr(inner, "conditions").Any(c => Str(c, "comparison") != "COMPARE_NOT_DISTINCT_FROM")) return null;
        var inside = Arr(inner, "children").ToList();
        if (inside.Count != 2 || Str(inside[0], "type") != "LOGICAL_DELIM_GET" || Str(inside[1], "type") != "LOGICAL_AGGREGATE_AND_GROUP_BY") return null;
        if (!Arr(outer[1], "expressions").All(e => Str(e, "name") is "count_star" or "count")) throw new LoweringException("a correlated aggregate that is not a count, over a COUNT-bug guard");

        var delim = Node(inside[0]);
        var agg = Node(inside[1]);
        var items = delim.Sel.Concat(agg.Sel).Append(new Item("0", null, "BIGINT")).ToList();
        var outs = items.Select(i => i.Sql).ToList();
        var result = new List<Item>();
        foreach (var e in Arr(p, "expressions"))
        {
            if (Str(e, "type") == "CASE_EXPR" && Arr(e, "case_checks").ToList() is [{ } check] && IsGuardCase(e, check, delim.Sel.Count, items.Count - 1, out var count))
                result.Add(items[count] with { Outer = false });
            else
                result.Add(new Item(Expr(e, outs), Str(e, "alias"), TypeNameOf(e), IsOuterRef(e, items)));
        }
        var rel = agg.Clone();
        rel.Sel = result;
        rel.Plain = false;
        return rel;
    }

    /// <summary>`CASE WHEN #marker IS NULL THEN #empty ELSE #count END`: the marker is a column of the grouped side, the empty count is the last column.</summary>
    private static bool IsGuardCase(JsonElement e, JsonElement check, int firstAggColumn, int emptyColumn, out int count)
    {
        count = -1;
        var when = check.GetProperty("when_expr");
        var then = check.GetProperty("then_expr");
        var other = e.GetProperty("else_expr");
        if (Str(when, "type") != "OPERATOR_IS_NULL" || Arr(when, "children").FirstOrDefault() is not { ValueKind: JsonValueKind.Object } m || Str(m, "type") != "BOUND_REF") return false;
        if (Str(then, "type") != "BOUND_REF" || then.GetProperty("index").GetInt32() != emptyColumn || Str(other, "type") != "BOUND_REF") return false;
        count = other.GetProperty("index").GetInt32();
        return m.GetProperty("index").GetInt32() >= firstAggColumn && count >= firstAggColumn;
    }

    /// <summary>
    /// A MARK join that is not wrapped in a delim join: how DuckDB 2.0 hands over EXISTS and IN. Its decorrelation has already happened, so the correlated predicates are join
    /// conditions between the outer (left) and the subquery (right) side. An EQUAL condition is the comparison of an IN; the other conditions are the correlation and go back into
    /// the subquery's WHERE as predicates on the outer columns. The null-safe equality (NOT DISTINCT FROM) DuckDB uses for a correlated `=` is written as `=` again when the
    /// subquery side already filters that column to not NULL, which is exactly what makes the two the same.
    /// </summary>
    private Rel MarkJoin(JsonElement p, List<JsonElement> kids)
    {
        var left = Node(kids[0]);
        if (!left.Mergeable() || left.HasWindow) left = Wrap(left);
        var right = Node(kids[1]);
        if (right.SetOp != null) throw new LoweringException("a correlated subquery over a set operation (UNION, INTERSECT, EXCEPT)");
        var lo = left.Outs();
        var ro = right.Outs();

        string? inLeft = null, inRight = null;
        var correlated = new List<string>();
        foreach (var c in Arr(p, "conditions"))
        {
            var op = Str(c, "comparison")!;
            var l = Expr(c.GetProperty("left"), lo);
            var r = Expr(c.GetProperty("right"), ro);
            if (op == "COMPARE_EQUAL")
            {
                if (inLeft != null) throw new LoweringException("a subquery comparison other than IN and EXISTS");
                (inLeft, inRight) = (l, r);
                continue;
            }
            if (right.HasAgg || right.HasWindow || !right.Mergeable()) throw new LoweringException("a correlated predicate over an aggregate");
            var text = Comparisons.TryGetValue(op, out var sym) ? sym : throw new LoweringException("a subquery comparison other than IN and EXISTS");
            if (op == "COMPARE_NOT_DISTINCT_FROM")
            {
                var guard = $"({r} IS NOT NULL)";
                if (right.Where.Remove(guard)) text = "=";
                else text = "IS NOT DISTINCT FROM";
            }
            // written the way a subquery is usually written, its own column first: `u.a = t.a`, `u.b < t.b`
            var flipped = text switch { "<" => ">", ">" => "<", "<=" => ">=", ">=" => "<=", _ => text };
            correlated.Add($"({r} {flipped} {Requalify(l)})");
        }
        if (correlated.Count > 0)
        {
            right = right.Clone();
            right.Where.AddRange(correlated);
        }

        var mark = inLeft == null ? $"EXISTS (\n{SubqueryWith(right, "1")}\n)" : $"({inLeft} IN (\n{SubqueryWith(right, inRight!)}\n))";
        left.Sel = left.Sel.Append(new Item(mark, null, "BOOLEAN")).ToList();
        left.Plain = false;
        return left;
    }

    /// <summary>A join where one side is the duplicate-eliminated outer values: those are not a table here, they are the outer query, so the join disappears.</summary>
    private Rel JoinWithDelim(JsonElement p, string type, Rel l, Rel r)
    {
        var other = l.IsDelim ? r : l;
        if (l.IsDelim && r.IsDelim) throw new LoweringException("a join of two sets of correlated values");
        if (type == "LOGICAL_COMPARISON_JOIN")
        {
            var jt = Str(p, "join_type");
            if (jt is not ("INNER" or "LEFT") || (jt == "LEFT" && !l.IsDelim)) throw new LoweringException($"the correlated join type {jt}");
            var lo = l.Outs(); var ro = r.Outs();
            foreach (var c in Arr(p, "conditions"))
            {
                var le = c.GetProperty("left"); var re = c.GetProperty("right");
                var bothOuter = IsOuterRef(le, l.Sel) && IsOuterRef(re, r.Sel);
                if (bothOuter) continue;      // the join back to the outer value: true by construction
                if (other.HasAgg) throw new LoweringException("a correlated predicate over an aggregate");
                other.Where.Add($"({Expr(le, lo)} {Comparisons[Str(c, "comparison")!]} {Expr(re, ro)})");
            }
            if (p.TryGetProperty("expression", out _)) throw new LoweringException("a correlated join with an extra expression");
        }
        var sel = l.Sel.Concat(RightColumns(p, r.Sel)).ToList();
        other.Sel = sel;
        other.Plain = false;
        return other;
    }

    /// <summary>
    /// `DISTINCT ON (k) ... ORDER BY k, o` is `row_number() OVER (PARTITION BY k ORDER BY o) = 1` over the same rows. DuckDB picks an arbitrary row when the ordering leaves ties,
    /// and so would a window, so the result would differ between engines: it is lowered only when the ordering provably decides the row.
    /// </summary>
    private Rel DistinctOn(JsonElement p, Rel c)
    {
        if (!p.TryGetProperty("order_by", out var orderBy) || !orderBy.TryGetProperty("orders", out var ordersElement)) throw new LoweringException("DISTINCT ON without an ORDER BY (the row kept would be arbitrary)");
        if (c.SetOp != null) throw new LoweringException("DISTINCT ON over a set operation");
        if (!c.Mergeable() || c.HasWindow || c.HasAgg) throw new LoweringException("DISTINCT ON over a query that groups, limits or has windows of its own");
        var outs = c.Outs();
        var partitions = Arr(p, "distinct_targets").Select(e => Expr(e, outs)).ToList();
        var orders = ordersElement.EnumerateArray().ToList();
        var orderKeys = orders.Select(o => Expr(o.GetProperty("expression"), outs)).ToList();
        var remaining = orders.Where((o, i) => !partitions.Contains(orderKeys[i])).ToList();
        if (remaining.Count == 0) throw new LoweringException("DISTINCT ON whose ORDER BY only names the ON columns (the row kept would be arbitrary)");

        // the ordering must identify one row of every table the query reads
        var named = partitions.Concat(orderKeys).Select(t => Regex.Match(t, "^\0([^\u0001\0]*)\u0001([^\0]*)\0$")).Where(m => m.Success).Select(m => (Alias: m.Groups[1].Value, Column: m.Groups[2].Value)).ToList();
        foreach (var source in c.Sources)
        {
            var table = aliasTables.GetValueOrDefault(source);
            var grain = table == null || grainOf == null ? [] : grainOf(table);
            if (grain.Count == 0)
                throw new LoweringException($"DISTINCT ON over {(table == null ? "a derived table" : $"`{table}`, which has no declared grain")}: its ORDER BY cannot be shown to decide the row (declare a grain, or order by a unique key)");
            var missing = grain.Where(g => !named.Any(n => n.Alias == source && string.Equals(n.Column, g, StringComparison.OrdinalIgnoreCase))).ToList();
            if (missing.Count > 0)
                throw new LoweringException($"DISTINCT ON keeps an arbitrary row among ties: add the grain column(s) {string.Join(", ", missing.Select(m => $"`{m}`"))} of `{table}` to its ORDER BY");
        }

        var rowNumber = $"row_number() OVER (PARTITION BY {string.Join(", ", partitions)} ORDER BY {string.Join(", ", remaining.Select(o => OrderItem(o, outs)))})";
        c.Sel = c.Sel.Append(new Item(rowNumber, "rn", "BIGINT")).ToList();
        c.HasWindow = true;
        c.Plain = false;
        var wrapped = Wrap(c);
        wrapped.Where.Add($"({wrapped.Sel[^1].Sql} = 1)");
        return wrapped;
    }

    private static bool IsConstantRow(Rel r) => r.Frm.Length == 0 && r.Sources.Count == 0 && !r.IsDelim && r.SetOp == null && r.Where.Count == 0 && r.Group.Count == 0;

    /// <summary>
    /// An uncorrelated scalar subquery or EXISTS is a one-row relation crossed with the outer query. DuckDB writes the single-row guarantee of a scalar subquery as
    /// `CASE WHEN count(*) > 1 THEN error(...) ELSE first(x) END` over the subquery, and EXISTS as `count(*) = 1` over `LIMIT 1`. Both are turned back into the subquery.
    /// </summary>
    private Rel? TryUncorrelatedScalar(JsonElement p, List<JsonElement> kids)
    {
        if (kids.Count != 1 || Str(kids[0], "type") != "LOGICAL_AGGREGATE_AND_GROUP_BY" || kids[0].TryGetProperty("groups", out var gs) && gs.GetArrayLength() > 0) return null;
        var agg = kids[0];
        var aggs = Arr(agg, "expressions").ToList();
        var aggKids = Arr(agg, "children").ToList();
        if (aggKids.Count != 1) return null;
        var projections = Arr(p, "expressions").ToList();

        // EXISTS (SELECT ...): count(*) over a LIMIT 1, compared with 1
        if (aggs.Count == 1 && Str(aggs[0], "name") == "count_star" && Str(aggKids[0], "type") == "LOGICAL_LIMIT" && projections.Count == 1 &&
            Str(projections[0], "type") == "COMPARE_EQUAL" && projections[0].GetProperty("left").GetProperty("type").GetString() == "BOUND_REF" &&
            projections[0].GetProperty("right").GetProperty("type").GetString() == "VALUE_CONSTANT")
        {
            var want = projections[0].GetProperty("right").GetProperty("value").GetProperty("value").GetInt64();
            if (want is not (0 or 1)) return null;
            var limit = aggKids[0];
            if (!(Str(limit.GetProperty("limit_val"), "type") == "CONSTANT_VALUE" && limit.GetProperty("limit_val").GetProperty("constant_integer").GetInt64() == 1) || (limit.TryGetProperty("offset_val", out var off) && Str(off, "type") != "UNSET")) return null;
            var inner = Node(Arr(limit, "children").First());
            var text = $"EXISTS (\n{SubqueryWith(inner, "1")}\n)";
            return new Rel { Sel = [new Item(want == 1 ? text : $"(NOT {text})", Str(projections[0], "alias"), "BOOLEAN")] };
        }

        // (SELECT x FROM ...): first(x) guarded by count(*) > 1
        if (aggs.Count >= 1 && aggs.Count <= 2 && aggs.Any(a => Str(a, "name") == "first") && projections.Count >= 1 && projections.All(e => Str(e, "type") == "CASE_EXPR"))
        {
            var guarded = projections.All(e => Arr(e, "case_checks").Count() == 1 && Str(Arr(e, "case_checks").First().GetProperty("then_expr"), "name") == "error" && e.GetProperty("else_expr").GetProperty("type").GetString() == "BOUND_REF");
            if (!guarded) return null;
            var columns = new List<(JsonElement Expression, int Column)>();
            foreach (var e in projections)
            {
                var firstAgg = aggs[e.GetProperty("else_expr").GetProperty("index").GetInt32()];
                if (Str(firstAgg, "name") != "first") return null;
                columns.Add((e, Arr(firstAgg, "children").First().GetProperty("index").GetInt32()));
            }
            var inner = Node(aggKids[0]);          // only now that the shape is certain: lowering a node has side effects (aliases are numbered)
            var items = columns.Select(x => new Item($"(\n{SubqueryWith(inner, inner.Sel[x.Column].Sql)}\n)", Str(x.Expression, "alias"), TypeNameOf(x.Expression))).ToList();
            return new Rel { Sel = items };
        }
        return null;
    }

    private static string? TypeNameOf(JsonElement e) => e.TryGetProperty("return_type", out var t) ? TypeName(t) : null;
}
