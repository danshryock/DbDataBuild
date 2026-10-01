using DbDataBuild.Core;
using DbDataBuild.Models;

namespace DbDataBuild.Tests.Unit;

internal static class TestSupport
{
    public const string ValidModel = """
        name: marts.fct_orders
        kind:
          type: incremental_by_unique_key
          unique_key: [order_id]
        grain: [order_id]
        targets: [sqlserver, fabric]
        columns:
          - name: order_id
            type: BIGINT
            nullable: false
          - name: customer_id
            type: BIGINT
          - name: amount
            type: DECIMAL(14, 2)
        """;

    public static (ModelDefinition? Def, List<Diagnostic> Diags) Load(string yaml, string? expectedName = "marts.fct_orders")
    {
        var diags = new List<Diagnostic>();
        var def = ModelDefinitionLoader.Load(yaml, "models/marts/fct_orders.yml", expectedName, diags);
        return (def, diags);
    }

    public static string NewProjectDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ddb-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "models", "marts"));
        return dir;
    }

    /// <summary>Hash of every file path and content under a directory, for "nothing was written" assertions.</summary>
    public static string Snapshot(string dir) => string.Join("\n",
        Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => File.Exists(p) ? p + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p))) : p));
}
