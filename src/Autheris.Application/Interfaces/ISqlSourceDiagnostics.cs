using Autheris.Domain.Options;

namespace Autheris.Application.Interfaces;

/// <summary>
/// An exception of a SQL data source whose <see cref="Exception.Message"/> is safe to log and to return to an operator
/// (never a password, secret or raw driver text). <see cref="DiagnosticFields"/> carries non-secret diagnostic values only.
/// </summary>
public interface ISqlSourceDiagnosticException
{
    /// <summary>Stable category name, for example <c>NtlmNegotiation</c>.</summary>
    string CategoryName { get; }

    /// <summary>Non-secret diagnostic values (SqlException number/state/class, NTLM status and step, exception type names).</summary>
    IReadOnlyDictionary<string, string> DiagnosticFields { get; }
}

/// <summary>The data source has no entry in <c>DataSources:Connections</c>. The message contains only the data source name.</summary>
public sealed class SqlSourceNotConfiguredException : InvalidOperationException, ISqlSourceDiagnosticException
{
    public SqlSourceNotConfiguredException(string dataSourceName)
        : base($"The data source '{dataSourceName}' has no configured connection.")
    {
    }

    public string CategoryName => "NotConfigured";

    public IReadOnlyDictionary<string, string> DiagnosticFields { get; } = new Dictionary<string, string>();
}

/// <summary>
/// The login circuit breaker of a data source is open: after a login failure (18456, 18452, 18451, rejected or credential-type NTLM failure)
/// the server is not contacted again until the block expires, so repeated attempts cannot drive the Active Directory bad-password counter.
/// </summary>
public sealed class SqlSourceLoginBlockedException : InvalidOperationException, ISqlSourceDiagnosticException
{
    public SqlSourceLoginBlockedException(TimeSpan remaining, string reason)
        : base($"Connections to this data source are blocked for another {Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds))} s after a login failure ({reason}) " +
               "to protect the account from lockout. Correct the credentials or configuration; a changed configuration lifts the block immediately.")
    {
        Remaining = remaining;
        Reason = reason;
        DiagnosticFields = new Dictionary<string, string>
        {
            ["RemainingSeconds"] = Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["BlockReason"] = reason
        };
    }

    public TimeSpan Remaining { get; }

    public string Reason { get; }

    public const string Category = "LoginBlocked";

    public string CategoryName => Category;

    public IReadOnlyDictionary<string, string> DiagnosticFields { get; }
}

/// <summary>Credential-free description of a SQL data source connection.</summary>
/// <param name="Host">Host from the connection string (no user, password or database); empty when unknown.</param>
/// <param name="Port">Explicit port from the connection string, or null when none is given.</param>
/// <param name="Dialect">Provider/dialect name.</param>
/// <param name="AuthPath"><c>SqlPassword</c>, <c>Integrated-ManagedNtlm</c>, <c>Integrated-GSSAPI</c> or <c>Integrated-Kerberos</c> (or <c>n/a</c>).</param>
/// <param name="LoginName">Effective login: <c>DOMAIN\user</c> of the configured Windows account, the SQL login user id or a marker for ambient credentials; never the password.</param>
public sealed record SqlSourceDescription(string Host, int? Port, string Dialect, string AuthPath, string? LoginName = null);

/// <summary>Describes a configured SQL data source without secrets and without connecting.</summary>
public interface ISqlSourceDescriber
{
    SqlSourceDescription Describe(DataSourceConnectionOptions options);
}
