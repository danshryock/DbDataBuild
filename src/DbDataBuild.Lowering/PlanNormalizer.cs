using System.Text.Json.Nodes;

namespace DbDataBuild.Lowering;

/// <summary>
/// DuckDB's serialized plan is not a stable API, and DuckDB 2.0 restructured it: names moved into `qname.path`, comparisons and casts became ordinary functions with
/// `children`, `NOT IN` became NOT over IN, `BETWEEN` became a function, and window functions carry their arguments as children. The lowerer reads the 1.x shape; this rewrites a 2.0 plan into it, node by
/// node, so one lowerer serves both engines and the committed artifacts do not change when only the plan format did. A plan that is already in the 1.x shape is returned
/// unchanged (as the same string). Every rule here was found by comparing the plans of both engines on the lowering corpus.
/// </summary>
public static class PlanNormalizer
{
    private static readonly Dictionary<string, string> Comparisons = new(StringComparer.Ordinal)
    {
        ["COMPARE_EQUAL"] = "COMPARE_EQUAL", ["COMPARE_NOTEQUAL"] = "COMPARE_NOTEQUAL", ["COMPARE_LESSTHAN"] = "COMPARE_LESSTHAN", ["COMPARE_GREATERTHAN"] = "COMPARE_GREATERTHAN",
        ["COMPARE_LESSTHANOREQUALTO"] = "COMPARE_LESSTHANOREQUALTO", ["COMPARE_GREATERTHANOREQUALTO"] = "COMPARE_GREATERTHANOREQUALTO",
        ["COMPARE_DISTINCT_FROM"] = "COMPARE_DISTINCT_FROM", ["COMPARE_NOT_DISTINCT_FROM"] = "COMPARE_NOT_DISTINCT_FROM",
    };

    private static readonly Dictionary<string, string> Opposite = new(StringComparer.Ordinal)
    {
        ["COMPARE_EQUAL"] = "COMPARE_NOTEQUAL", ["COMPARE_NOTEQUAL"] = "COMPARE_EQUAL", ["COMPARE_LESSTHAN"] = "COMPARE_GREATERTHANOREQUALTO", ["COMPARE_GREATERTHANOREQUALTO"] = "COMPARE_LESSTHAN",
        ["COMPARE_GREATERTHAN"] = "COMPARE_LESSTHANOREQUALTO", ["COMPARE_LESSTHANOREQUALTO"] = "COMPARE_GREATERTHAN", ["COMPARE_DISTINCT_FROM"] = "COMPARE_NOT_DISTINCT_FROM", ["COMPARE_NOT_DISTINCT_FROM"] = "COMPARE_DISTINCT_FROM",
    };

    public static string Normalize(string planJson)
    {
        if (!planJson.Contains("\"qname\"", StringComparison.Ordinal)) return planJson;      // the 1.x shape has no qualified names
        var root = JsonNode.Parse(planJson)!;
        Walk(root);
        return root.ToJsonString();
    }

    private static void Walk(JsonNode? node)
    {
        switch (node)
        {
            case JsonArray a:
                foreach (var item in a) Walk(item);
                break;
            case JsonObject o:
                foreach (var key in o.Select(p => p.Key).ToList()) Walk(o[key]);
                Rewrite(o);
                break;
        }
    }

    /// <summary>A NULL literal, or a cast of one (2.0 types the default of lag and lead).</summary>
    private static bool IsNullConstant(JsonNode? n) =>
        n is JsonObject o && (Text(o["type"]) == "VALUE_CONSTANT" ? o["value"]?["is_null"] is JsonValue v && v.TryGetValue<bool>(out var isNull) && isNull : o["child"] is { } child && IsNullConstant(child));

    private static string? Text(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static void Rewrite(JsonObject o)
    {
        // names: `qname.path` is [catalog, schema, name], [schema, name] or [name]
        if (o["qname"]?["path"] is JsonArray { Count: > 0 } path && o["name"] is null)
        {
            o["name"] = path[^1]!.DeepClone();
            if (path.Count >= 2) o["schema_name"] = path[^2]!.DeepClone();
            if (path.Count >= 3) o["catalog_name"] = path[0]!.DeepClone();
        }

        var type = Text(o["type"]);           // `type` is an object on a column index, so it is a string only on operators and expressions
        var cls = Text(o["expression_class"]);
        if (cls == "BOUND_FUNCTION" && type != null && o["children"] is JsonArray children)
        {
            if (Comparisons.ContainsKey(type) && children.Count == 2)
            {
                o["expression_class"] = "BOUND_COMPARISON";
                o["left"] = children[0]!.DeepClone();
                o["right"] = children[1]!.DeepClone();
                o.Remove("children");
            }
            else if (type is "COMPARE_BETWEEN" or "COMPARE_NOT_BETWEEN" && children.Count == 3)
            {
                o["expression_class"] = "BOUND_BETWEEN";
                o["input"] = children[0]!.DeepClone();
                o["lower"] = children[1]!.DeepClone();
                o["upper"] = children[2]!.DeepClone();
                o["lower_inclusive"] = o["function_data"]?["lower_inclusive"]?.DeepClone() ?? true;
                o["upper_inclusive"] = o["function_data"]?["upper_inclusive"]?.DeepClone() ?? true;
                o.Remove("children");
            }
            else if (type == "OPERATOR_CAST" && children.Count == 1)
            {
                o["expression_class"] = "BOUND_CAST";
                o["child"] = children[0]!.DeepClone();
                o["try_cast"] = o["function_data"]?["try_cast"]?.DeepClone() ?? false;
                o.Remove("children");
            }
        }

        // NOT (a IN (...)) is what 1.x called COMPARE_NOT_IN
        if (type == "OPERATOR_NOT" && o["children"] is JsonArray { Count: 1 } not && not[0] is JsonObject inner && Text(inner["type"]) == "COMPARE_IN")
        {
            o["type"] = "COMPARE_NOT_IN";
            o["children"] = inner["children"]!.DeepClone();
        }

        // NOT over a comparison is the opposite comparison (1.x folded it while binding; the 3-valued logic is the same, NULL stays NULL)
        if (type == "OPERATOR_NOT" && o["children"] is JsonArray { Count: 1 } negated && negated[0] is JsonObject cmp && Opposite.TryGetValue(Text(cmp["type"]) ?? "", out var opposite) && cmp["left"] is not null)
        {
            o["expression_class"] = "BOUND_COMPARISON";
            o["type"] = opposite;
            o["left"] = cmp["left"]!.DeepClone();
            o["right"] = cmp["right"]!.DeepClone();
            o.Remove("children");
        }

        // lead and lag: 1.x kept the offset and default beside the argument, 2.0 lists them as arguments
        if (type is "WINDOW_LAG" or "WINDOW_LEAD" && o["children"] is JsonArray { Count: > 1 } args)
        {
            // an offset of 1 and a NULL default are what 1.x left out when the author did not write them; the text of the lowered query stays the same
            var plainOffset = args[1] is JsonObject off && Text(off["type"]) == "VALUE_CONSTANT" && off["value"]?["value"]?.ToJsonString() == "1";
            var nullDefault = args.Count > 2 && IsNullConstant(args[2]);
            if (!(plainOffset && (args.Count == 2 || nullDefault)))
            {
                o["offset_expr"] = args[1]!.DeepClone();
                if (args.Count > 2 && !nullDefault) o["default_expr"] = args[2]!.DeepClone();
            }
            o["children"] = new JsonArray(args[0]!.DeepClone());
        }
    }
}
