using DbDataBuild.Core;
using DbDataBuild.Models.Yaml;

namespace DbDataBuild.Tests.Unit;

/// <summary>The merge rules every layer of a project goes through, as one table.</summary>
public class YamlMergeTests
{
    private static readonly IReadOnlySet<string> Layered = new HashSet<string> { "connections", "kind", "hooks", "rewrites", "lint_ignore" };

    private static YamlLayer Layer(string file, string text)
    {
        var diags = new List<Diagnostic>();
        var root = (YamlMapping)StrictYamlReader.Read(text, file, diags)!;
        Assert.Empty(diags);
        return new YamlLayer(file, root);
    }

    /// <summary>The merged result written as `path=value` lines, so a case reads as the table it is.</summary>
    private static (string Result, List<Diagnostic> Diags) Merge(params string[] layers)
    {
        var diags = new List<Diagnostic>();
        var merged = YamlMerge.Merge(layers.Select((t, i) => Layer($"f{i}.yml", t)).ToList(), Layered, diags);
        return (string.Join(";", merged.Origins().Select(o => $"{o.Path}={o.Value}")), diags);
    }

    [Theory]
    // scalars replace, maps merge by key and the nearer layer wins a conflict
    [InlineData("kind: {type: full}", "kind: {type: view}", "kind.type=view")]
    [InlineData("kind: {type: incremental_by_unique_key, unique_key: [id]}", "kind: {type: incremental_by_unique_key, time_column: ts}", "kind.type=incremental_by_unique_key;kind.unique_key[0]=id;kind.time_column=ts")]
    // lists append, the inherited first, a repeated scalar once
    [InlineData("connections: [a, b]", "connections: [b, c]", "connections[0]=a;connections[1]=b;connections[2]=c")]
    // a list of mappings with a name merges by name; the others append
    [InlineData("hooks: [{name: grant, event: post_create, effect: ddl}]", "hooks: [{name: grant, effect: dml}, {name: audit, event: post_load}]",
        "hooks[0].name=grant;hooks[0].event=post_create;hooks[0].effect=dml;hooks[1].name=audit;hooks[1].event=post_load")]
    // `=` resets, `-` removes, `+` is the default said out loud
    [InlineData("connections: [a, b]", "connections=: [c]", "connections[0]=c")]
    [InlineData("connections: [a, b, c]", "connections-: [b]", "connections[0]=a;connections[1]=c")]
    [InlineData("kind: {type: full, unique_key: [id]}", "kind-: [unique_key]", "kind.type=full")]
    [InlineData("hooks: [{name: grant, event: post_create}, {name: audit, event: post_load}]", "hooks-: [grant]", "hooks[0].name=audit;hooks[0].event=post_load")]
    [InlineData("connections: [a]", "connections+: [b]", "connections[0]=a;connections[1]=b")]
    [InlineData("kind: {type: full, unique_key: [id]}", "kind=: {type: view}", "kind.type=view")]
    [InlineData("lint_ignore: [x]", "lint_ignore=: []", "")]
    // nothing above: a reset or an explicit merge is just the value, a removal has nothing to remove
    [InlineData("name: a", "connections=: [c]", "name=a;connections[0]=c")]
    [InlineData("name: a", "connections-: [c]", "name=a")]
    [InlineData("name: a", "connections+: [c]", "name=a;connections[0]=c")]
    // a key that does not inherit keeps its name as written (the loader calls it unknown), and is still the nearest value
    [InlineData("name: a", "columns=: [x]", "name=a;columns=[0]=x")]
    [InlineData("name: a", "name: b", "name=b")]
    public void The_merge_rules(string below, string above, string expected)
    {
        var (result, diags) = Merge(below, above);
        Assert.Empty(diags);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Three_layers_merge_root_down_and_the_nearest_wins()
    {
        var (result, diags) = Merge("connections: [a]\nkind: {type: full}", "connections: [b]\nrewrites: {fidelity: native}", "connections=: [c]\nkind: {type: view}");
        Assert.Empty(diags);
        Assert.Equal("connections[0]=c;kind.type=view;rewrites.fidelity=native", result);
    }

    [Theory]
    [InlineData("kind: {type: full}", "kind: [a]", "`kind` is a list here and a mapping in a file above it")]        // a list over a mapping: `kind=` says replace
    [InlineData("connections: [a]", "connections: a", "`connections` is a single value here and a list in a file above it")]
    [InlineData("kind: {type: full}", "kind+: view", "has a suffix on a single value")]                               // only `=` applies to one value
    [InlineData("kind: {type: full}", "kind-: view", "takes a list of the items (or keys) to remove")]
    [InlineData("kind: {type: full}", "kind: {type-: [full]}", "removes from a list or a mapping, and `type` is a single value")]
    public void A_layer_that_cannot_merge_is_a_diagnostic_naming_the_key_and_the_file_it_is_in(string below, string above, string expected)
    {
        var (_, diags) = Merge(below, above);
        var d = Assert.Single(diags);
        Assert.Contains(expected, d.Found);
        Assert.Equal("f1.yml", d.Location.File);
    }

    [Fact]
    public void Every_scalar_knows_the_file_and_line_it_was_written_on()
    {
        var diags = new List<Diagnostic>();
        var merged = YamlMerge.Merge([Layer("root.yml", "connections: [a]\nkind:\n  type: full\n"), Layer("models/m.yml", "name: m\nconnections: [b]\n")], Layered, diags);
        Assert.Empty(diags);
        Assert.Equal(["connections[0]=root.yml:1", "connections[1]=models/m.yml:2", "kind.type=root.yml:3", "name=models/m.yml:1"],
            merged.Origins().Select(o => $"{o.Path}={o.File}:{o.Line}").Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Merging_changes_none_of_the_files_it_read()
    {
        var below = Layer("a.yml", "connections: [a]\n");
        var above = Layer("b.yml", "connections: [b]\n");
        YamlMerge.Merge([below, above], Layered, []);
        Assert.Single(((YamlSequence)below.Root.Get("connections")!).Items);
        Assert.Single(((YamlSequence)above.Root.Get("connections")!).Items);
    }
}
