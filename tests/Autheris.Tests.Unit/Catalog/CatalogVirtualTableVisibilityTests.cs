namespace Autheris.Tests.Unit.Catalog;

using System.Collections;
using System.Security.Claims;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Dbt.Services;
using Autheris.Application.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Services;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.GraphQL.Types;
using Autheris.Tests.Unit.TestSupport;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// #112 A1-13 (SEC-112-15, -22..-25): in Multi a semantic virtual table is visible only to the tenant whose <c>t_&lt;hash&gt;</c> schema it
/// lives in. The rule sits in the shared visibility engine, runs before consent evaluation and is not re-admitted by consents, tiers or
/// <c>SharedTables</c>.
/// </summary>
[Trait("Category", "Security")]
public sealed class CatalogVirtualTableVisibilityTests
{
    private static readonly TenantId TenantA = Wp0.TenantA;
    private static readonly TenantId TenantB = Wp0.TenantB;

    private static TableMetadata VirtualTable(TenantId tenant, string name = "orders", DatasetVisibility visibility = DatasetVisibility.Hidden)
    {
        var id = SemanticVirtualTables.IdentifierFor(tenant, name);
        return new TableMetadata
        {
            Identifier = id,
            Table = new Table
            {
                SourceName = id.Domain,
                SchemaName = id.Schema,
                TableName = name,
                DocumentationSource = SemanticVirtualTables.SourceMarker,
                IsActive = true,
                Visibility = visibility,
                DataSourceType = DataSourceType.Sql
            },
            Columns = [new TableColumn { ColumnName = "id", DataType = "varchar" }, new TableColumn { ColumnName = "secret_col", DataType = "varchar" }]
        };
    }

    private static TableMetadata Malformed(string schema, string name = "m") =>
        VirtualTable(TenantA, name) with { Identifier = new TableIdentifier("semantic", schema, name) };

    private static IReadOnlyList<TableMetadata> Real() => [Wp0.Table("sales", "real_table")];

    private static async Task<List<string>> VisibleAsync(
        ClaimsPrincipal user, TenantId tenant, IEnumerable<TableMetadata> tables, IConsentRepository consents,
        TenancyMode mode = TenancyMode.Multi, IEnumerable<string>? shared = null, CatalogSurface surface = CatalogSurface.DataAccess)
    {
        var visible = await CatalogVisibility.VisibleTablesAsync(
            tables.ToList(), user, tenant, consents, contract: null, mode.Owner(), shared, surface);
        return visible.Select(t => t.Identifier.ToString()).Order().ToList();
    }

    private static string Id(TableMetadata t) => t.Identifier.ToString();

    [Fact]
    public async Task OwnVirtualTable_WithConsent_IsVisible_ForeignIsNot()
    {
        var own = VirtualTable(TenantA);
        var foreign = VirtualTable(TenantB);
        var consents = Wp0.Consents(Wp0.Allow(own.Identifier, "S-USER", TenantA, "id"));

        var visible = await VisibleAsync(Wp0.User("S-USER", "tenant-a"), TenantA, [own, foreign], consents);

        visible.ShouldBe([Id(own)]);
    }

    [Fact]
    public async Task VirtualTable_ForeignTenant_ConsentDoesNotReadmit()
    {
        var foreign = VirtualTable(TenantB);
        // The consent row exists (a stray or hostile grant in tenant A's name on tenant B's virtual table).
        var consents = Wp0.Consents(Wp0.Allow(foreign.Identifier, "S-USER", TenantA, "id"));

        (await VisibleAsync(Wp0.User("S-USER", "tenant-a"), TenantA, [foreign], consents)).ShouldBeEmpty();
    }

    [Fact]
    public async Task VirtualTable_ForeignTenant_SharedTablesDoesNotReadmit()
    {
        var foreign = VirtualTable(TenantB, visibility: DatasetVisibility.Full);
        var shared = new[] { foreign.Identifier.ToString() };

        // Discovery tier of a subject and the consent-derived view of a claimed GovernanceAdmin both honour SharedTables for real tables.
        (await VisibleAsync(Wp0.User("S-USER", "tenant-a"), TenantA, [foreign], Wp0.Consents(), shared: shared, surface: CatalogSurface.Discovery)).ShouldBeEmpty();
        (await VisibleAsync(Wp0.User("S-ADM", "tenant-a", ["GovernanceAdmin"]), TenantA, [foreign], Wp0.Consents(), shared: shared)).ShouldBeEmpty();
    }

