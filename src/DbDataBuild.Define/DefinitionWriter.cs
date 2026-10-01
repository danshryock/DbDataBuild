using System.Text;
using DbDataBuild.Models;

namespace DbDataBuild.Define;

/// <summary>
/// Writes a new definition in canonical formatting with a fixed key order (DESIGN.md 6.5):
/// name, kind, grain, targets, columns, renames. <c>nullable</c> is written only when false.
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
        if (d.Targets is { Count: > 0 }) sb.Append("targets: ").Append(YamlText.FlowList(d.Targets)).Append('\n');

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
        return sb.ToString();
    }
}
