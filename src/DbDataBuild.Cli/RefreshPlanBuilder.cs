using DbDataBuild.Core;
using DbDataBuild.Models;
using DbDataBuild.Planning;
using DbDataBuild.State;
using DbDataBuild.Targets;
using DbDataBuild.Targets.Ddl;
using DbDataBuild.Targets.Rendering;

namespace DbDataBuild.Cli;

/// <summary>
/// Compiles the refresh plan of a connection from the project alone (no connection is opened): the routine loads in dependency order, with the hooks around them, the committed scripts they run (by path
/// and hash), and what the compiled project expects the structure to be. A model whose load is not routine (a copy, a load that needs a value from a person, a structure hook) is left out and says why.
/// </summary>
internal static class RefreshPlanBuilder
{
    /// <summary>Every model of the project that declares <paramref name="connection"/>, with its query as that connection reads it: what the project hash and the refresh plan are made from.</summary>
    public static List<LoadedModel> ModelsOf(ProjectContext ctx, string connection) =>
        ctx.Project.Sources.Where(s => ctx.TargetsOf(s.Definition).Contains(connection)).Select(s => new LoadedModel(s, s.ReadQuery(ctx.Root, ctx.Config, connection))).ToList();

    /// <summary>The structure the compiled project expects of one connection, as one hash: each table model by the shape its declaration gives it, each view by its definition. A deploy records it.</summary>
    public static string ProjectHash(ProjectContext ctx, string connection, IReadOnlyList<LoadedModel> models)
    {
        var ddl = TargetRegistry.Get(ctx.Config.EngineOf(connection) ?? connection).CreateDdl(ctx.Config.ForConnection(connection));
        var lines = new List<string>();
        foreach (var m in models.OrderBy(m => m.Source.Definition.Name, StringComparer.Ordinal))
        {
            var def = m.Source.Definition;
            lines.Add(def.KindType == ModelKinds.View ? $"{def.Name}=view:{ctx.DefinitionHashOf(m.Sql)}" : $"{def.Name}={ExpectedShape(ddl, def)}");
        }
        return Hashing.Sha256Hex(string.Join("\n", lines));
    }

    private static string ExpectedShape(DdlGenerator ddl, ModelDefinition def)
    {
        try { return Hashing.ShapeHash(DdlGenerator.ExpectedShape(ddl.MapAll(def))); }
        catch (DdlUnsupportedException ex) { return "unsupported:" + ex.Diagnostic.Code; }
    }

    /// <param name="models">Every model of the project that declares <paramref name="connection"/>, with its query as that connection reads it, and its rendering for the connection.</param>
    public static RefreshPlan Build(ProjectContext ctx, string connection, IReadOnlyList<(LoadedModel Model, RenderResult Render)> models, List<Diagnostic> diags)
    {
        var root = ctx.Root;
        var ddl = TargetRegistry.Get(ctx.Config.EngineOf(connection) ?? connection).CreateDdl(ctx.Config.ForConnection(connection));
        var planned = models.Select(x =>
        {
            var m = x.Model;
            var bases = m.Source.Definition is { IsCopy: true, LocalCopy: false } ? [] : ctx.WithNativeReads(ctx.BaseTablesOf(m.Source, m.Sql));
            return new PlannedModel(m.Source.Definition, "", m.Source.QueryFile, ctx.DefinitionHashOf(m.Sql), bases, HookLoader.Load(m.Source, ctx.Config, connection, root, diags), [],
                PlanningSession.Merge(m.Source.ParametersFor(ctx.Config, connection), ctx.Lowering.NativeValuesFor(m.Sql, connection)), null);
        }).ToList();
        var renders = models.ToDictionary(x => x.Model.Source.Definition.Name, x => x.Render, StringComparer.Ordinal);

        var loads = new List<RefreshLoad>();
        var requires = new List<RefreshRequirement>();
        var excluded = new List<RefreshExclusion>();
        foreach (var model in ModelOrder.Sort(planned, out _))
        {
            var def = model.Definition;
            var ops = renders[def.Name].Loads.Where(l => l.Target == connection).ToList();
            if (ops.Count == 0) continue;                                           // a view, a mapped model, a kind without loads
            void Skip(string reason) => excluded.Add(new(def.Name, reason));
            if (def.IsCopy && !def.LocalCopy) { Skip("a copy moves rows between connections; a deploy plans it"); continue; }
            var op = ops.FirstOrDefault(o => o.IsDefault) ?? (ops.Count == 1 ? ops[0] : null);
            if (op == null) { Skip("it has several load operations and none is the default"); continue; }

            var parameters = new List<RefreshParameter>();
            var routine = true;
            foreach (var p in op.Parameters)
            {
                if (p.Source == "parameter")
                {
                    var key = QueryParameter.KeyOf(p.Name);
                    if (key == null || model.ParameterValues?.GetValueOrDefault(key) is not { } pv) { Skip($"the parameter `{key ?? p.Name}` has no value on {connection}"); routine = false; break; }
                    parameters.Add(new(p.Name, pv.Type, "parameter", pv.Value));
                }
                else if (p.Source == "resolver") parameters.Add(new(p.Name, p.Type, "resolver", null));
                else { Skip($"the operation `{op.Operation}` needs a value from a person (`@{p.Name}`); a deploy asks for it"); routine = false; break; }
            }
            if (!routine) continue;

            var hooks = model.HookList.Where(h => h.Hook.Event is "pre_load" or "post_load").ToList();
            var bad = hooks.FirstOrDefault(h => h.Hook.Effect != "data" || !string.Equals(h.Hook.Risk, "safe", StringComparison.OrdinalIgnoreCase));
            if (bad != null) { Skip($"the hook `{bad.Hook.Name}` around its load is not a safe data hook, so the load is not routine"); continue; }
            RefreshHook Hook(PlannedHook h) => new(h.Hook.Name, h.Hook.Event, h.Hook.ScriptPath.Replace('\\', '/'), h.FileHash, h.Hook.Effect, h.Hook.Risk);

            var shape = ExpectedShape(ddl, def);
            if (shape.StartsWith("unsupported:", StringComparison.Ordinal)) { Skip($"its declared columns have no type on {connection} ({shape[12..]})"); continue; }
            loads.Add(new(def.Name, op.Operation, op.ScriptPath, Hashing.ScriptHash(op.Script), op.ResolverPath, op.Resolver == null ? null : Hashing.ScriptHash(op.Resolver), model.DefinitionHash,
                parameters, op.Watermark?.OnNull, op.Watermark?.Initial, hooks.Where(h => h.Hook.Event == "pre_load").Select(Hook).ToList(), hooks.Where(h => h.Hook.Event == "post_load").Select(Hook).ToList()));
            requires.Add(new(def.Name, shape));
        }
        return new RefreshPlan(connection, ProjectHash(ctx, connection, models.Select(x => x.Model).ToList()), loads, requires.OrderBy(r => r.Object, StringComparer.Ordinal).ToList(), excluded.OrderBy(x => x.Model, StringComparer.Ordinal).ToList());
    }
}
