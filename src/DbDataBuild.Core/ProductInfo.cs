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

    /// <summary>
    /// The release's version: the one `scripts/publish.sh` stamps (`VERSION`), and in a development build the one in Directory.Build.props, which a release bumps. It is in plans, in the JSON documents and in
    /// the tracking tables as provenance. It is not in anything rendered: a rendered file changes when its text changes, not when the tool is upgraded.
    /// </summary>
    public static string Version { get; } =
        typeof(ProductInfo).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false).OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "0.0.0";
}
