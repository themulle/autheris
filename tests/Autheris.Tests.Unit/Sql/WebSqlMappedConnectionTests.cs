namespace Autheris.Tests.Unit.Sql;

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
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
/// #668 through the governed pipeline: the dbt table of database <c>finance</c> is catalogued as <c>finance.dbo.invoices</c> with
/// <c>SourceName = crmdb</c> (the connection key); <c>WebSql:DataSourceMappings:finance = crmdb</c> makes the logical name reach it.
/// </summary>
public sealed class WebSqlMappedConnectionTests
{
    private const string Tenant = "tenant_finance";
    private const string Secret = "crmdb_secret_key";

    private static ClaimsPrincipal User() =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-USER-1"), new Claim("tenant_id", Tenant)], "Test"));

    private static TableMetadata Table(string domain, string table, string sourceName) => new()
    {
        Identifier = new TableIdentifier(domain, "dbo", table),
        Table = new Table { TableName = table, SchemaName = "dbo", SourceType = "PostgreSql", SourceName = sourceName },
        Columns = [new TableColumn { ColumnName = "id", DataType = "int" }],
        ColumnMaskingRules = new Dictionary<string, MaskingRule>()
    };

    private sealed record Rig(IGovernedSqlExecutionService Service, ISqlConnectionFactory Factory);

    private static Rig Build(Dictionary<string, string> mappings, string[] connections, params TableMetadata[] tables) =>
        Build(mappings, connections, "PostgreSql", tables);

    private static Rig Build(Dictionary<string, string> mappings, string[] connections, string connectionProvider, params TableMetadata[] tables)
    {
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<TableMetadata?>(tables.FirstOrDefault(t => t.Identifier == call.Arg<TableIdentifier>())));

