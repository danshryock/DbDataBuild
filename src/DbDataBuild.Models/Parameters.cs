using System.Globalization;
using System.Text.RegularExpressions;
using DbDataBuild.Core;
using DbDataBuild.Models.Yaml;

namespace DbDataBuild.Models;

/// <summary>
/// A parameter's value and type. Written as a single value (text, type VARCHAR) or as <c>{ type: DATE, value: "2024-01-01" }</c>. The type matters where the value reaches a query: a bound value has the type
/// the parameter declares.
/// </summary>
public sealed record ParameterValue(string Value, string Type = ParameterValue.Text)
{
    public const string Text = "VARCHAR";

    /// <summary>The types a parameter may have (the ones a query value can be bound and typed as offline).</summary>
    public static readonly IReadOnlyList<string> Types = [Text, "BIGINT", "INTEGER", "SMALLINT", "DATE", "TIMESTAMP"];
}

/// <summary>
/// One concept, four scopes in files and a fifth at run time. A reference carries its scope, so nothing shadows anything: `${project.x}` (the project files, layered root down), `${connection.x}` (the connection a model is
/// built on), `${origin.x}` (the connection a copy reads from), `${model.x}` (the model's own file only). Operation parameters (`@name`) are another matter: they are bound at run time.
/// </summary>
public static class ParameterReferences
{
    public static readonly Regex Pattern = new(@"\$\{([a-z]+)\.([a-z][a-z0-9_]*)\}", RegexOptions.Compiled);
    public static readonly IReadOnlyList<string> Scopes = ["project", "connection", "origin", "model"];
    private static readonly Regex Name = new("^[a-z][a-z0-9_]*$", RegexOptions.Compiled);

    public static bool IsValidName(string name) => Name.IsMatch(name);

    /// <summary>Reads a `parameters:` mapping into values; problems are reported through <paramref name="add"/> at the node they are about.</summary>
    public static Dictionary<string, ParameterValue>? Read(YamlNode node, string where, Action<DiagnosticDescriptor, YamlNode, string> add)
    {
        if (node is not YamlMapping map) { add(DiagnosticCatalog.InvalidValue, node, $"{where} must map parameter names to values."); return null; }
        var result = new Dictionary<string, ParameterValue>(StringComparer.Ordinal);
        foreach (var e in map.Entries)
        {
            if (!IsValidName(e.Key.Value)) { add(DiagnosticCatalog.InvalidValue, e.Key, $"`{e.Key.Value}` is not a valid parameter name (lowercase letters, digits and underscores, starting with a letter)."); continue; }
            switch (e.Value)
            {
                case YamlScalar s:
                    result[e.Key.Value] = new ParameterValue(s.Value);
                    break;
                case YamlMapping m:
                {
                    foreach (var k in m.Entries.Where(k => k.Key.Value is not ("type" or "value")))
                        add(DiagnosticCatalog.UnknownKey, k.Key, $"Unknown key `{k.Key.Value}` in the parameter `{e.Key.Value}` (`type` and `value`).");
                    var type = (m.Get("type") as YamlScalar)?.Value.Trim().ToUpperInvariant() ?? ParameterValue.Text;
                    if (m.Get("value") is not YamlScalar v) { add(DiagnosticCatalog.MissingKey, m, $"The parameter `{e.Key.Value}` needs a single `value`."); break; }
                    if (!ParameterValue.Types.Contains(type)) { add(DiagnosticCatalog.InvalidValue, (YamlNode?)m.Get("type") ?? m, $"`{type}` is not a parameter type. One of: {string.Join(", ", ParameterValue.Types)}."); break; }
                    if (type != ParameterValue.Text && !ColumnTypes.LiteralFits(type, v.Value)) { add(DiagnosticCatalog.InvalidValue, v, $"`{v.Value}` is not a {type} (integers, `yyyy-MM-dd`, `yyyy-MM-dd HH:mm:ss`)."); break; }
                    result[e.Key.Value] = new ParameterValue(v.Value, type);
                    break;
                }
                default:
                    add(DiagnosticCatalog.InvalidValue, e.Value, $"The parameter `{e.Key.Value}` must be a single value, or `{{type, value}}`.");
                    break;
            }
        }
        return result;
    }

    /// <summary>`project.region` style keys of the values a model sees on one connection: the project files' parameters (nearest wins), the connection's (the folders may override them), and the model's own.</summary>
    public static Dictionary<string, ParameterValue> Effective(IReadOnlyDictionary<string, ParameterValue> project, IReadOnlyDictionary<string, ParameterValue> connection, IReadOnlyDictionary<string, ParameterValue> model)
    {
        var all = new Dictionary<string, ParameterValue>(StringComparer.Ordinal);
        foreach (var (k, v) in project) all[$"project.{k}"] = v;
        foreach (var (k, v) in connection) all[$"connection.{k}"] = v;
        foreach (var (k, v) in model) all[$"model.{k}"] = v;
        return all;
    }

    /// <summary>The distinct `scope.name` references in a text, in order of appearance.</summary>
    public static IReadOnlyList<(string Scope, string Name)> In(string text) =>
        Pattern.Matches(text).Select(m => (m.Groups[1].Value, m.Groups[2].Value)).Distinct().ToList();

    /// <summary>Replaces every reference with <paramref name="lookup"/> (`scope`, `name`); a reference it cannot answer (null) is left as written.</summary>
    public static string Substitute(string text, Func<string, string, string?> lookup) =>
        Pattern.Replace(text, m => lookup(m.Groups[1].Value, m.Groups[2].Value) ?? m.Value);

    internal static string Invariant(string s) => s.ToString(CultureInfo.InvariantCulture);
}
