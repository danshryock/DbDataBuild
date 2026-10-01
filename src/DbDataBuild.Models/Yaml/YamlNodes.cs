namespace DbDataBuild.Models.Yaml;

/// <summary>Node of the strict YAML subset. All scalars are strings; every node carries its source position (1-based).</summary>
public abstract record YamlNode(int Line, int Column);

public sealed record YamlScalar(string Value, int Line, int Column) : YamlNode(Line, Column);

public sealed record YamlSequence(IReadOnlyList<YamlNode> Items, int Line, int Column) : YamlNode(Line, Column);

public sealed record YamlEntry(YamlScalar Key, YamlNode Value);

public sealed record YamlMapping(IReadOnlyList<YamlEntry> Entries, int Line, int Column) : YamlNode(Line, Column)
{
    public YamlNode? Get(string key) => Entries.FirstOrDefault(e => e.Key.Value == key)?.Value;
}
