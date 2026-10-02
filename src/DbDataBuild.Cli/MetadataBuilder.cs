using System.Text.Json;
using DbDataBuild.Core;
using DbDataBuild.Define;
using DbDataBuild.Models;
using DbDataBuild.Sql.Analysis;
using DbDataBuild.Sql.Matrix;
using DbDataBuild.State;
using DbDataBuild.Targets;
using DbDataBuild.Targets.Ddl;

namespace DbDataBuild.Cli;

/// <summary>
/// Everything the tool can say about a project and its models as plain data: declared and native types per target, lineage and inferred nullability, rendered operations and their
/// hashes with matrix status, indexes, hooks, and the hashes that identify each model's definition and expected shape. The same documents are printed by `--format json`
/// and stored in the target for introspection (`metadata --store`), so what a person reads and what SQL can query are one thing.
/// </summary>
internal static class MetadataBuilder
{
    public const string ModelSchema = "dbdatabuild.model/1";
    public const string ProjectSchema = "dbdatabuild.project/1";

    private static readonly JsonSerializerOptions Compact = new(CommandReport.Json) { WriteIndented = false };

    /// <summary>The document as stored in a database: compact, snake_case, stable key order.</summary>
    public static string ToStoredJson(object document) => JsonSerializer.Serialize(document, Compact);

    public static object Project(ProjectContext ctx)
    {
        var cfg = ctx.Config;
        return new
        {
            schema = ProjectSchema,
            tool_version = ProductInfo.Version,
            matrix_version = MatrixLoader.EmbeddedVersion(),
            config = new
            {
                default_targets = cfg.DefaultTargets,
                target_versions = cfg.TargetVersions,
                tracking_schema = cfg.TrackingSchema,
                string_semantics = new
                {
                    @case = cfg.StringSemantics.Case.ToString().ToLowerInvariant(),
                    accent = cfg.StringSemantics.Accent.ToString().ToLowerInvariant(),
                    trailing_space = cfg.StringSemantics.TrailingSpace.ToString().ToLowerInvariant(),
                    collations = cfg.StringSemantics.Collations,
                },
                policy = cfg.Policy.ToDictionary(p => p.Key, p => p.Value.ToString().ToLowerInvariant()),
                hook_groups = cfg.HookGroups.ToDictionary(g => g.Key, g => g.Value.Select(HookJson).ToList()),
            },
            models = ctx.Project.Sources.OrderBy(s => s.Definition.Name, StringComparer.Ordinal).Select(s => new
            {
                name = s.Definition.Name,
                kind = s.Definition.KindType,
                targets = ctx.TargetsOf(s.Definition),
                definition_hash = AstHasher.Hash(File.ReadAllText(Path.Combine(ctx.Root, s.QueryFile))).Hash,
            }).ToList(),
        };
    }

    private static object HookJson(HookDefinition h) => h.IsReference
        ? new { use = h.Use }
        : new { name = h.Name, @event = h.Event, script = (object?)h.Script ?? h.ScriptByTarget, targets = h.Targets, effect = h.Effect, risk = h.Risk };

    public static object Model(ProjectContext ctx, ModelSource source, string sql)
    {
        var def = source.Definition;
        var targets = ctx.TargetsOf(def);
        var (hash, _) = AstHasher.Hash(sql);

        // lineage and inferred nullability, against the declared columns of everything upstream
        var upstream = ctx.Project.Models.Where(m => m.Name != def.Name).Select(m => (m.Name, m.Columns))
            .Concat(ctx.Project.Descriptors.Select(d => (d.Name, d.Columns))).ToList();
        var specs = upstream.Select(u =>
        {
            var i = u.Name.LastIndexOf('.');
            return new SchemaTableSpec(i < 0 ? null : u.Name[..i], i < 0 ? u.Name : u.Name[(i + 1)..], u.Columns.Select(c => new SchemaColumnSpec(c.Name, c.Type, c.Nullable)).ToList());
        }).ToList();
        var (facts, _) = QueryAnalyzer.Analyze(sql, specs);
        var known = ctx.Project.Models.Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sources = ctx.Project.Descriptors.Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var ddls = targets.ToDictionary(t => t, t => TargetRegistry.Get(t).CreateDdl(ctx.Config));
        object Native(ColumnDefinition c) => targets.ToDictionary(t => t, t =>
        {
            try { var n = ddls[t].Map(def.Name, c); return (object)new { type = n.Declaration, collation = n.Collation }; }
            catch (DdlUnsupportedException ex) { return new { error = ex.Diagnostic.Code, found = ex.Diagnostic.Found }; }
        });
        object? Lineage(ColumnDefinition c)
        {
            var p = facts?.Projections.FirstOrDefault(x => string.Equals(x.Name, c.Name, StringComparison.OrdinalIgnoreCase));
            return p == null ? null : new
            {
                transform = p.TransformKind,
                cast_type = p.CastType,
                inferred_nullability = p.Nullability,
                upstream = p.Upstream.Select(u => new { table = u.Table, column = u.Column }).ToList(),
            };
        }

