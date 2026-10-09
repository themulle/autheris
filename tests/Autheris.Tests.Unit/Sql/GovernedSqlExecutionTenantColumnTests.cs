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

public sealed class GovernedSqlExecutionTenantColumnTests
{
    private const string Tenant = "tenant_test";
    private const string DataSource = "pg_source";

    private static ClaimsPrincipal CreateUser() =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.PrimarySid, "S-1-5-21-USER-1"),
                new Claim("tenant_id", Tenant),
                new Claim(ClaimTypes.Role, "Admin")
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
            new TableColumn { ColumnName = "tenant_id", DataType = "varchar" },
            new TableColumn { ColumnName = "amount", DataType = "decimal" }
        ],
        ColumnMaskingRules = new Dictionary<string, MaskingRule>()
    };

    private static (GovernedSqlExecutionService Service, DbCommand Command) CreateService(TableMetadata tableMeta)
    {
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<TableMetadata?>(tableMeta));

        var consents = Substitute.For<IConsentRepository>();
        consents.GetActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>(Array.Empty<Consent>()));

        var resolution = Substitute.For<IConsentResolutionService>();
        resolution.ResolveAccess(Arg.Any<Sid>(), Arg.Any<IReadOnlySet<Sid>>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<TableIdentifier>(), Arg.Any<IReadOnlyList<Consent>>(), Arg.Any<DatabaseDialect>())
            .Returns(ci => TableAccessDecision.Allowed(
                tableMeta.Identifier,
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

        return (service, cmd);
    }

    [Fact]
    public async Task ExecuteQuery_WithThreePartName_AppliesTenantColumnRls()
    {
        var id = new TableIdentifier(DataSource, "public", "invoices");
        var meta = CreateTable(id);
        var (service, cmd) = CreateService(meta);

        // Three-part query
        await service.ExecuteQueryBufferedAsync(
            new GovernedSqlQueryRequest($"SELECT id, amount FROM {DataSource}.public.invoices", null, DataSource),
            CreateUser(),
            new TenantId(Tenant));

        cmd.CommandText.ShouldNotBeNull();
        cmd.CommandText.ShouldContain("tenant_id = 'tenant_test'");
    }

    [Fact]
    public async Task ExecuteQuery_WithTwoPartName_AppliesTenantColumnRls()
    {
        var id = new TableIdentifier(DataSource, "public", "invoices");
        var meta = CreateTable(id);
        var (service, cmd) = CreateService(meta);

        // Two-part query
        await service.ExecuteQueryBufferedAsync(
            new GovernedSqlQueryRequest("SELECT id, amount FROM public.invoices", null, DataSource),
            CreateUser(),
            new TenantId(Tenant));

        cmd.CommandText.ShouldNotBeNull();
        cmd.CommandText.ShouldContain("tenant_id = 'tenant_test'");
    }
}
