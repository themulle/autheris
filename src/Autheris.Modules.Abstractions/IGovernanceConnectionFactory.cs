namespace Autheris.Modules.Abstractions;

using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Opens database connections to the governance store only, never to governed customer data sources.
/// </summary>
public interface IGovernanceConnectionFactory
{
    /// <summary>
    /// Opens and returns a connection to the configured governance database.
    /// </summary>
    ValueTask<DbConnection> OpenGovernanceConnectionAsync(CancellationToken ct = default);
}
