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

/// <summary>
/// WebSQL findings v1.1.0, 2.1: a schema-qualified name (<c>schema.table</c>) is resolved in the domain of the requested
/// data source, not in the "default" domain.
/// </summary>
public sealed class WebSqlTwoPartNameDataSourceTests
{
    internal const string Tenant = "tenant_finance";
    private const string DataSource = "lwetem_prod";

    internal static readonly TableIdentifier CraneId = new(DataSource, "md", "crane");

    internal static ClaimsPrincipal CreateUser() =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.PrimarySid, "S-1-5-21-USER-1"),
                new Claim("tenant_id", Tenant)
            ],
            "Test"));

    private static TableMetadata CreateTable(TableIdentifier id) => new()
    {
        Identifier = id,
        Table = new Table { TableName = id.TableName, SchemaName = id.Schema, SourceType = "PostgreSql" },
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "int" },
            new TableColumn { ColumnName = "name", DataType = "text" }
        ],
        ColumnMaskingRules = new Dictionary<string, MaskingRule>()
    };

    private static (GovernedSqlExecutionService Service, ITableMetadataRepository Repository) CreateService(params TableIdentifier[] catalogued)
    {
        var (service, repo, _) = CreateServiceWithCommand(catalogued);
        return (service, repo);
    }

    internal static (GovernedSqlExecutionService Service, ITableMetadataRepository Repository, DbCommand Command) CreateServiceWithCommand(params TableIdentifier[] catalogued) =>
        CreateServiceWithCommand(null, null, catalogued);

    internal static (GovernedSqlExecutionService Service, ITableMetadataRepository Repository, DbCommand Command) CreateServiceWithCommand(
        IAuditLogRepository? audit,
        Autheris.Application.VirtualFilters.IMandatoryRowFilterResolver? mandatoryFilters,
        params TableIdentifier[] catalogued)
    {
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var requested = ci.ArgAt<TableIdentifier>(0);
                foreach (var id in catalogued)
                {
                    if (id.Equals(requested))
                        return Task.FromResult<TableMetadata?>(CreateTable(id));
                }

                return Task.FromResult<TableMetadata?>(null);
            });

        var consents = Substitute.For<IConsentRepository>();
        consents.GetActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>(Array.Empty<Consent>()));

        var resolution = Substitute.For<IConsentResolutionService>();
        resolution.ResolveAccess(Arg.Any<Sid>(), Arg.Any<IReadOnlySet<Sid>>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<TableIdentifier>(), Arg.Any<IReadOnlyList<Consent>>(), Arg.Any<DatabaseDialect>())
            .Returns(ci => TableAccessDecision.Allowed(
                ci.ArgAt<TableIdentifier>(3),
                new Dictionary<string, ColumnAccessLevel> { ["id"] = ColumnAccessLevel.Clear, ["name"] = ColumnAccessLevel.Clear },
                null,
                hasUnconstrainedColumnAllow: false));

        var options = new GatewayOptions
        {
            WebSql = new WebSqlOptions
            {
                Enabled = true,
                DefaultDataSourceName = "default",
                AllowedDataSources = ["default", DataSource]
            },
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["default"] = new() { Provider = "PostgreSql", ConnectionString = "Host=localhost;Database=default_db" },
                    [DataSource] = new() { Provider = "PostgreSql", ConnectionString = "Host=localhost;Database=lwetem" }
                }
            }
        };

        var reader = Substitute.For<DbDataReader>();
        reader.ReadAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(false));
        reader.FieldCount.Returns(0);
        var command = Substitute.For<DbCommand>();
        command.ExecuteReaderAsync(Arg.Any<CommandBehavior>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(reader));
        var connection = Substitute.For<DbConnection>();
        connection.CreateCommand().Returns(command);
        var factory = Substitute.For<ISqlConnectionFactory>();
        factory.CreateOpenConnectionAsync(Arg.Any<DataSourceConnectionOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(connection));

        var service = new GovernedSqlExecutionService(
            Options.Create(options),
            policyEnforcement: null,
            consentResolution: resolution,
            tableRepository: repo,
            auditLogRepository: audit,
            connectionFactory: factory,
            clientIpResolver: null,
            environment: null,
            logger: NullLogger<GovernedSqlExecutionService>.Instance,
            consentRepository: consents,
            secretProvider: null,
            sessionInitializer: Substitute.For<IDbSessionContextInitializer>(),
            mandatoryFilters: mandatoryFilters);
        return (service, repo, command);
    }

    [Fact]
    public async Task TwoPartName_WithDataSource_ResolvesInDataSourceDomain()
    {
        var (service, repo) = CreateService(CraneId);

        var result = await service.ExecuteQueryBufferedAsync(
            new GovernedSqlQueryRequest("SELECT id, name FROM md.crane", null, DataSource), CreateUser(), new TenantId(Tenant));

        result.ShouldNotBeNull();
        await repo.Received().GetTableMetadataAsync(CraneId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TwoPartName_WithoutDataSource_StaysInDefaultSourceAndIsDenied()
    {
        var (service, _) = CreateService(CraneId);

        var ex = await Should.ThrowAsync<WebSqlPolicyException>(() => service.ExecuteQueryBufferedAsync(
            new GovernedSqlQueryRequest("SELECT id, name FROM md.crane"), CreateUser(), new TenantId(Tenant)));

        ex.Message.ShouldContain("is denied or the table is not registered");
    }

    [Fact]
    public async Task ThreePartName_WithMatchingCatalog_IsUnchanged()
    {
        var (service, repo) = CreateService(CraneId);

        var result = await service.ExecuteQueryBufferedAsync(
            new GovernedSqlQueryRequest("SELECT id, name FROM lwetem_prod.md.crane"), CreateUser(), new TenantId(Tenant));

        result.ShouldNotBeNull();
        await repo.Received().GetTableMetadataAsync(CraneId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TwoPartName_WithDataSource_FallsBackToDefaultDomain()
    {
        var defaultId = new TableIdentifier("default", "md", "crane");
        var (service, repo) = CreateService(defaultId);

        var result = await service.ExecuteQueryBufferedAsync(
            new GovernedSqlQueryRequest("SELECT id, name FROM md.crane", null, DataSource), CreateUser(), new TenantId(Tenant));

        result.ShouldNotBeNull();
        await repo.Received().GetTableMetadataAsync(CraneId, Arg.Any<CancellationToken>());
        await repo.Received().GetTableMetadataAsync(defaultId, Arg.Any<CancellationToken>());
    }
}
