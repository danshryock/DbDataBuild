using System.Data.Common;
using DbDataBuild.Core;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace DbDataBuild.Execution;

public enum Login { Read, Write }

/// <summary>A connection string found in the environment. Never printed whole: <see cref="Describe"/> shows the variable and the user only.</summary>
public sealed class LoginSettings
{
    public string Target { get; }
    public Login Login { get; }
    public string Variable { get; }
    internal string ConnectionString { get; }

    private LoginSettings(string target, Login login, string variable, string connectionString)
    {
        Target = target; Login = login; Variable = variable; ConnectionString = connectionString;
    }

    /// <summary>`DBDATABUILD_SQLSERVER_READ`, `DBDATABUILD_POSTGRES_WRITE` and so on.</summary>
    public static string VariableName(string target, Login login) =>
        $"{ProductInfo.Cli.ToUpperInvariant()}_{target.ToUpperInvariant()}_{login.ToString().ToUpperInvariant()}";

    /// <summary>Reads the login from the environment. There is deliberately no fallback from one login to the other.</summary>
    public static (LoginSettings? Settings, Diagnostic? Error) FromEnvironment(string target, Login login, Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        var variable = VariableName(target, login);
        var value = env(variable);
        if (string.IsNullOrWhiteSpace(value))
            return (null, new Diagnostic(DiagnosticCatalog.LoginNotConfigured, new($"env:{variable}", 0, 0),
                $"The {login.ToString().ToLowerInvariant()} login for target `{target}` is not configured: environment variable `{variable}` is not set."));
        return (new LoginSettings(target, login, variable, value), null);
    }

    /// <summary>For the command header (DESIGN.md 9.1): which variable and which user, never the password or the rest of the string.</summary>
    public string Describe() => $"{User ?? "integrated/default"} ({Variable})";

    public string? User
    {
        get
        {
            try
            {
                if (Target == "postgres") return new NpgsqlConnectionStringBuilder(ConnectionString).Username;
                var b = new SqlConnectionStringBuilder(ConnectionString);
                return string.IsNullOrEmpty(b.UserID) ? null : b.UserID;
            }
            catch (ArgumentException) { return null; }
        }
    }

    /// <summary>Opens a connection of the right provider. Internal on purpose: the write side is reached only through <see cref="MutationGate"/>, the read side through <see cref="ReadSession"/>.</summary>
    internal async Task<DbConnection> OpenAsync(CancellationToken ct)
    {
        DbConnection c = Target == "postgres"
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
