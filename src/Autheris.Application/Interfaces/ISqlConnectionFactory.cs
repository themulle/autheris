using System.Data.Common;
using Autheris.Domain.Options;

namespace Autheris.Application.Interfaces;

public interface ISqlConnectionFactory
{
    Task<DbConnection> CreateOpenConnectionAsync(DataSourceConnectionOptions options, CancellationToken ct = default);
}
