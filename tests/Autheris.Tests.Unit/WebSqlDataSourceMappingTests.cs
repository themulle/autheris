namespace Autheris.Tests.Unit;

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

public sealed class WebSqlDataSourceMappingTests
{
    private const string Tenant = "tenant_finance";

    private static ClaimsPrincipal CreateUser() =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.PrimarySid, "S-1-5-21-USER-1"),
                new Claim("tenant_id", Tenant)
            ],
            "Test"));

    private static TableMetadata CreateTable(string table)
    {
        return new TableMetadata
        {
            Identifier = new TableIdentifier("finance", "dbo", table),
            Table = new Table { TableName = table, SchemaName = "dbo", SourceType = "PostgreSql" },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "amount", DataType = "decimal" }
            ],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>()
        };
    }

    [Fact]
    public async Task DataSourceMapping_WhenConfigured_ResolvesMappedConnectionAndExecutes()
    {
        var tableMeta = CreateTable("invoices");
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TableMetadata?>(tableMeta));

        var consents = Substitute.For<IConsentRepository>();
        consents.GetActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>(Array.Empty<Consent>()));

        var resolution = Substitute.For<IConsentResolutionService>();
        resolution.ResolveAccess(Arg.Any<Sid>(), Arg.Any<IReadOnlySet<Sid>>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<TableIdentifier>(), Arg.Any<IReadOnlyList<Consent>>(), Arg.Any<DatabaseDialect>())
            .Returns(ci =>
            {
                var tableId = ci.ArgAt<TableIdentifier>(3);
                var columnAccess = new Dictionary<string, ColumnAccessLevel>
                {
                    ["id"] = ColumnAccessLevel.Clear,
                    ["amount"] = ColumnAccessLevel.Clear
                };
                return TableAccessDecision.Allowed(tableId, columnAccess, null, hasUnconstrainedColumnAllow: false);
            });

        var options = new GatewayOptions
        {
            WebSql = new WebSqlOptions
            {
                Enabled = true,
                DefaultDataSourceName = "default",
                DataSourceMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["finance"] = "crmdb"
                }
            },
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["crmdb"] = new DataSourceConnectionOptions
                    {
                        Provider = "PostgreSql",
                        ConnectionString = "Host=localhost;Database=crm_actual"
                    }
                }
            }
        };

        var factory = Substitute.For<ISqlConnectionFactory>();
        var dbConn = Substitute.For<DbConnection>();
        var dbCmd = Substitute.For<DbCommand>();
        var dbReader = Substitute.For<DbDataReader>();

        dbReader.ReadAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(false));
        dbReader.FieldCount.Returns(0);
        dbCmd.ExecuteReaderAsync(Arg.Any<CommandBehavior>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(dbReader));
        dbConn.CreateCommand().Returns(dbCmd);

        factory.CreateOpenConnectionAsync(Arg.Any<DataSourceConnectionOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(dbConn));

        var sessionInit = Substitute.For<IDbSessionContextInitializer>();

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
            sessionInitializer: sessionInit);

        var request = new GovernedSqlQueryRequest("SELECT id, amount FROM finance.dbo.invoices", null, "finance");

        var result = await service.ExecuteQueryBufferedAsync(request, CreateUser(), new TenantId(Tenant));

        result.ShouldNotBeNull();
        await factory.Received(1).CreateOpenConnectionAsync(
            Arg.Is<DataSourceConnectionOptions>(o => o.ConnectionString == "Host=localhost;Database=crm_actual"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DataSourceMapping_WhenNotConfiguredAndNotAllowed_ThrowsWebSqlPolicyException()
    {
        var options = new GatewayOptions
        {
            WebSql = new WebSqlOptions
            {
                Enabled = true,
                DefaultDataSourceName = "default"
            }
        };

        var service = new GovernedSqlExecutionService(
            Options.Create(options),
            logger: NullLogger<GovernedSqlExecutionService>.Instance);

        var request = new GovernedSqlQueryRequest("SELECT 1", null, "unmapped_datasource");

        var ex = await Should.ThrowAsync<WebSqlPolicyException>(() =>
            service.ExecuteQueryBufferedAsync(request, CreateUser(), new TenantId(Tenant)));

        ex.Message.ShouldContain("not enabled for WebSQL");
    }
}
