namespace Autheris.Tests.Integration.Data;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.DataSources.Relations;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Common;
using Microsoft.Extensions.Options;
using Npgsql;
using Shouldly;

/// <summary>
/// PostgreSQL integration tests for <see cref="GovernedCrossSourceRowReader"/>.
/// </summary>
[Trait("Category", "Security")]
public sealed class GovernedCrossSourceRowReaderIntegrationTests
{
    private static readonly TenantId Tenant = new("tenant-a");

    private static TableMetadata Target() => new()
    {
        Identifier = new TableIdentifier("crm", "public", "Customers"),
        Table = new Table { SourceName = "crm", SchemaName = "public", TableName = "Customers", SourceType = "PostgreSql", DataSourceType = DataSourceType.Sql, IsActive = true },
        Columns = [new TableColumn { ColumnName = "CustomerId" }, new TableColumn { ColumnName = "Name" }, new TableColumn { ColumnName = "tenant_id" }]
    };

    private static CrossSourceKeyReadRequest Request(TableMetadata target) =>
        new(target, ["customerid"], ["name"], [new CompositeKey([7])], Tenant, "S-1-5-1");

    [RequiresContainerRuntimeFact]
    public async Task ReadByKeys_InitializesSessionWithTenantAndUser_AndReturnsOnlyOwnTenantRows()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable)
        {
            return;
        }

        await using (var conn = new NpgsqlConnection(db.ConnectionString))
        {
            await conn.OpenAsync();
            await using var setup = conn.CreateCommand();
            setup.CommandText = """
                CREATE TABLE public."Customers" ("CustomerId" integer, "Name" text, "tenant_id" text);
                INSERT INTO public."Customers" VALUES (7, 'own', 'tenant-a'), (7, 'foreign', 'tenant-b'), (7, 'none', NULL);
                """;
            await setup.ExecuteNonQueryAsync();
        }

        var initializer = new RecordingSessionInitializer();
        var options = Options.Create(new GatewayOptions
        {
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions> { ["crm"] = new() { Provider = "PostgreSql", ConnectionString = db.ConnectionString } }
            }
        });
        var reader = new GovernedCrossSourceRowReader(new NpgsqlConnectionFactory(), options, initializer);

        var rows = await reader.ReadRowsByKeysAsync(Request(Target()));

        rows.Select(r => r["Name"]).ShouldBe(["own"]);
        initializer.Calls.ShouldBe([(DatabaseDialect.PostgreSql, Tenant, "S-1-5-1", null)]);
    }

    private sealed class NpgsqlConnectionFactory : ISqlConnectionFactory
    {
        public async Task<DbConnection> CreateOpenConnectionAsync(DataSourceConnectionOptions options, CancellationToken ct = default)
        {
            var connection = new NpgsqlConnection(options.ConnectionString);
            await connection.OpenAsync(ct);
            return connection;
        }
    }

    private sealed class RecordingSessionInitializer : IDbSessionContextInitializer
    {
        public List<(DatabaseDialect Dialect, TenantId TenantId, string? UserSid, string? Purpose)> Calls { get; } = [];

        public Task<DbTransaction?> InitializeSessionAsync(
            DbConnection connection, DatabaseDialect dialect, TenantId tenantId,
            string? userSid = null, string? purpose = null, bool requireTransaction = true, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<DbTransaction?> InitializeSessionAsync(
            DbConnection connection, string? provider, TenantId tenantId,
            string? userSid = null, string? purpose = null, bool requireTransaction = true, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task InitializeSessionAsync(
            DbConnection connection, DbTransaction? tx, DatabaseDialect dialect, TenantId tenantId,
            string? userSid = null, string? purpose = null, CancellationToken ct = default)
        {
            Calls.Add((dialect, tenantId, userSid, purpose));
            return Task.CompletedTask;
        }
    }
}