        var shapeHashes = new Dictionary<string, object?>();
        foreach (var t in targets)
        {
            try { shapeHashes[t] = Hashing.ShapeHash(DdlGenerator.ExpectedShape(ddls[t].MapAll(def))); }
            catch (DdlUnsupportedException ex) { shapeHashes[t] = new { error = ex.Diagnostic.Code }; }
        }

        var (render, _) = ctx.RenderModel(source, sql, targets);
        var lowered = ctx.Lowering.Enabled ? ctx.Lowering.Lower(source, sql).Model : null;
        var rendered = render.Operations.OrderBy(o => o.Target, StringComparer.Ordinal).ThenBy(o => o.Operation, StringComparer.Ordinal).Select(o =>
        {
            var op = render.Loads.FirstOrDefault(l => l.Target == o.Target && l.Operation == o.Operation);
            return new
            {
                target = o.Target, operation = o.Operation, strategy = o.Strategy, is_default = o.IsDefault, matrix_status = o.Status, findings = o.Findings,
                script_path = op?.ScriptPath, script_hash = op == null ? null : Hashing.ScriptHash(op.Script),
                resolver_path = op?.ResolverPath, resolver_hash = op?.Resolver == null ? null : Hashing.ScriptHash(op.Resolver),
                parameters = op?.Parameters.Select(p => new { name = p.Name, type = p.Type, source = p.Source, constraint = p.Constraint }).ToList(),
            };
        }).ToList();

        var hooks = new Dictionary<string, object>();
        foreach (var t in targets)
        {
            var resolved = HookReader.Resolve(def, ctx.Config, t, source.DefinitionFile, new List<Diagnostic>());
            hooks[t] = resolved.Select(h =>
            {
                var path = Path.Combine(ctx.Root, h.ScriptPath);
                return new { name = h.Name, @event = h.Event, group = h.Group, script = h.ScriptPath, script_hash = File.Exists(path) ? Hashing.ScriptHash(File.ReadAllText(path)) : null, effect = h.Effect, risk = h.Risk };
            }).ToList();
        }

        return new
        {
            schema = ModelSchema,
            name = def.Name,
            kind = new { type = def.KindType, unique_key = def.UniqueKey, time_column = def.TimeColumn, lookback = def.Lookback },
            grain = def.Grain,
            targets,
            files = new { definition = source.DefinitionFile, query = source.QueryFile },
            definition_hash = hash,
            upstream = facts?.BaseTables.Select(b => new { name = b.QualifiedName, kind = known.Contains(b.QualifiedName) ? "model" : sources.Contains(b.QualifiedName) ? "source" : "unknown" }).ToList(),
            lowered = lowered == null ? null : new
            {
                file = $"rendered/{lowered.ArtifactPath}", hash = lowered.Hash, rules = lowered.Query.Rules,
                output = lowered.Query.Columns.Select(c => new { name = c.Name, duckdb_type = c.DuckDbType }).ToList(),
            },
            columns = def.Columns.Select(c => new
            {
                name = c.Name, logical_type = c.Type, duckdb_type = lowered?.Query.Columns.FirstOrDefault(x => string.Equals(x.Name, c.Name, StringComparison.OrdinalIgnoreCase))?.DuckDbType,
                nullable = c.Nullable, collation = c.Collation, native = Native(c), lineage = Lineage(c),
            }).ToList(),
            expected_shape_hash = shapeHashes,
            renames = def.Renames.Select(r => new { from = r.From, to = r.To }).ToList(),
            loads = rendered,
            indexes = def.Indexes.Select(i => new { name = i.Name, columns = i.Columns, unique = i.Unique, include = i.Include, targets = i.Targets }).ToList(),
            hooks,
        };
    }
}
