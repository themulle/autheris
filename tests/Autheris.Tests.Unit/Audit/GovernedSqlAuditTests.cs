namespace Autheris.Tests.Unit.Audit;

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Security;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class GovernedSqlAuditTests
{
    private const string Tenant = "tenant_audit";
    private const string DataSource = "pg_audit";

    private static ClaimsPrincipal CreateUser() =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.PrimarySid, "S-1-5-21-AUDIT-USER"),
                new Claim("tenant_id", Tenant),
                new Claim(ClaimTypes.Role, "Analyst")
            ],
            "Test"));

    private static TableMetadata CreateTable(TableIdentifier id) => new()
    {
        Identifier = id,
        Table = new Table
        {
            TableName = id.TableName,
            SchemaName = id.Schema,
            SourceName = DataSource,
            SourceType = "PostgreSql"
        },
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "int" },
            new TableColumn { ColumnName = "amount", DataType = "decimal" }
        ],
        ColumnMaskingRules = new Dictionary<string, MaskingRule>()
    };

    [Fact]
    public async Task DeniedSelectQuery_MustRecord_WebSqlQueryDeniedAudit()
    {
        var id = new TableIdentifier(DataSource, "public", "confidential");
        var meta = CreateTable(id);

        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TableMetadata?>(meta));

        var resolution = Substitute.For<IConsentResolutionService>();
        resolution.ResolveAccess(Arg.Any<Sid>(), Arg.Any<IReadOnlySet<Sid>>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<TableIdentifier>(), Arg.Any<IReadOnlyList<Consent>>(), Arg.Any<DatabaseDialect>())
            .Returns(TableAccessDecision.Denied(id, "Access to confidential table denied by governance policy"));

        var auditRepo = Substitute.For<IAuditLogRepository>();
        var options = new GatewayOptions
        {
            WebSql = new WebSqlOptions
            {
                Enabled = true,
                DefaultDataSourceName = DataSource,
                AllowedDataSources = [DataSource]
            }
        };

        var service = new GovernedSqlExecutionService(
            Options.Create(options),
            policyEnforcement: null,
            consentResolution: resolution,
            tableRepository: repo,
            auditLogRepository: auditRepo,
            connectionFactory: Substitute.For<ISqlConnectionFactory>(),
            clientIpResolver: null,
            environment: null,
            logger: NullLogger<GovernedSqlExecutionService>.Instance,
            consentRepository: Substitute.For<IConsentRepository>(),
            secretProvider: null,
            sessionInitializer: Substitute.For<IDbSessionContextInitializer>(),
            mandatoryFilters: null);

        var request = new GovernedSqlQueryRequest($"SELECT id, amount FROM {DataSource}.public.confidential", null, DataSource);

        await Should.ThrowAsync<SecurityException>(async () =>
        {
            await service.ExecuteQueryBufferedAsync(request, CreateUser(), new TenantId(Tenant));
        });

        await auditRepo.Received(1).RecordAuditEventAsync(
            Arg.Is<AuditLogEntry>(e =>
                e.EventType == AuditEventTypes.WebSqlQueryDenied &&
                e.Decision == "DENY" &&
                e.DetailsJson.Contains("POLICY_VIOLATION") &&
                e.TargetTable == DataSource),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FailedQueryExecution_MustRecord_QueryExecutionErrorAudit()
    {
        var id = new TableIdentifier(DataSource, "public", "orders");
        var meta = CreateTable(id);

        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TableMetadata?>(meta));

        var resolution = Substitute.For<IConsentResolutionService>();
        resolution.ResolveAccess(Arg.Any<Sid>(), Arg.Any<IReadOnlySet<Sid>>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<TableIdentifier>(), Arg.Any<IReadOnlyList<Consent>>(), Arg.Any<DatabaseDialect>())
            .Returns(TableAccessDecision.Allowed(
                id,
                new Dictionary<string, ColumnAccessLevel>
                {
                    ["id"] = ColumnAccessLevel.Clear,
                    ["amount"] = ColumnAccessLevel.Clear
                },
                null,
                hasUnconstrainedColumnAllow: true));

        var factory = Substitute.For<ISqlConnectionFactory>();
        var conn = Substitute.For<DbConnection>();
        var cmd = Substitute.For<DbCommand>();
        cmd.ExecuteReaderAsync(Arg.Any<CommandBehavior>(), Arg.Any<CancellationToken>())
            .Returns<Task<DbDataReader>>(_ => throw new InvalidOperationException("Database socket connection reset by peer"));
        cmd.Parameters.Returns(Substitute.For<DbParameterCollection>());
        cmd.CreateParameter().Returns(Substitute.For<DbParameter>());
        conn.CreateCommand().Returns(cmd);
        factory.CreateOpenConnectionAsync(Arg.Any<DataSourceConnectionOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(conn));

        var auditRepo = Substitute.For<IAuditLogRepository>();
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

        var service = new GovernedSqlExecutionService(
            Options.Create(options),
            policyEnforcement: null,
            consentResolution: resolution,
            tableRepository: repo,
            auditLogRepository: auditRepo,
            connectionFactory: factory,
            clientIpResolver: null,
            environment: null,
            logger: NullLogger<GovernedSqlExecutionService>.Instance,
            consentRepository: Substitute.For<IConsentRepository>(),
            secretProvider: null,
            sessionInitializer: Substitute.For<IDbSessionContextInitializer>(),
            mandatoryFilters: null);

        var request = new GovernedSqlQueryRequest($"SELECT id, amount FROM {DataSource}.public.orders", null, DataSource);

        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await service.ExecuteQueryBufferedAsync(request, CreateUser(), new TenantId(Tenant));
        });

        await auditRepo.Received(1).RecordAuditEventAsync(
            Arg.Is<AuditLogEntry>(e =>
                e.EventType == AuditEventTypes.QueryExecutionError &&
                e.Decision == "ERROR" &&
                e.DetailsJson.Contains(nameof(InvalidOperationException)) &&
                e.TargetTable == DataSource),
            Arg.Any<CancellationToken>());
    }
}
