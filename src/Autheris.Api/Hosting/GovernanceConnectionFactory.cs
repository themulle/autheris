namespace Autheris.Api.Hosting;

using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.State;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Autheris.Modules.Abstractions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;

/// <summary>
/// Implementation of <see cref="IGovernanceConnectionFactory"/> that opens connections exclusively
/// to the configured governance store (<see cref="GovernanceDbOptions"/>) and never accesses governed customer data sources.
/// </summary>
public sealed class GovernanceConnectionFactory : IGovernanceConnectionFactory, IDisposable, IAsyncDisposable
{
    private readonly GovernanceDbOptions _governanceDb;
    private readonly SqliteGovernanceConnectionProvider? _sqliteProvider;

    public GovernanceConnectionFactory(IServiceProvider services)
        : this(
            services?.GetService<IOptions<GatewayOptions>>()?.Value?.GovernanceDb ?? new GovernanceDbOptions(),
            services?.GetService<IGatewayStatePaths>(),
            services?.GetService<IHostEnvironment>())
    {
    }

    public GovernanceConnectionFactory(
        IOptions<GatewayOptions> gatewayOptions,
        IGatewayStatePaths? statePaths = null,
        IHostEnvironment? environment = null)
        : this(
            gatewayOptions?.Value?.GovernanceDb ?? new GovernanceDbOptions(),
            statePaths,
            environment)
    {
    }

    public GovernanceConnectionFactory(
        GovernanceDbOptions governanceDb,
        IGatewayStatePaths? statePaths = null,
        IHostEnvironment? environment = null)
    {
        ArgumentNullException.ThrowIfNull(governanceDb);
        _governanceDb = governanceDb;

        if (!DataSourceProvider.Is(_governanceDb.Provider, DatabaseDialect.PostgreSql) &&
            !DataSourceProvider.Is(_governanceDb.Provider, DatabaseDialect.SqlServer))
        {
            var resolvedConnStr = statePaths is not null
                ? statePaths.ResolveGovernanceConnectionString(_governanceDb.ConnectionString)
                : (string.IsNullOrWhiteSpace(_governanceDb.ConnectionString)
                    ? "Data Source=:memory:"
                    : _governanceDb.ConnectionString);

            bool allowUnsafeJournal = environment is not null &&
                string.Equals(environment.EnvironmentName, "Development", StringComparison.OrdinalIgnoreCase);

            _sqliteProvider = new SqliteGovernanceConnectionProvider(
                resolvedConnStr,
                _governanceDb.JournalMode,
                logger: null,
                allowUnsafeJournalModes: allowUnsafeJournal);
        }
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

        return await _sqliteProvider!.OpenAsync(ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _sqliteProvider?.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        return _sqliteProvider?.DisposeAsync() ?? ValueTask.CompletedTask;
    }
}
