namespace Autheris.Api.Hosting;

using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Autheris.Modules.Abstractions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

/// <summary>
/// Implementation of <see cref="IGovernanceConnectionFactory"/> that opens connections exclusively
/// to the configured governance store (<see cref="GovernanceDbOptions"/>) and never accesses governed customer data sources.
/// </summary>
public sealed class GovernanceConnectionFactory : IGovernanceConnectionFactory, IDisposable, IAsyncDisposable
{
    private readonly GovernanceDbOptions _governanceDb;

    public GovernanceConnectionFactory(IServiceProvider services)
        : this(services?.GetService<IOptions<GatewayOptions>>()?.Value?.GovernanceDb ?? new GovernanceDbOptions())
    {
    }

    public GovernanceConnectionFactory(GovernanceDbOptions governanceDb)
    {
        ArgumentNullException.ThrowIfNull(governanceDb);
        _governanceDb = governanceDb;
    }

    public async ValueTask<DbConnection> OpenGovernanceConnectionAsync(CancellationToken ct = default)
    {
        if (DataSourceProvider.Is(_governanceDb.Provider, DatabaseDialect.PostgreSql))
        {
            if (string.IsNullOrWhiteSpace(_governanceDb.ConnectionString))
            {
                throw new InvalidOperationException("GovernanceDb:ConnectionString is required when Provider is PostgreSql.");
            }

            var pgConn = new NpgsqlConnection(_governanceDb.ConnectionString);
            try
            {
                await pgConn.OpenAsync(ct).ConfigureAwait(false);
                return pgConn;
            }
            catch
            {
                await pgConn.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        if (DataSourceProvider.Is(_governanceDb.Provider, DatabaseDialect.SqlServer))
        {
            if (string.IsNullOrWhiteSpace(_governanceDb.ConnectionString))
            {
                throw new InvalidOperationException("GovernanceDb:ConnectionString is required when Provider is SqlServer.");
            }

            var sqlConn = new SqlConnection(_governanceDb.ConnectionString);
            try
            {
                await sqlConn.OpenAsync(ct).ConfigureAwait(false);
                return sqlConn;
            }
            catch
            {
                await sqlConn.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        // Fail closed: the guard rejects other providers at startup, so reaching this line means a misconfigured composition.
        throw new InvalidOperationException("Governance provider unsupported (OD-1). See docs/operations/migration-simplification.md.");
    }

    public void Dispose()
    {
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
