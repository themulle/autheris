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

/// <summary>Credential-free description of a SQL data source connection.</summary>
/// <param name="Host">Host from the connection string (no user, password or database); empty when unknown.</param>
/// <param name="Port">Explicit port from the connection string, or null when none is given.</param>
/// <param name="Dialect">Provider/dialect name.</param>
/// <param name="AuthPath"><c>SqlPassword</c>, <c>Integrated-ManagedNtlm</c>, <c>Integrated-GSSAPI</c> or <c>Integrated-Kerberos</c> (or <c>n/a</c>).</param>
public sealed record SqlSourceDescription(string Host, int? Port, string Dialect, string AuthPath);

/// <summary>Describes a configured SQL data source without secrets and without connecting.</summary>
public interface ISqlSourceDescriber
{
    SqlSourceDescription Describe(DataSourceConnectionOptions options);
}