    [Fact]
    public async Task VirtualTable_ForeignTenant_AdminConsentTablesDoNotReadmit()
    {
        var foreign = VirtualTable(TenantB);
        var consents = Wp0.Consents(Wp0.Allow(foreign.Identifier, "S-OTHER", TenantA, "id"));

        (await VisibleAsync(Wp0.User("S-ADM", "tenant-a", ["GovernanceAdmin"]), TenantA, [foreign], consents)).ShouldBeEmpty();
    }

    [Fact]
    public async Task VirtualTable_ClaimlessMulti_Hidden()
    {
        var own = VirtualTable(TenantA);
        var legacyHash = VirtualTable(TenantId.LegacySingleTenant);
        var consents = Wp0.Consents(Wp0.Allow(own.Identifier, "S-USER", TenantId.LegacySingleTenant, "id"), Wp0.Allow(legacyHash.Identifier, "S-USER", TenantId.LegacySingleTenant, "id"));

        (await VisibleAsync(Wp0.User("S-USER", null), TenantId.LegacySingleTenant, [own, legacyHash], consents)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("t_xyz")]
    [InlineData("t_0123456789abcde")]
    [InlineData("T_0123456789ABCDEF")]
    [InlineData("semantic")]
    public async Task VirtualTable_MalformedSchema_Dropped(string schema)
    {
        var malformed = Malformed(schema);
        var consents = Wp0.Consents(Wp0.Allow(malformed.Identifier, "S-USER", TenantA, "id"));

        (await VisibleAsync(Wp0.User("S-USER", "tenant-a"), TenantA, [malformed], consents)).ShouldBeEmpty();
    }

    [Fact]
    public async Task GlobalAdmin_InMulti_SeesEveryVirtualTable()
    {
        var a = VirtualTable(TenantA);
        var b = VirtualTable(TenantB);

        (await VisibleAsync(Wp0.User("S-ROOT", "tenant-a", ["ClusterAdmin"]), TenantA, [a, b], Wp0.Consents())).ShouldBe([Id(a), Id(b)]);
    }

    [Fact]
    public async Task DefaultTenant_OwnVirtualTable_IsVisible_ByTenantSchema()
    {
        var single = VirtualTable(TenantId.LegacySingleTenant);
        var consents = Wp0.Consents(Wp0.Allow(single.Identifier, "S-USER", TenantId.LegacySingleTenant, "id"));

        (await VisibleAsync(Wp0.User("S-USER", null), TenantId.LegacySingleTenant, [single], consents, TenancyMode.Single)).ShouldBe([Id(single)]);
    }

    [Fact]
    public async Task UserSchemaNamedLikeTenantHash_IsAnOrdinaryTable_ForItsOwnerTenant()
    {
        // A real table of another domain whose schema looks like a tenant hash is not a virtual table: normal consent rules apply.
        var lookalike = Wp0.Table("sales", "orders") with
        {
            Identifier = new TableIdentifier("sales", SemanticVirtualTables.TenantSchema(TenantB), "orders")
        };
        var consents = Wp0.Consents(Wp0.Allow(lookalike.Identifier, "S-USER", TenantA, "id"));

        (await VisibleAsync(Wp0.User("S-USER", "tenant-a"), TenantA, [lookalike], consents)).ShouldBe([Id(lookalike)]);
    }

    [Fact]
    public async Task VirtualTable_DropRunsBeforeConsentEvaluation()
    {
        // A foreign virtual table whose masking rules throw when enumerated: consent evaluation of an allowed table enumerates them.
        var foreign = VirtualTable(TenantB) with { ColumnMaskingRules = new ThrowingMaskingRules() };
        var consents = Wp0.Consents(Wp0.Allow(foreign.Identifier, "S-USER", TenantA, "id"));

        (await VisibleAsync(Wp0.User("S-USER", "tenant-a"), TenantA, [foreign], consents)).ShouldBeEmpty();
    }

    [Fact]
    public void VirtualTableRule_AppliedInFilterForSubjectAndFilterToTenant()
    {
        var foreign = VirtualTable(TenantB);
        var own = VirtualTable(TenantA);
        var allConsents = new[] { Wp0.Allow(foreign.Identifier, "S-USER", TenantA, "id"), Wp0.Allow(own.Identifier, "S-USER", TenantA, "id") };

        var subject = CatalogVisibility.FilterForSubject([foreign, own], allConsents, TenantA);
        var tenant = CatalogVisibility.FilterToTenant([foreign, own], TenantA, [foreign.Identifier, own.Identifier], [foreign.Identifier.ToString()], TenancyMode.Multi.Owner());

        subject.Select(Id).ShouldBe([Id(own)]);
        tenant.Select(Id).ShouldBe([Id(own)]);
    }

    private sealed class ThrowingMaskingRules : IReadOnlyDictionary<string, MaskingRule>
    {
        public MaskingRule this[string key] => throw new InvalidOperationException("consent evaluation reached a foreign virtual table");
        public IEnumerable<string> Keys => throw new InvalidOperationException("consent evaluation reached a foreign virtual table");
        public IEnumerable<MaskingRule> Values => throw new InvalidOperationException("consent evaluation reached a foreign virtual table");
        public int Count => throw new InvalidOperationException("consent evaluation reached a foreign virtual table");
        public bool ContainsKey(string key) => throw new InvalidOperationException("consent evaluation reached a foreign virtual table");
        public bool TryGetValue(string key, out MaskingRule value) => throw new InvalidOperationException("consent evaluation reached a foreign virtual table");
        public IEnumerator<KeyValuePair<string, MaskingRule>> GetEnumerator() => throw new InvalidOperationException("consent evaluation reached a foreign virtual table");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    // ---------------------------------------------------------------- information_schema and metadata listing

    [Fact]
    public async Task InformationSchema_Schemata_HidesForeignTenantSchema()
    {
        var own = VirtualTable(TenantA);
        var foreign = VirtualTable(TenantB);
        var repo = Wp0.Tables(own, foreign, Wp0.Table("sales", "real_table"));
        var consents = Wp0.Consents(
            Wp0.Allow(own.Identifier, "S-USER", TenantA, "id"),
            Wp0.Allow(foreign.Identifier, "S-USER", TenantA, "id"));
        var user = Wp0.User("S-USER", "tenant-a");
        var options = Wp0.Options(TenancyMode.Multi, foreign.Identifier.ToString());

        foreach (var sql in new[]
        {
            "SELECT catalog_name, schema_name FROM information_schema.schemata",
            "SELECT table_catalog, table_schema, table_name, table_type FROM information_schema.tables",
            "SELECT table_schema, column_name FROM information_schema.columns",
            "SHOW SCHEMAS FROM semantic",
            "SHOW CATALOGS"
        })
        {
            var result = await TrinoMetadataQueryHandler.TryExecuteAsync(sql, user, TenantA, repo, consents, options);

            result.ShouldNotBeNull(sql);
            var text = string.Join('|', result.Rows.SelectMany(r => r.Values.Select(v => v?.ToString())));
            text.ShouldNotContain(SemanticVirtualTables.TenantSchema(TenantB), customMessage: sql);
        }

        var tables = await TrinoMetadataQueryHandler.TryExecuteAsync("SELECT table_schema, table_name, table_type FROM information_schema.tables", user, TenantA, repo, consents, options);
        tables!.Rows.Select(r => (string?)r["table_schema"]).ShouldContain(SemanticVirtualTables.TenantSchema(TenantA));
        tables.Rows.Single(r => (string?)r["table_schema"] == SemanticVirtualTables.TenantSchema(TenantA))["table_type"].ShouldBe("VIEW");
    }

    [Fact]
    public async Task InformationSchema_TableType_ViewKeysOnTheSharedHelper_NotOnASchemaString()
    {
        var real = Wp0.Table("sales", "orders") with
        {
            Identifier = new TableIdentifier("sales", "semantic", "orders")
        };
        var repo = Wp0.Tables(real);
        var consents = Wp0.Consents(Wp0.Allow(real.Identifier, "S-USER", TenantA, "id"));

        var result = await TrinoMetadataQueryHandler.TryExecuteAsync(
            "SELECT table_schema, table_name, table_type FROM information_schema.tables", Wp0.User("S-USER", "tenant-a"), TenantA, repo, consents, Wp0.Options(TenancyMode.Multi));

        result!.Rows.Single()["table_type"].ShouldBe("BASE TABLE");
    }

    [Fact]
    public async Task DescribeOtherTenantVirtualTable_SameErrorAsMissing()
    {
        var foreign = VirtualTable(TenantB);
        var consents = Wp0.Consents(Wp0.Allow(foreign.Identifier, "S-USER", TenantA, "id"));
        var repo = Wp0.Tables(foreign);
        var options = Wp0.Options(TenancyMode.Multi, foreign.Identifier.ToString());
        var service = CatalogVisibilityTestFactory.Create(repo, consents, options);
        var mcp = new McpDatasetCatalog(
            repo, consents, Substitute.For<IGatewayExecutionService>(), Substitute.For<IAuditLogRepository>(),
            options: Options.Create(options), visibilityService: service);
        var user = Wp0.User("S-USER", "tenant-a");

        var foreignError = await Should.ThrowAsync<Exception>(() => mcp.DescribeDatasetAsync(user, foreign.Identifier.ToString()));
        var missingError = await Should.ThrowAsync<Exception>(() => mcp.DescribeDatasetAsync(user, "semantic." + SemanticVirtualTables.TenantSchema(TenantB) + ".nothing"));

        foreignError.GetType().ShouldBe(missingError.GetType());
        foreignError.Message.Replace("orders", "nothing").ShouldBe(missingError.Message);
        (await service.FindVisibleAsync(CatalogVisibilityTestFactory.Caller(user), foreign.Identifier)).ShouldBeNull();
    }

    // ---------------------------------------------------------------- parity across REST, MCP and GraphQL

    public enum Caller { OwnerSubject, ForeignSubject, Claimless, TenantAdminWithClaim, GlobalAdmin }

    private static ClaimsPrincipal UserOf(Caller c) => c switch
    {
        Caller.OwnerSubject => Wp0.User("S-USER", "tenant-a"),
        Caller.ForeignSubject => Wp0.User("S-USER", "tenant-b"),
        Caller.Claimless => Wp0.User("S-USER", null),
        Caller.TenantAdminWithClaim => Wp0.User("S-USER", "tenant-a", ["GovernanceAdmin"]),
        _ => Wp0.User("S-USER", "tenant-a", ["ClusterAdmin"])
    };

    public static TheoryData<Caller> Callers() => new(Enum.GetValues<Caller>());

    [Theory]
    [MemberData(nameof(Callers))]
    public async Task CatalogVisibilityParity_VirtualTableRows_AllSurfaces(Caller caller)
    {
        var a = VirtualTable(TenantA);
        var b = VirtualTable(TenantB);
        var user = UserOf(caller);
        var tenant = user.FindFirst("tenant_id") is { } c ? new TenantId(c.Value) : TenantId.LegacySingleTenant;
        // Every caller holds an Allow consent on both virtual tables in its own tenant: only the schema rule can separate them.
        var consents = Wp0.Consents(
            Wp0.Allow(a.Identifier, "S-USER", tenant, "id"), Wp0.Allow(b.Identifier, "S-USER", tenant, "id"),
            Wp0.Allow(a.Identifier, "S-USER", TenantA, "id"), Wp0.Allow(b.Identifier, "S-USER", TenantB, "id"));
        var tables = Wp0.Tables(a, b);
        var options = Wp0.Options(TenancyMode.Multi);
        var service = CatalogVisibilityTestFactory.Create(tables, consents, options);

        var rest = new CatalogDiscoveryService(tables, Substitute.For<IAuditLogRepository>(), Options.Create(options), service);
        var restNames = (await rest.ListDatasetsAsync(Wp0.Context(user))).Select(s => $"{s.Domain}.{s.Schema}.{s.Table}").Order().ToList();
        var mcp = new McpDatasetCatalog(
            tables, consents, Substitute.For<IGatewayExecutionService>(), Substitute.For<IAuditLogRepository>(),
            options: Options.Create(options), visibilityService: service);
        var mcpNames = (await mcp.ListDatasetsAsync(user)).Datasets.Select(d => d.Id).Order().ToList();
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(new DefaultHttpContext { User = user });
        var graphQl = (await new Query().GetCatalogAsync(null, null, null, null, null, tables, consents, null, null, accessor, Options.Create(options), service))
            .Select(t => $"{t.Domain}.{t.Schema}.{t.TableName}").Order().ToList();

        var expected = caller switch
        {
            Caller.OwnerSubject or Caller.TenantAdminWithClaim => new List<string> { Id(a) },
            Caller.ForeignSubject => [Id(b)],
            Caller.Claimless => [],
            _ => [Id(a), Id(b)]
        };
        restNames.ShouldBe(expected, "REST");
        mcpNames.ShouldBe(expected, "MCP");
        graphQl.ShouldBe(expected, "GraphQL");
    }
}
