using DbDataBuild.Models;
using DbDataBuild.Sql.Analysis;

namespace DbDataBuild.Cli;

/// <summary>Builds the <see cref="DependencyGraph"/> of a project from its queries, and the column-level lineage on request.</summary>
internal static class ProjectGraph
{
    public static DependencyGraph Build(ProjectContext ctx)
    {
        var edges = new List<(string Model, string Reads)>();
        foreach (var m in ctx.Project.Sources)
        {
            var sql = m.ReadQuery(ctx.Root, ctx.Config);
            foreach (var t in QueryAnalyzer.Analyze(sql).Facts?.BaseTables ?? []) edges.Add((m.Definition.Name, t.QualifiedName));
        }
        return new DependencyGraph(ctx.Project.Sources.Select(s => s.Definition.Name), ctx.Project.AllDescriptors.Select(d => d.Name), edges);
    }

    /// <summary>Which column of which table each output column of a model comes from, with the kind of transformation (direct, expression, aggregation, ...).</summary>
    public static IReadOnlyList<ColumnEdge> ColumnEdges(ProjectContext ctx, ModelSource model)
    {
        var sql = model.ReadQuery(ctx.Root, ctx.Config);
        var facts = QueryAnalyzer.Analyze(sql, MetadataBuilder.UpstreamSchema(ctx, model.Definition.Name)).Facts;
        var edges = new List<ColumnEdge>();
        foreach (var p in facts?.Projections ?? [])
        {
            if (p.Name == null) continue;
            var column = model.Definition.Columns.FirstOrDefault(c => string.Equals(c.Name, p.Name, StringComparison.OrdinalIgnoreCase))?.Name ?? p.Name;
            if (p.Upstream.Count == 0) edges.Add(new ColumnEdge(null, null, model.Definition.Name, column, p.TransformKind));
            foreach (var u in p.Upstream) edges.Add(new ColumnEdge(u.Table, u.Column, model.Definition.Name, column, p.TransformKind));
        }
        return edges;
    }
}

/// <summary>A column of <c>To</c> built from <c>FromColumn</c> of <c>FromTable</c> (both null for a column made of constants).</summary>
internal sealed record ColumnEdge(string? FromTable, string? FromColumn, string ToModel, string ToColumn, string Transform);
