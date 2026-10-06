using System.Data.Common;
using DbDataBuild.Core;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace DbDataBuild.Execution;

public enum Login { Read, Write }

/// <summary>A connection string found in the environment. Never printed whole: <see cref="Describe"/> shows the variable and the user only.</summary>
public sealed class LoginSettings
{
    static LoginSettings() => DriverSettings.Apply();

    /// <summary>The connection this login is for (its name: `warehouse`, or `sqlserver` for a connection named after its engine).</summary>
    public string Connection { get; }

    /// <summary>The engine of that connection (sqlserver, fabric, postgres): what decides the driver and the SQL.</summary>
    public string Engine { get; }
    public Login Login { get; }
    public string Variable { get; }
    internal string ConnectionString { get; }

    private LoginSettings(string connection, string engine, Login login, string variable, string connectionString)
    {
        Connection = connection; Engine = engine; Login = login; Variable = variable; ConnectionString = connectionString;
    }

    /// <summary>`DBDATABUILD_SQLSERVER_READ`, `DBDATABUILD_WAREHOUSE_WRITE` and so on: the connection's name, in capitals.</summary>
    public static string VariableName(string connection, Login login) =>
        $"{ProductInfo.Cli.ToUpperInvariant()}_{connection.ToUpperInvariant()}_{login.ToString().ToUpperInvariant()}";

    /// <summary>Reads the login from the environment. There is deliberately no fallback from one login to the other.</summary>
    public static (LoginSettings? Settings, Diagnostic? Error) FromEnvironment(string connection, string engine, Login login, Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        var variable = VariableName(connection, login);
        var value = env(variable);
        if (string.IsNullOrWhiteSpace(value))
            return (null, new Diagnostic(DiagnosticCatalog.LoginNotConfigured, new($"env:{variable}", 0, 0),
                $"The {login.ToString().ToLowerInvariant()} login for connection `{connection}` is not configured: environment variable `{variable}` is not set."));
        return (new LoginSettings(connection, engine, login, variable, value), null);
    }

    /// <summary>For a connection named after its engine (`sqlserver`, `postgres`, `fabric`), which needs no declaration.</summary>
    public static (LoginSettings? Settings, Diagnostic? Error) FromEnvironment(string connection, Login login, Func<string, string?>? env = null) =>
        FromEnvironment(connection, connection, login, env);

    /// <summary>For the command header (DESIGN.md 9.1): which variable and which user, never the password or the rest of the string.</summary>
    public string Describe() => $"{User ?? "integrated/default"} ({Variable})";

    public string? User
    {
        get
        {
            try
            {
                if (Engine == "postgres") return new NpgsqlConnectionStringBuilder(ConnectionString).Username;
                var b = new SqlConnectionStringBuilder(ConnectionString);
                return string.IsNullOrEmpty(b.UserID) ? null : b.UserID;
            }
            catch (ArgumentException) { return null; }
        }
    }

    /// <summary>Opens a connection of the right provider. Internal on purpose: the write side is reached only through <see cref="MutationGate"/>, the read side through <see cref="ReadSession"/>.</summary>
    internal async Task<DbConnection> OpenAsync(CancellationToken ct)
    {
        DbConnection c = Engine == "postgres"
            ? new NpgsqlConnection(new NpgsqlConnectionStringBuilder(ConnectionString)
            {
                // a read login's session is also read-only at the session level (a second layer; the login's permissions are the real one)
                Options = Login == Login.Read ? "-c default_transaction_read_only=on" : null,
            }.ConnectionString)
            : new SqlConnection(new SqlConnectionStringBuilder(ConnectionString) { ApplicationName = Login == Login.Read ? $"{ProductInfo.Cli}-read" : $"{ProductInfo.Cli}-write" }.ConnectionString);
        await c.OpenAsync(ct);
        return c;
    }
}
