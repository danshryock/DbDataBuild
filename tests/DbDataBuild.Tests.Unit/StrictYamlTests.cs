using DbDataBuild.Core;
using DbDataBuild.Models.Yaml;

namespace DbDataBuild.Tests.Unit;

public class StrictYamlTests
{
    private static (YamlNode? Node, List<Diagnostic> Diags) Read(string yaml)
    {
        var d = new List<Diagnostic>();
        return (StrictYamlReader.Read(yaml, "t.yml", d), d);
    }

    [Fact]
    public void Scalars_are_never_coerced()
    {
        var (node, diags) = Read("a: no\nb: on\nc: 2026-10-12\nd: 12\ne: null\n");
        Assert.Empty(diags);
        var map = Assert.IsType<YamlMapping>(node);
        Assert.Equal(["no", "on", "2026-10-12", "12", "null"],
            map.Entries.Select(e => Assert.IsType<YamlScalar>(e.Value).Value));
    }

    [Fact]
    public void Nodes_carry_one_based_positions()
    {
        var (node, _) = Read("a: 1\nb:\n  - x\n");
        var map = Assert.IsType<YamlMapping>(node);
        Assert.Equal(2, map.Entries[1].Key.Line);
        Assert.Equal(1, map.Entries[1].Key.Column);
        Assert.Equal(3, Assert.IsType<YamlSequence>(map.Entries[1].Value).Items[0].Line);
    }

    [Fact]
    public void Duplicate_keys_are_errors_with_position()
    {
        var (_, diags) = Read("a: 1\nb: 2\na: 3\n");
        var d = Assert.Single(diags);
        Assert.Equal("DDB-102", d.Code);
        Assert.Equal(3, d.Location.Line);
    }

    [Theory]
    [InlineData("a: &x 1\n", "Anchor")]
    [InlineData("a: &x 1\nb: *x\n", "Alias")]
    [InlineData("a: !!str 1\n", "Tag")]
    [InlineData("a: {x: 1}\nb:\n  <<: {y: 2}\n", "Merge key")]
    public void Anchors_aliases_merge_keys_and_tags_are_rejected(string yaml, string expected)
    {
        var (_, diags) = Read(yaml);
        Assert.Contains(diags, d => d.Code == "DDB-103" && d.Found.StartsWith(expected));
    }

    [Fact]
    public void Syntax_errors_are_diagnostics_not_exceptions()
    {
        var (node, diags) = Read("a: [1, 2\nb: : :\n");
        Assert.Null(node);
        Assert.Equal("DDB-101", Assert.Single(diags).Code);
    }

    [Fact]
    public void Multiple_documents_are_rejected()
    {
        var (_, diags) = Read("a: 1\n---\nb: 2\n");
        Assert.Contains(diags, d => d.Code == "DDB-101");
    }
}
