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
    public const string Oracle = "DBDATABUILD_TEST_ORACLE";
    public const string Spark = "DBDATABUILD_TEST_SPARK";
    public const string BigQuery = "DBDATABUILD_TEST_BIGQUERY";

    public static string? Get(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

    /// <summary>Creates the engine for a test, or skips the test (visibly) when it is not configured.</summary>
    public static Engine Require(string engine)
    {
        var variable = engine == "sqlserver" ? SqlServer : Postgres;
        Skip.If(Get(variable) == null, $"Set {variable} to a connection string for a local throwaway {engine} (run scripts/test-engines.sh up).");
        return engine == "sqlserver" ? new SqlServerEngine() : new PostgresEngine();
    }

    /// <summary>
    /// The engine for a dialect probe: the two the whole tool runs on, or one of the engines that are only probed (Oracle, Spark SQL, the BigQuery emulator). A test is skipped, visibly, when the engine is not running.
    /// </summary>
    public static IProbeEngine RequireProbe(string engine)
    {
        var variable = engine switch { "sqlserver" => SqlServer, "postgres" => Postgres, "oracle" => Oracle, "spark" => Spark, "bigquery" => BigQuery, _ => throw new ArgumentException($"Unknown engine `{engine}`.") };
        Skip.If(Get(variable) == null, $"Set {variable} to a connection for a local throwaway {engine} (run scripts/test-engines.sh up {engine}).");
        return engine switch
        {
            "sqlserver" => new SqlServerEngine(),
            "postgres" => new PostgresEngine(),
            "oracle" => new OracleProbeEngine(Get(variable)!),
            "spark" => new SparkProbeEngine(Get(variable)!),
            _ => new BigQueryProbeEngine(Get(variable)!),
        };
    }
}
