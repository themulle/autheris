namespace Autheris.Tests.Unit.Sql;

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class GovernedSqlExecutionMultiSchemaShortNameTests
{
    private const string Tenant = "tenant_multi";
    private const string DataSource = "pg_multi";

    private static ClaimsPrincipal CreateUser() =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.PrimarySid, "S-1-5-21-USER-1"),
                new Claim("tenant_id", Tenant),
                new Claim(ClaimTypes.Role, "Admin")
            ],
            "Test"));

    private static TableMetadata CreateTable(TableIdentifier id, bool hasTenantColumn) => new()
    {
        Identifier = id,
        Table = new Table
        {
            TableName = id.TableName,
            SchemaName = id.Schema,
            SourceName = DataSource,
            SourceType = "PostgreSql"
        },
        Columns = hasTenantColumn
            ?
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "tenant_id", DataType = "varchar" },
                new TableColumn { ColumnName = "amount", DataType = "decimal" }
            ]
            :
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "amount", DataType = "decimal" }
            ],
        ColumnMaskingRules = new Dictionary<string, MaskingRule>()
    };

    [Fact]
    public async Task MultiSchema_Same_TableName_Does_Not_Overwrite_Rls_On_Protected_Table()
    {
        var publicId = new TableIdentifier(DataSource, "public", "orders");
        var archiveId = new TableIdentifier(DataSource, "archive", "orders");

        var publicMeta = CreateTable(publicId, hasTenantColumn: true);
        var archiveMeta = CreateTable(archiveId, hasTenantColumn: false);

        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Arg.Is<TableIdentifier>(t => t.Schema == "public"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TableMetadata?>(publicMeta));
        repo.GetTableMetadataAsync(Arg.Is<TableIdentifier>(t => t.Schema == "archive"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TableMetadata?>(archiveMeta));

        var consents = Substitute.For<IConsentRepository>();
        consents.GetActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>(Array.Empty<Consent>()));

        var resolution = Substitute.For<IConsentResolutionService>();
        resolution.ResolveAccess(Arg.Any<Sid>(), Arg.Any<IReadOnlySet<Sid>>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<TableIdentifier>(), Arg.Any<IReadOnlyList<Consent>>(), Arg.Any<DatabaseDialect>())
            .Returns(ci => TableAccessDecision.Allowed(
                ci.ArgAt<TableIdentifier>(3),
                new Dictionary<string, ColumnAccessLevel>
                {
                    ["id"] = ColumnAccessLevel.Clear,
                    ["tenant_id"] = ColumnAccessLevel.Clear,
                    ["amount"] = ColumnAccessLevel.Clear
                },
                null,
                hasUnconstrainedColumnAllow: true));

        var options = new GatewayOptions
        {
            WebSql = new WebSqlOptions
            {
                Enabled = true,
                DefaultDataSourceName = DataSource,
                AllowedDataSources = [DataSource],
                DmlWriterRoles = ["Admin"]
            },
            DataSources = new SqlDataSourceOptions
            {
                TenantColumnExemptTables = [archiveId.ToString(), archiveId.ToQualifiedName()],
                Connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    [DataSource] = new() { Provider = "PostgreSql", ConnectionString = "Host=localhost;Database=testdb" }
                }
            }
        };

        var factory = Substitute.For<ISqlConnectionFactory>();
        var conn = Substitute.For<DbConnection>();
        var cmd = Substitute.For<DbCommand>();
        var reader = Substitute.For<DbDataReader>();
        reader.ReadAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(false));
        reader.FieldCount.Returns(0);
        cmd.ExecuteReaderAsync(Arg.Any<CommandBehavior>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(reader));
        cmd.Parameters.Returns(Substitute.For<DbParameterCollection>());
        cmd.CreateParameter().Returns(Substitute.For<DbParameter>());
        conn.CreateCommand().Returns(cmd);
        factory.CreateOpenConnectionAsync(Arg.Any<DataSourceConnectionOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(conn));

        var service = new GovernedSqlExecutionService(
            Options.Create(options),
            policyEnforcement: null,
            consentResolution: resolution,
            tableRepository: repo,
            auditLogRepository: null,
            connectionFactory: factory,
            clientIpResolver: null,
            environment: null,
            logger: NullLogger<GovernedSqlExecutionService>.Instance,
            consentRepository: consents,
            secretProvider: null,
            sessionInitializer: Substitute.For<IDbSessionContextInitializer>(),
            mandatoryFilters: null);

        // Query referencing both public.orders and archive.orders
        var sql = "SELECT p.id, a.id FROM public.orders p JOIN archive.orders a ON p.id = a.id";
        var request = new GovernedSqlQueryRequest(sql, null, DataSource);

        await service.ExecuteQueryBufferedAsync(request, CreateUser(), new TenantId(Tenant));

        cmd.CommandText.ShouldNotBeNull();
        // public.orders tenant isolation must NOT have been removed by archive.orders
        cmd.CommandText.ShouldContain("tenant_id = 'tenant_multi'");
    }

    [Fact]
    public async Task FallbackToDefaultDomain_WithEmptyOrMismatchedSourceName_ThrowsWebSqlPolicyException()
    {
        // SR15-43: If table is not catalogued under the requested data source, fallback to domain 'default'
        // must be rejected when SourceName is empty or does not match the active data source.
        var targetId = new TableIdentifier(DataSource, "crm", "leads");
        var defaultId = new TableIdentifier("default", "crm", "leads");

        var defaultMeta = new TableMetadata
        {
            Identifier = defaultId,
            Table = new Table
            {
                TableName = "leads",
                SchemaName = "crm",
                SourceName = "", // Empty source name
                SourceType = "PostgreSql"
            },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "name", DataType = "varchar" }
            ]
        };

        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Arg.Is<TableIdentifier>(t => t.Domain == DataSource), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TableMetadata?>(null));
        repo.GetTableMetadataAsync(Arg.Is<TableIdentifier>(t => t.Domain == "default"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TableMetadata?>(defaultMeta));

        var options = new GatewayOptions
        {
            WebSql = new WebSqlOptions
            {
                Enabled = true,
                DefaultDataSourceName = DataSource,
                AllowedDataSources = [DataSource]
            },
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    [DataSource] = new() { Provider = "PostgreSql", ConnectionString = "Host=localhost;Database=testdb" }
                }
            }
        };

        var resolution = Substitute.For<IConsentResolutionService>();
        resolution.ResolveAccess(Arg.Any<Sid>(), Arg.Any<IReadOnlySet<Sid>>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<TableIdentifier>(), Arg.Any<IReadOnlyList<Consent>>(), Arg.Any<DatabaseDialect>())
            .Returns(ci => TableAccessDecision.Allowed(
                ci.ArgAt<TableIdentifier>(3),
                new Dictionary<string, ColumnAccessLevel>
                {
                    ["id"] = ColumnAccessLevel.Clear,
                    ["name"] = ColumnAccessLevel.Clear
                },
                null,
                hasUnconstrainedColumnAllow: true));

        var factory = Substitute.For<ISqlConnectionFactory>();
        var conn = Substitute.For<DbConnection>();
        var cmd = Substitute.For<DbCommand>();
        var reader = Substitute.For<DbDataReader>();
        reader.ReadAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(false));
        reader.FieldCount.Returns(0);
        cmd.ExecuteReaderAsync(Arg.Any<CommandBehavior>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(reader));
        cmd.Parameters.Returns(Substitute.For<DbParameterCollection>());
        cmd.CreateParameter().Returns(Substitute.For<DbParameter>());
        conn.CreateCommand().Returns(cmd);
        factory.CreateOpenConnectionAsync(Arg.Any<DataSourceConnectionOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(conn));

        var service = new GovernedSqlExecutionService(
            Options.Create(options),
            policyEnforcement: null,
            consentResolution: resolution,
            tableRepository: repo,
            auditLogRepository: null,
            connectionFactory: factory,
            clientIpResolver: null,
            environment: null,
            logger: NullLogger<GovernedSqlExecutionService>.Instance,
            consentRepository: Substitute.For<IConsentRepository>(),
            secretProvider: null,
            sessionInitializer: Substitute.For<IDbSessionContextInitializer>(),
            mandatoryFilters: null);

        var request = new GovernedSqlQueryRequest("SELECT id, name FROM crm.leads", null, DataSource);

        var ex = await Should.ThrowAsync<WebSqlPolicyException>(async () =>
            await service.ExecuteQueryBufferedAsync(request, CreateUser(), new TenantId(Tenant)));
        ex.Message.ShouldContain("is denied or the table is not registered");
    }
}
