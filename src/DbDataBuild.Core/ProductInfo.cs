namespace DbDataBuild.Core;

/// <summary>Single source for every product-derived name, so a rename is mechanical.</summary>
public static class ProductInfo
{
    public const string Name = "DbDataBuild";
    public const string Cli = "dbdatabuild";
    public const string ConfigFile = "dbdatabuild.yml";
    public const string TrackingSchema = "dbdatabuild";
    public const string DiagnosticPrefix = "DDB-";
    public const string NamespacePrefix = "DbDataBuild";
    public const string Version = "0.1.0";
}
