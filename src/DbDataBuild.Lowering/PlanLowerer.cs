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

    public static LoweredQuery Lower(string planJson, IReadOnlyList<string>? outputNames = null)
    {
        using var doc = JsonDocument.Parse(planJson);
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.True)
            throw new LoweringException(root.TryGetProperty("error_message", out var m) ? m.GetString()! : "unknown error", root.TryGetProperty("error_type", out var et) ? et.GetString()! : "binder");
        var plans = root.GetProperty("plans");
        if (plans.GetArrayLength() != 1) throw new LoweringException($"expected one plan, got {plans.GetArrayLength()}");
        var lowerer = new PlanLowerer();
        var rel = lowerer.Node(plans[0]);
        if (outputNames != null && rel.SetOp == null)
        {
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
                foreach (var c in Arr(e, "case_checks")) sb.Append($"WHEN {Expr(c.GetProperty("when_expr"), outs)} THEN {Expr(c.GetProperty("then_expr"), outs)} ");
                if (e.TryGetProperty("else_expr", out var el)) sb.Append($"ELSE {Expr(el, outs)} ");
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
        return $"{fn}({Expr(child, outs)} AS {TypeName(e.GetProperty("return_type"))})";
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

    private sealed record Item(string Sql, string? Alias, string? Type);

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
        public string? SetOp { get; set; }
        public bool SetOpDistinct { get; set; }
        public IReadOnlyList<string>? SetOpNames { get; set; }

        public IReadOnlyList<string> Outs() => Sel.Select(s => s.Sql).ToList();
        public bool Mergeable() => Limit == null && Offset == null && !Distinct && SetOp == null;

        public string Render(string text)
        {
            var multi = Sources.Count > 1;
            return Regex.Replace(text, "\0([^\u0001\0]*)\u0001([^\0]*)\0", m => (multi ? Ident(m.Groups[1].Value) + "." : "") + Ident(m.Groups[2].Value));
        }

        private static readonly Regex Plainref = new("^\0[^\u0001\0]*\u0001([^\0]*)\0$");

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
            var names = Aliases();
            var items = new List<string>();
            for (var i = 0; i < Sel.Count; i++)
            {
                var m = Plainref.Match(Sel[i].Sql);
                var needsAs = !m.Success || m.Groups[1].Value != names[i];
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
            Sel = names.Select((n, i) => new Item(Token(a, n), n, i < types.Count ? types[i] : null)).ToList(),
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
                if (fd.ValueKind != JsonValueKind.Object || !fd.TryGetProperty("table", out var table)) throw new LoweringException("a table function");
                var names = p.GetProperty("names").EnumerateArray().Select(x => x.GetString()!).ToList();
                var types = p.GetProperty("returned_types").EnumerateArray().Select(TypeName).ToList();
                var idx = Arr(p, "column_indexes").Select(c => c.GetProperty("index").GetInt32()).ToList();
                if (idx.Count == 0) idx = Enumerable.Range(0, names.Count).ToList();
                var baseName = table.GetString()!;
                uses[baseName] = uses.GetValueOrDefault(baseName) + 1;
                var alias = uses[baseName] == 1 ? baseName : $"{baseName}_{uses[baseName]}";
                var schema = Str(fd, "schema");
                var rel = new Rel
                {
                    Frm = (schema is null or "main" ? baseName : $"{schema}.{baseName}") + (alias == baseName ? "" : $" AS {alias}"),
                    Sources = [alias],
                    Plain = true,
                    Sel = idx.Select(i => new Item(Token(alias, names[i]), names[i], types[i])).ToList(),
                };
                return rel;
            }
            case "LOGICAL_DUMMY_SCAN": return new Rel();
            case "LOGICAL_PROJECTION":
            {
                var c = Node(kids[0]);
                if (!c.Mergeable()) c = Wrap(c);
                var outs = c.Outs();
                c.Sel = Arr(p, "expressions").Select(e => new Item(Expr(e, outs), Str(e, "alias"), TypeId(e) is { } id ? TypeNameOf(e) : null)).ToList();
                c.Plain = false;
                if (c.Sel.Any(s => s.Sql.Contains(" OVER (", StringComparison.Ordinal))) c.HasWindow = true;
                return c;
            }
            case "LOGICAL_FILTER":
            {
                var c = Node(kids[0]);
                if (c.HasWindow || !c.Mergeable()) c = Wrap(c);
                var preds = Arr(p, "expressions").Select(e => Expr(e, c.Outs())).ToList();
                if (c.HasAgg && c.Having.Count == 0 && !c.Plain) c.Having.AddRange(preds); else c.Where.AddRange(preds);
                return c;
            }
            case "LOGICAL_AGGREGATE_AND_GROUP_BY":
            {
                var c = Node(kids[0]);
                if (!c.Mergeable() || c.HasWindow || c.Group.Count > 0 || c.Having.Count > 0) c = Wrap(c);
                var outs = c.Outs();
                var groups = Arr(p, "groups").Select(g => (Sql: Expr(g, outs), Type: TypeNameOf(g))).ToList();
                var aggs = Arr(p, "expressions").Select(a => (Sql: Expr(a, outs), Type: TypeNameOf(a))).ToList();
                if (groups.Count == 0 && aggs.Count == 0) throw new LoweringException("an empty aggregate");
                c.Group = groups.Select(g => g.Sql).ToList();
                c.Sel = groups.Concat(aggs).Select(x => new Item(x.Sql, null, x.Type)).ToList();
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
                if (Str(p, "distinct_type") != "DISTINCT") throw new LoweringException("DISTINCT ON");
                if (c.SetOp != null && c.SetOpDistinct) return c; // a set operation without ALL is already distinct
                if (!c.Mergeable() || c.HasWindow) c = Wrap(c);
                c.Distinct = true;
                return c;
            }
            case "LOGICAL_COMPARISON_JOIN" or "LOGICAL_CROSS_PRODUCT":
            {
                var l = Node(kids[0]);
                var r = Node(kids[1]);
                if (!l.Plain) l = Wrap(l);
                if (!r.Plain) r = Wrap(r);
                var rel = new Rel { Sources = l.Sources.Concat(r.Sources).ToList(), Sel = l.Sel.Concat(r.Sel).ToList() };
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
                c.Sel = c.Sel.Concat(Arr(p, "expressions").Select(e => new Item(Window(e, outs), null, TypeNameOf(e)))).ToList();
                c.HasWindow = true;
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

    private static string? TypeNameOf(JsonElement e) => e.TryGetProperty("return_type", out var t) ? TypeName(t) : null;
}
