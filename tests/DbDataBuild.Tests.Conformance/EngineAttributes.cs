using Xunit;

namespace DbDataBuild.Tests.Conformance;

/// <summary>
/// Environment variables that point the conformance suite at throwaway local engines. `scripts/test-engines.sh up` starts them and prints
/// the exports. Without them the tests are reported as skipped, with this reason; they are never silently passed.
/// </summary>
public static class EngineEnv
{
    public const string SqlServer = "DBDATABUILD_TEST_MSSQL";
    public const string Postgres = "DBDATABUILD_TEST_PG";

    public static string? Get(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

    /// <summary>Creates the engine for a test, or skips the test (visibly) when it is not configured.</summary>
    public static Engine Require(string engine)
    {
        var variable = engine == "sqlserver" ? SqlServer : Postgres;
        Skip.If(Get(variable) == null, $"Set {variable} to a connection string for a local throwaway {engine} (run scripts/test-engines.sh up).");
        return engine == "sqlserver" ? new SqlServerEngine() : new PostgresEngine();
    }
}
