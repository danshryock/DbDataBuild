using System.Text;
using DbDataBuild.Core;
using DbDataBuild.Lowering;
using DbDataBuild.Models;
using DbDataBuild.Sql.Analysis;
using DbDataBuild.Targets.DuckDb;

namespace DbDataBuild.Cli;

/// <summary>A model's lowered query and the committed artifact that shows it.</summary>
/// <param name="ArtifactPath">Relative to rendered/, `lowered/&lt;model&gt;/lowered.sql`.</param>
/// <param name="ArtifactText">The file: a header naming its source, hashes and the DuckDB version, then the query.</param>
internal sealed record LoweredModel(string Sql, LoweredQuery Query, string ArtifactPath, string ArtifactText, string Hash);

/// <summary>
/// The lowering stage (docs/research/duckdb-plan-lowering): DuckDB binds each model query against an empty schema built from the declared columns of everything
/// else in the project, and the bound plan becomes one explicit query that the matrix lint and polyglot then work on. A query that cannot be lowered is DDB-324.
/// </summary>
internal sealed class ModelLowering(IReadOnlyList<ModelDefinition> models, IReadOnlyList<SourceDescriptor> descriptors, ProjectConfig config)
{
    private readonly Dictionary<string, (LoweredModel? Model, Diagnostic? Error)> cache = new(StringComparer.Ordinal);
    private string? duckDbVersion;

    public bool Enabled => config.LoweringEnabled;

    /// <summary>The declared grain of a model or source, by the name a query uses for it.</summary>
    private IReadOnlyList<string> GrainOf(string table) =>
        models.FirstOrDefault(m => string.Equals(m.Name, table, StringComparison.OrdinalIgnoreCase))?.Grain
        ?? descriptors.FirstOrDefault(d => string.Equals(d.Name, table, StringComparison.OrdinalIgnoreCase))?.Grain ?? [];

    private readonly Dictionary<string, NativeUse> nativeUses = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The native models a query reads (by lineage of its text), ready to be spliced into the target's text after transpiling.</summary>
    public IReadOnlyList<NativeUse> NativeUsesFor(string sql)
    {
        if (!descriptors.Any(d => d.IsNative)) return [];
        var read = QueryAnalyzer.Analyze(sql).Facts?.BaseTables.Select(t => t.QualifiedName).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        var result = new List<NativeUse>();
        foreach (var d in descriptors.Where(d => d.IsNative && read.Contains(d.Name)))
        {
            if (!nativeUses.TryGetValue(d.Name, out var use)) nativeUses[d.Name] = use = NativeInline.Prepare(d, config);
            result.Add(use);
        }
        return result;
    }

    /// <summary>The value of each placeholder of the native models a query reads, on one connection.</summary>
    public IReadOnlyDictionary<string, ParameterValue> NativeValuesFor(string sql, string connection)
    {
        var values = new Dictionary<string, ParameterValue>(StringComparer.Ordinal);
        foreach (var use in NativeUsesFor(sql))
            foreach (var (k, v) in NativeInline.Values(descriptors.First(d => d.Name == use.Name), use, config, connection)) values[k] = v;
        return values;
    }

    public static string ArtifactPathFor(string model) => $"lowered/{model}/lowered.sql";

    /// <param name="parameters">The parameters the query uses as values (their markers are in <paramref name="authorSql"/>); the committed artifact shows them as the references they stand for.</param>
    public (LoweredModel? Model, Diagnostic? Error) Lower(ModelSource source, string authorSql, IReadOnlyList<QueryParameter>? parameters = null)
    {
        var key = source.Definition.Name + "\0" + authorSql;
        if (cache.TryGetValue(key, out var hit)) return hit;
        return cache[key] = Compute(source, authorSql, parameters ?? []);
    }

    private (LoweredModel?, Diagnostic?) Compute(ModelSource source, string authorSql, IReadOnlyList<QueryParameter> parameters)
    {
        var name = source.Definition.Name;
        Diagnostic Fail(string why) => new(DiagnosticCatalog.QueryNotLowerable, new(source.QueryFile, 0, 0), $"{name} cannot be lowered: {why}.");

        var upstream = models.Where(m => m.Name != name).Select(m => (m.Name, m.Columns)).Concat(descriptors.Select(d => (d.Name, d.Columns)))
            .Select(u =>
            {
                var i = u.Name.LastIndexOf('.');
                return new DuckTable(i < 0 ? "main" : u.Name[..i], i < 0 ? u.Name : u.Name[(i + 1)..], u.Columns.Select(c => new DuckColumn(c.Name, c.Type, c.Nullable)).ToList());
            }).ToList();

        var policy = RewriteCatalog.For(config, source.Definition);
        var (json, error) = QueryDescriber.SerializePlan(upstream, authorSql);
        if (json == null) return (null, Fail(error ?? "DuckDB returned no plan"));
        LoweredQuery query;
        try
        {
            PlanLowerer.ThrowIfError(json);                                        // DuckDB's own parse and bind errors first
            var described = QueryDescriber.Describe(upstream, authorSql);
            if (!described.Ok) return (null, Fail(described.Error ?? "DuckDB could not describe the query"));
            query = PlanLowerer.Lower(json, described.Columns!.Select(c => c.Name).ToList(), GrainOf, policy);
        }
        catch (LoweringException ex) when (ex.Kind == "parser") { return (null, new Diagnostic(DiagnosticCatalog.SqlParseFailure, new(source.QueryFile, 0, 0), $"The DuckDB parser reported: {ex.Message}")); }
        catch (LoweringException ex) when (ex.Kind is "binder" or "catalog" && ex.Message.StartsWith("Table with name", StringComparison.Ordinal))
        {
            return (null, new Diagnostic(DiagnosticCatalog.UpstreamNotFound, new(source.QueryFile, 0, 0), $"{name}: {ex.Message.Split('\n')[0]}"));
        }
        catch (LoweringException ex) { return (null, Fail(ex.Kind != "unsupported" ? "DuckDB could not bind the query: " + ex.Message.Split('\n')[0] : ex.Message)); }

        duckDbVersion ??= QueryDescriber.DuckDbVersion();
        var (authorHash, _) = AstHasher.Hash(authorSql);
        var sb = new StringBuilder();
        sb.Append("-- dbdatabuild lowered query. Generated by `render --write`; do not edit.\n");
        sb.Append($"-- model:        {name}\n-- source:       {source.QueryFile}\n-- source hash:  {authorHash}\n-- duckdb:       {duckDbVersion}\n");
        sb.Append($"-- output:       {string.Join(", ", query.Columns.Select(c => $"{c.Name} {c.DuckDbType}"))}\n");
        if (query.Rules.Count > 0) sb.Append($"-- type rules:   {string.Join(", ", query.Rules)}\n");
        if (!policy.IsDefault) sb.Append($"-- rewrites off: {policy.Describe()}\n");
        sb.Append(QueryParameters.BackToReferences(query.Sql, parameters)).Append('\n');
        var text = sb.ToString();
        return (new LoweredModel(query.Sql, query, ArtifactPathFor(name), text, DbDataBuild.State.Hashing.ScriptHash(text)), null);
    }
}