        var consents = Substitute.For<IConsentRepository>();
        consents.GetActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>(Array.Empty<Consent>()));
        var resolution = Substitute.For<IConsentResolutionService>();
        resolution.ResolveAccess(Arg.Any<Sid>(), Arg.Any<IReadOnlySet<Sid>>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<TableIdentifier>(), Arg.Any<IReadOnlyList<Consent>>(), Arg.Any<DatabaseDialect>())
            .Returns(ci => TableAccessDecision.Allowed(ci.ArgAt<TableIdentifier>(3),
                new Dictionary<string, ColumnAccessLevel> { ["id"] = ColumnAccessLevel.Clear }, null, hasUnconstrainedColumnAllow: false));

        var options = new GatewayOptions { WebSql = new WebSqlOptions { Enabled = true, DefaultDataSourceName = "default" } };
        foreach (var (key, value) in mappings)
        {
            options.WebSql.DataSourceMappings[key] = value;
        }

        foreach (var name in connections)
        {
            options.DataSources.Connections[name] = new DataSourceConnectionOptions { Provider = connectionProvider, ConnectionString = $"Host=localhost;Database={name}" };
        }

        var factory = Substitute.For<ISqlConnectionFactory>();
        var dbConn = Substitute.For<DbConnection>();
        var dbCmd = Substitute.For<DbCommand>();
        dbCmd.WireParameters();
        var dbReader = Substitute.For<DbDataReader>();
        dbReader.ReadAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(false));
        dbReader.FieldCount.Returns(0);
        dbCmd.ExecuteReaderAsync(Arg.Any<CommandBehavior>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(dbReader));
        dbConn.CreateCommand().Returns(dbCmd);
        factory.CreateOpenConnectionAsync(Arg.Any<DataSourceConnectionOptions>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(dbConn));

        var service = global::Autheris.Tests.Unit.TestDoubles.GovernedSqlExecutionServiceFactory.Create(
            Options.Create(options), global::Autheris.Tests.Unit.TestDoubles.NullAuditLogRepository.Instance, executionGuard: GovernedTestSupport.Guard,
            consentResolution: resolution,
            tableRepository: repo, policyFactory: GovernedTestSupport.PolicyFactory(repo), typedPolicies: GovernedTestSupport.TypedPolicies(),
            connectionFactory: factory, clientIpResolver: null, environment: null,
            logger: NullLogger<GovernedSqlExecutionService>.Instance, consentRepository: consents, secretProvider: null,
            sessionInitializer: Substitute.For<IDbSessionContextInitializer>());
        return new Rig(service, factory);
    }

    private static Task<GovernedSqlResult> Query(Rig rig, string sql, string dataSource) =>
        rig.Service.ExecuteQueryBufferedAsync(new GovernedSqlQueryRequest(sql, null, dataSource), User(), new TenantId(Tenant));

    [Fact]
    public async Task Query_MappedLogicalName_ExecutesOnTheMappedConnection()
    {
        // a catalog entry does not record its connection: its source is the logical name, the connection follows from the mapping
        var rig = Build(new() { ["finance"] = "crmdb" }, ["crmdb"], Table("finance", "invoices", "finance"));

        await Query(rig, "SELECT id FROM finance.dbo.invoices", "finance");

        await rig.Factory.Received(1).CreateOpenConnectionAsync(
            Arg.Is<DataSourceConnectionOptions>(o => o.ConnectionString == "Host=localhost;Database=crmdb"), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Query_EntryOfOtherLogicalName_NotAdmittedThroughMapping()
    {
        var rig = Build(new() { ["finance"] = Secret }, [Secret], Table("finance", "invoices", "sales"));

        var ex = await Should.ThrowAsync<WebSqlTableDeniedException>(() => Query(rig, "SELECT id FROM finance.dbo.invoices", "finance"));

        ex.Message.ShouldNotContain(Secret);
        await rig.Factory.DidNotReceiveWithAnyArgs().CreateOpenConnectionAsync(default!, default);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Denial_Response_ContainsNoConnectionKey()
    {
        // the mapped connection has another dialect than the catalog entry
        var rig = Build(new() { ["finance"] = Secret }, [Secret], "Sqlite", Table("finance", "invoices", "finance"));

        var ex = await Should.ThrowAsync<WebSqlPolicyException>(() => Query(rig, "SELECT id FROM finance.dbo.invoices", "finance"));

        ex.Message.ShouldNotContain(Secret);
        await rig.Factory.DidNotReceiveWithAnyArgs().CreateOpenConnectionAsync(default!, default);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task TwoLogicalNames_OneConnection_EachSeesOnlyOwnDomainTables()
    {
        var rig = Build(new() { ["finance"] = "crmdb", ["sales"] = "crmdb" }, ["crmdb"],
            Table("finance", "invoices", "finance"), Table("sales", "orders", "sales"));

        await Query(rig, "SELECT id FROM finance.dbo.invoices", "finance");
        await Query(rig, "SELECT id FROM sales.dbo.orders", "sales");

        await Should.ThrowAsync<WebSqlPolicyException>(() => Query(rig, "SELECT id FROM sales.dbo.orders", "finance"));
        await Should.ThrowAsync<WebSqlPolicyException>(() => Query(rig, "SELECT id FROM finance.dbo.invoices", "sales"));
        await Should.ThrowAsync<WebSqlTableDeniedException>(() => Query(rig, "SELECT id FROM dbo.orders", "finance"));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("")]
    [Trait("Category", "Security")]
    public async Task Query_MappingWithoutConnection_Denied(string target)
    {
        var rig = Build(new() { ["finance"] = target }, ["finance"], Table("finance", "invoices", "finance"));

        await Should.ThrowAsync<WebSqlPolicyException>(() => Query(rig, "SELECT id FROM finance.dbo.invoices", "finance"));
        await rig.Factory.DidNotReceiveWithAnyArgs().CreateOpenConnectionAsync(default!, default);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Query_ChainedMapping_Denied()
    {
        var rig = Build(new() { ["finance"] = "crmdb", ["crmdb"] = "other" }, ["crmdb", "other"], Table("finance", "invoices", "finance"));

        await Should.ThrowAsync<WebSqlPolicyException>(() => Query(rig, "SELECT id FROM finance.dbo.invoices", "finance"));
        await rig.Factory.DidNotReceiveWithAnyArgs().CreateOpenConnectionAsync(default!, default);
    }
}
