using System.Data.Common;
using Autheris.Domain.Options;

namespace Autheris.Application.Interfaces;

public interface ISqlConnectionFactory
{
    Task<DbConnection> CreateOpenConnectionAsync(DataSourceConnectionOptions options, CancellationToken ct = default);
}

/// <summary>
/// WP-X0b, SEC-X1-08/14: a data source cannot hand out a correctly pinned session (missing or invalid default schema, a pin that
/// did not verify). Fail closed: the caller maps it to <see cref="ErrorCode"/>. The message never echoes configuration values.
/// </summary>
public sealed class SqlSourceUnavailableException : InvalidOperationException
{
    public const string ErrorCode = "SQL_SOURCE_UNAVAILABLE";

    public SqlSourceUnavailableException(string message) : base(message)
    {
    }
}
