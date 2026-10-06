using System.Text;
using DbDataBuild.Models;

namespace DbDataBuild.Define;

/// <summary>
/// Writes a new definition in canonical formatting with a fixed key order (DESIGN.md 6.5):
/// name, kind, grain, targets, columns, renames, indexes. <c>nullable</c> is written only when false.
/// </summary>
public static class DefinitionWriter
{
    public static string Create(ModelDefinition d)
    {
        var sb = new StringBuilder();
        sb.Append("name: ").Append(YamlText.Scalar(d.Name)).Append('\n');
        sb.Append("kind:\n  type: ").Append(d.KindType).Append('\n');
        if (d.UniqueKey.Count > 0) sb.Append("  unique_key: ").Append(YamlText.FlowList(d.UniqueKey)).Append('\n');
        if (d.TimeColumn != null) sb.Append("  time_column: ").Append(YamlText.Scalar(d.TimeColumn)).Append('\n');
        if (d.Lookback != null) sb.Append("  lookback: ").Append(YamlText.Scalar(d.Lookback)).Append('\n');
        if (d.Grain.Count > 0) sb.Append("grain: ").Append(YamlText.FlowList(d.Grain)).Append('\n');
        if (d.Targets is { Count: > 0 }) sb.Append("connections: ").Append(YamlText.FlowList(d.Targets)).Append('\n');

        sb.Append("columns:\n");
        foreach (var c in d.Columns)
        {
            sb.Append("  - name: ").Append(YamlText.Scalar(c.Name)).Append('\n');
            sb.Append("    type: ").Append(YamlText.Scalar(c.Type)).Append('\n');
            if (!c.Nullable) sb.Append("    nullable: false\n");
            if (c.Collation != null) sb.Append("    collation: ").Append(YamlText.Scalar(c.Collation)).Append('\n');
        }
        if (d.Renames.Count > 0)
        {
            sb.Append("renames:\n");
            foreach (var r in d.Renames)
                sb.Append("  - from: ").Append(YamlText.Scalar(r.From)).Append("\n    to: ").Append(YamlText.Scalar(r.To)).Append('\n');
        }
        if (d.Indexes.Count > 0)
        {
            sb.Append("indexes:\n");
            foreach (var i in d.Indexes)
            {
                sb.Append("  - {name: ").Append(YamlText.Scalar(i.Name)).Append(", columns: ").Append(YamlText.FlowList(i.Columns));
                if (i.Unique) sb.Append(", unique: true");
                if (i.Include.Count > 0) sb.Append(", include: ").Append(YamlText.FlowList(i.Include));
                if (i.Targets is { Count: > 0 }) sb.Append(", connections: ").Append(YamlText.FlowList(i.Targets));
                sb.Append("}\n");
            }
        }
        return sb.ToString();
    }
}
