using System.Collections.Immutable;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Sql.Catalog;
using Autheris.Application.Sql.Masking;
using Autheris.Application.Sql.Policy;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Data.Sqlite;
using NSubstitute;
using Shouldly;
using System.Security;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using Xunit;

namespace Autheris.Tests.Unit.Sql.Policy;

/// <summary>WP-X0l: the single assembled typed policy per statement, consumed by <c>ISqlEngine.Compile</c> (SEC-ADG-02, SEC-X1-04).</summary>
public sealed class GovernancePolicyFactoryTests
{
    private static readonly TableIdentifier OrdersId = new("d", "main", "orders");
    private static readonly TableIdentifier InvoicesId = new("d", "main", "invoices");
    private static readonly TableIdentity Orders = new("main", "orders");
    private static readonly TableIdentity Invoices = new("main", "invoices");
    private static readonly PolicyPredicateRenderer Renderer = new();
    private static readonly RowPolicyFactory RowPolicies = new(Renderer);
    private readonly FastSqlEngine _engine = new();

    private static TableCatalogEntry Entry(TableIdentity id, string tenantType = "TEXT") => new(id, ImmutableArray.Create(
        new CatalogColumn("id", "INTEGER"), new CatalogColumn("tenant_id", tenantType), new CatalogColumn("region", "TEXT"),
        new CatalogColumn("email", "TEXT")), "tenant_id", 1);

    private static ITableCatalog Catalog(params TableCatalogEntry[] entries) => new InMemoryTableCatalog(entries, "main");

    private static GovernancePolicyFactory Factory(
        ITableCatalog catalog, out IGovernedCatalogProvider provider, GatewayOptions? options = null, Func<TableIdentifier, TableMetadata?>? metadata = null)
    {
        provider = Substitute.For<IGovernedCatalogProvider>();
        provider.GetCatalogAsync(Arg.Any<string>(), Arg.Any<IReadOnlyCollection<TableIdentifier>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ITableCatalog>(catalog));
        var tables = Substitute.For<ITableMetadataRepository>();
        tables.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(metadata is null ? DependentMetadata(call.Arg<TableIdentifier>(), withTenant: true) : metadata(call.Arg<TableIdentifier>())));
        return new GovernancePolicyFactory(provider, tables, Microsoft.Extensions.Options.Options.Create(options ?? new GatewayOptions()));
    }

    private static TableMetadata DependentMetadata(TableIdentifier id, bool withTenant, bool active = true, string sourceName = "ds") => new()
    {
        Table = new Table { SourceName = sourceName, SchemaName = id.Schema, TableName = id.TableName, IsActive = active },
        Identifier = id,
        Columns = withTenant
            ? [new TableColumn { ColumnName = "id" }, new TableColumn { ColumnName = "tenant_id" }]
            : [new TableColumn { ColumnName = "id" }]
    };

    private static IRowPolicy RegionPolicy(string region = "EU", TargetSqlDialect dialect = TargetSqlDialect.Sqlite)
    {
        var namer = new PolicyParameterNamer("__gql_rls_");
        var parameter = namer.Add(new PolicyValue(region, SqlParameterType.String));
        var expression = new BinaryExpression(PolicyParameterNamer.Column("region"), BinaryOperator.Equal, parameter);
        return new PolicyPredicateRowPolicy(PolicyPredicate.Create(expression, namer.Values), dialect, Renderer);
    }

    private static TableAccessDecision Allowed(TableIdentifier table, IRowPolicy? rowPolicy = null, ColumnAccessLevel email = ColumnAccessLevel.Clear, IColumnMaskPolicy? masks = null)
    {
        var decision = TableAccessDecision.Allowed(table,
            new Dictionary<string, ColumnAccessLevel> { ["email"] = email }, hasUnconstrainedColumnAllow: true);
        if (rowPolicy != null)
        {
            decision = decision.WithAdditionalRowPolicy(rowPolicy);
        }

        return masks is null ? decision : decision with { ColumnMasks = masks };
    }

    private static ColumnMaskPolicy EmailMask() => new(new Dictionary<string, MaskSpec>
    {
        ["email"] = SqlDataMaskingProvider.BuildUnkeyedMaskSpec(null, "email", DatabaseDialect.Sqlite)
    });

    private static GovernedPolicyInput Input(
        IReadOnlyDictionary<TableIdentity, TableAccessDecision> decisions, IEnumerable<TableIdentity>? withoutRls = null, string tenant = "acme") =>
        new("ds", new TenantId(tenant), decisions, (withoutRls ?? []).ToHashSet(), GovernedSubqueryStrategy.Correlated);

    private static CompileRequest Request(GovernancePolicy policy, bool dml = false) =>
        CompileRequestFactory.Create(policy, TargetSqlDialect.Sqlite, new WebSqlOptions(), dml, 100, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    private List<object?[]> Run(CompileRequest request, string sql)
    {
        var compiled = _engine.Compile(sql.AsMemory(), request, CancellationToken.None);
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using (var setup = connection.CreateCommand())
        {
            setup.CommandText = """
                CREATE TABLE orders (id INTEGER, tenant_id TEXT, region TEXT, email TEXT);
                CREATE TABLE invoices (id INTEGER, tenant_id TEXT, region TEXT, email TEXT);
                INSERT INTO orders VALUES (1, 'acme', 'EU', 'a@x'), (2, 'acme', 'US', 'b@x'), (3, 'other', 'EU', 'c@x'), (4, 'ACME', 'EU', 'd@x');
                INSERT INTO invoices VALUES (1, 'acme', 'EU', 'z@x');
                """;
            setup.ExecuteNonQuery();
        }

        using var command = connection.CreateCommand();
        new SqliteCompiledSqlBinder().Bind(command, compiled, new Dictionary<string, object?>());
        var rows = new List<object?[]>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var row = new object[reader.FieldCount];
            reader.GetValues(row);
            rows.Add(row.Select(v => v is DBNull ? null : v).ToArray());
        }

        return rows;
    }

    private async Task<CompileRequest> AssembleAsync(
        IReadOnlyDictionary<TableIdentity, TableAccessDecision> decisions, IEnumerable<TableIdentity>? withoutRls = null, params TableCatalogEntry[] entries)
    {
        var factory = Factory(Catalog(entries.Length == 0 ? [Entry(Orders), Entry(Invoices)] : entries), out _);
        return Request(await factory.CreateAsync(Input(decisions, withoutRls), CancellationToken.None));
    }

    private static List<int> Ids(List<object?[]> rows) => rows.Select(r => Convert.ToInt32(r[0])).OrderBy(x => x).ToList();

    [Fact]
    public async Task TypedRowPolicy_FiltersRows_AndIsBound()
    {
        var request = await AssembleAsync(new Dictionary<TableIdentity, TableAccessDecision> { [Orders] = Allowed(OrdersId, RegionPolicy("EU")) });

        var compiled = _engine.Compile("SELECT id FROM orders".AsMemory(), request, CancellationToken.None);

        compiled.Sql.ShouldNotContain("'EU'");
        compiled.Parameters.Select(p => p.Value).ShouldContain("EU");
        Ids(Run(request, "SELECT id FROM orders")).ShouldBe(new List<int> { 1 });
    }

    [Fact]
    public async Task MissingDecision_DeniesTable()
    {
        var request = await AssembleAsync(new Dictionary<TableIdentity, TableAccessDecision> { [Orders] = Allowed(OrdersId, RegionPolicy()) });

        // invoices is in the catalog but has no decision: the compiler must not know it.
        Should.Throw<SecurityException>(() => _engine.Compile("SELECT id FROM invoices".AsMemory(), request, CancellationToken.None));
    }

    [Fact]
    public async Task DeniedDecision_DeniesTable()
    {
        var request = await AssembleAsync(new Dictionary<TableIdentity, TableAccessDecision>
        {
            [Orders] = TableAccessDecision.Denied(OrdersId, "no")
        });

        Should.Throw<SecurityException>(() => _engine.Compile("SELECT id FROM orders".AsMemory(), request, CancellationToken.None));
    }

    [Fact]
    public async Task StringOnlyRowFilter_DeniesTable()
    {
        var legacy = TableAccessDecision.Allowed(OrdersId, new Dictionary<string, ColumnAccessLevel>(), rowFilterSql: "region = 'EU'", hasUnconstrainedColumnAllow: true);
        var request = await AssembleAsync(new Dictionary<TableIdentity, TableAccessDecision> { [Orders] = legacy });

        Should.Throw<SecurityException>(() => _engine.Compile("SELECT id FROM orders".AsMemory(), request, CancellationToken.None));
    }

    [Fact]
    public async Task MaskedColumnWithoutTypedMask_DeniesTable()
    {
        var request = await AssembleAsync(new Dictionary<TableIdentity, TableAccessDecision>
        {
            [Orders] = Allowed(OrdersId, RegionPolicy(), email: ColumnAccessLevel.Mask)
        });

        Should.Throw<SecurityException>(() => _engine.Compile("SELECT id FROM orders".AsMemory(), request, CancellationToken.None));
    }

    [Fact]
    public async Task CatalogSensitiveColumn_WithoutTypedMask_DeniesTable_WhenMetadataIsGiven()
    {
        var factory = Factory(Catalog(Entry(Orders)), out _);
        var metadata = new TableMetadata
        {
            Identifier = OrdersId,
            Columns = [new TableColumn { ColumnName = "email", IsSensitive = true }, new TableColumn { ColumnName = "id" }]
        };
        // No explicit level for email: only the catalog marks it sensitive, so it is masked unless a typed mask exists.
        var unlisted = TableAccessDecision.Allowed(OrdersId, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true)
            .WithAdditionalRowPolicy(RegionPolicy());
        var input = Input(new Dictionary<TableIdentity, TableAccessDecision> { [Orders] = unlisted }) with
        {
            Metadata = new Dictionary<TableIdentity, TableMetadata> { [Orders] = metadata }
        };

        var request = Request(await factory.CreateAsync(input, CancellationToken.None));

        Should.Throw<SecurityException>(() => _engine.Compile("SELECT id FROM orders".AsMemory(), request, CancellationToken.None));
    }

    [Fact]
    public async Task TypedMask_IsApplied()
    {
        var request = await AssembleAsync(new Dictionary<TableIdentity, TableAccessDecision>
        {
            [Orders] = Allowed(OrdersId, RegionPolicy("EU"), email: ColumnAccessLevel.Mask, masks: EmailMask())
        });

        var rows = Run(request, "SELECT id, email FROM orders");

        rows.Count.ShouldBe(1);
        rows[0][1].ShouldBe("***");
    }

    [Fact]
    public async Task DeniedColumn_IsNullified()
    {
        var request = await AssembleAsync(new Dictionary<TableIdentity, TableAccessDecision>
        {
            [Orders] = Allowed(OrdersId, RegionPolicy("EU"), email: ColumnAccessLevel.Deny)
        });

        var rows = Run(request, "SELECT id, email FROM orders");

        rows.Count.ShouldBe(1);
        rows[0][1].ShouldBeNull();
    }

    [Fact]
    public async Task NoRowPolicy_DeclaredWithoutRls_ReturnsTenantRows()
    {
        var request = await AssembleAsync(
            new Dictionary<TableIdentity, TableAccessDecision> { [Orders] = Allowed(OrdersId) }, withoutRls: [Orders]);

        Ids(Run(request, "SELECT id FROM orders")).ShouldBe(new List<int> { 1, 2 });
    }

    [Fact]
    public async Task NoRowPolicy_NotDeclaredWithoutRls_DeniesRows()
    {
        var request = await AssembleAsync(new Dictionary<TableIdentity, TableAccessDecision> { [Orders] = Allowed(OrdersId) });

        Run(request, "SELECT id FROM orders").ShouldBeEmpty();
    }

    [Fact]
    public async Task RowPolicy_DeclaredWithoutRls_IsContradiction_DeniesTable()
    {
        var request = await AssembleAsync(
            new Dictionary<TableIdentity, TableAccessDecision> { [Orders] = Allowed(OrdersId, RegionPolicy()) }, withoutRls: [Orders]);

        Should.Throw<SecurityException>(() => _engine.Compile("SELECT id FROM orders".AsMemory(), request, CancellationToken.None));
    }

    [Fact]
    public async Task ForeignRowPolicy_DeniesTable()
    {
        var foreign = Substitute.For<IRowPolicy>();
        foreign.Project().Returns(new RowPolicyText("region = 'EU'", new Dictionary<string, object?>()));
        foreign.Fingerprint.Returns("x");
        var decision = TableAccessDecision.Allowed(OrdersId, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true)
            .WithAdditionalRowPolicy(foreign);
        var request = await AssembleAsync(new Dictionary<TableIdentity, TableAccessDecision> { [Orders] = decision });

        Should.Throw<SecurityException>(() => _engine.Compile("SELECT id FROM orders".AsMemory(), request, CancellationToken.None));
    }

    [Fact]
    public async Task MandatoryPredicateText_ThatDiffersFromItsTypedPolicy_DeniesTable()
    {
        var vf = RegionPolicy("EU");
        var decision = TableAccessDecision.Allowed(OrdersId, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true)
            .WithMandatoryPredicate(vf, ["vf"]) with { MandatoryRowPredicateSql = "region = @__other" };
        var request = await AssembleAsync(new Dictionary<TableIdentity, TableAccessDecision> { [Orders] = decision });

        Should.Throw<SecurityException>(() => _engine.Compile("SELECT id FROM orders".AsMemory(), request, CancellationToken.None));
    }

    [Fact]
    public async Task MandatoryPredicate_WithMatchingTypedPolicy_IsEnforced()
    {
        var decision = TableAccessDecision.Allowed(OrdersId, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true)
            .WithMandatoryPredicate(RegionPolicy("US"), ["vf"]);
        var request = await AssembleAsync(new Dictionary<TableIdentity, TableAccessDecision> { [Orders] = decision });

        Ids(Run(request, "SELECT id FROM orders")).ShouldBe(new List<int> { 2 });
    }

    [Fact]
    public async Task TenantBinding_UsesCatalogTenantColumnType()
    {
        var factory = Factory(Catalog(Entry(Orders, tenantType: "INTEGER")), out _);
        var policy = await factory.CreateAsync(
            Input(new Dictionary<TableIdentity, TableAccessDecision> { [Orders] = Allowed(OrdersId, RegionPolicy()) }), CancellationToken.None);

        // The SQLite tenant comparison needs TEXT affinity; the type comes from the catalog entry, not from the factory.
        Should.Throw<SecurityException>(() => _engine.Compile("SELECT id FROM orders".AsMemory(), Request(policy), CancellationToken.None));
    }

    [Fact]
    public async Task TenantBinding_BindsTheTenantAsParameter()
    {
        var request = await AssembleAsync(new Dictionary<TableIdentity, TableAccessDecision> { [Orders] = Allowed(OrdersId, RegionPolicy()) });

        var compiled = _engine.Compile("SELECT id FROM orders".AsMemory(), request, CancellationToken.None);

        request.Policy.Tenant.ParameterName.ShouldBe(RowPolicyFactory.TenantParameterName);
        compiled.Parameters.Select(p => p.Value).ShouldContain("acme");
        compiled.Sql.ShouldNotContain("acme");
    }

    [Fact]
    public async Task Catalog_IsRequestedForEveryDecisionTable()
    {
        var factory = Factory(Catalog(Entry(Orders), Entry(Invoices)), out var provider);
        var decisions = new Dictionary<TableIdentity, TableAccessDecision>
        {
            [Orders] = Allowed(OrdersId, RegionPolicy()),
            [Invoices] = Allowed(InvoicesId, RegionPolicy())
        };

        await factory.CreateAsync(Input(decisions), CancellationToken.None);

        await provider.Received(1).GetCatalogAsync("ds",
            Arg.Is<IReadOnlyCollection<TableIdentifier>>(t => t.Count == 2 && t.Contains(OrdersId) && t.Contains(InvoicesId)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TableOmittedByTheCatalogProvider_IsRejectedByTheCompiler()
    {
        var request = await AssembleAsync(
            new Dictionary<TableIdentity, TableAccessDecision> { [Orders] = Allowed(OrdersId, RegionPolicy()) }, null, Entry(Invoices));

        Should.Throw<SecurityException>(() => _engine.Compile("SELECT id FROM orders".AsMemory(), request, CancellationToken.None));
    }

    [Fact]
    public async Task ConsentFilteredAndMaskedTables_AreListedForTheDmlGuards()
    {
        var factory = Factory(Catalog(Entry(Orders), Entry(Invoices)), out _);
        var decisions = new Dictionary<TableIdentity, TableAccessDecision>
        {
            [Orders] = Allowed(OrdersId, RegionPolicy(), email: ColumnAccessLevel.Mask, masks: EmailMask()),
            [Invoices] = Allowed(InvoicesId)
        };

        var policy = await factory.CreateAsync(Input(decisions, withoutRls: [Invoices]), CancellationToken.None);

        policy.TablesWithConsentRowFilter.ShouldBe(new[] { "main.orders" });
        policy.TablesWithMaskedColumns.ShouldBe(new[] { "main.orders" });
    }

    [Fact]
    public async Task MixedPolicyDialects_AreRefused()
    {
        var factory = Factory(Catalog(Entry(Orders), Entry(Invoices)), out _);
        var decisions = new Dictionary<TableIdentity, TableAccessDecision>
        {
            [Orders] = Allowed(OrdersId, RegionPolicy(dialect: TargetSqlDialect.Sqlite)),
            [Invoices] = Allowed(InvoicesId, RegionPolicy(dialect: TargetSqlDialect.PostgreSql))
        };

        await Should.ThrowAsync<SecurityException>(async () => await factory.CreateAsync(Input(decisions), CancellationToken.None));
    }

    [Fact]
    public async Task SubqueryStrategy_IsCarriedOver()
    {
        var factory = Factory(Catalog(Entry(Orders)), out _);

        var policy = await factory.CreateAsync(
            Input(new Dictionary<TableIdentity, TableAccessDecision> { [Orders] = Allowed(OrdersId, RegionPolicy()) }), CancellationToken.None);

        policy.SubqueryStrategy.ShouldBe(GovernedSubqueryStrategy.Correlated);
        policy.Dml.ShouldBe(DmlGuardOptions.Strict);
    }

    [Fact]
    public async Task PolicyDialect_DifferentFromTheRequestDialect_IsRefused()
    {
        var factory = Factory(Catalog(Entry(Orders)), out _);
        var policy = await factory.CreateAsync(
            Input(new Dictionary<TableIdentity, TableAccessDecision> { [Orders] = Allowed(OrdersId, RegionPolicy(dialect: TargetSqlDialect.Sqlite)) }),
            CancellationToken.None);

        Should.Throw<SecurityException>(() => CompileRequestFactory.Create(
            policy, TargetSqlDialect.PostgreSql, new WebSqlOptions(), false, 100, new HashSet<string>()));
    }

    // ---- X1: tables read by a policy subquery -----------------------------------------------------------------------------------

    /// <summary>EXISTS (SELECT 1 FROM main.invoices dep WHERE dep.id = autheris_target.id): the shape of a correlated consent filter.</summary>
    private static IRowPolicy CorrelatedPolicy(string? domain = null) => new PolicyPredicateRowPolicy(PolicyPredicate.Create(
        new ExistsExpression(new SelectStatement(null,
            new QuerySpecification(
                false,
                [new ColumnSelectItem(new ColumnReference(new SqlQualifiedName(new[] { new SqlIdentifier("dep", true), new SqlIdentifier("id", true) })), null)],
                new NamedTableSource(
                    new SqlQualifiedName(domain is null
                        ? new[] { new SqlIdentifier("main", true), new SqlIdentifier("invoices", true) }
                        : new[] { new SqlIdentifier(domain, true), new SqlIdentifier("main", true), new SqlIdentifier("invoices", true) }),
                    new SqlIdentifier("dep", true)),
                new BinaryExpression(
                    new ColumnReference(new SqlQualifiedName(new[] { new SqlIdentifier("dep", true), new SqlIdentifier("id", true) })),
                    BinaryOperator.Equal,
                    PolicyParameterNamer.Column($"{RowFilterAliases.Target}.id")),
                null, null),
            null, null)),
        new Dictionary<string, PolicyValue>()), TargetSqlDialect.Sqlite, Renderer);

    [Fact]
    public async Task PolicySubqueryTable_IsRequestedFromTheProvider_ResolvableForThePolicy_ButNeverAdmitted()
    {
        var factory = Factory(Catalog(Entry(Orders), Entry(Invoices)), out var provider);
        var decisions = new Dictionary<TableIdentity, TableAccessDecision> { [Orders] = Allowed(OrdersId, CorrelatedPolicy()) };

        var policy = await factory.CreateAsync(Input(decisions), CancellationToken.None);

        // the provider verifies the table that the policy reads, like any other table of the statement
        await provider.Received().GetCatalogAsync(
            "ds", Arg.Is<IReadOnlyCollection<TableIdentifier>>(t => t.Any(x => x.TableName == "invoices")), Arg.Any<CancellationToken>());
        var request = Request(policy);
        // the policy subquery resolves it (with the tenant predicate), so the statement compiles and filters by the other table's rows
        Ids(Run(request, "SELECT id FROM orders ORDER BY id")).ShouldBe(new List<int> { 1 });
        // a user reference is still decided by stage 3 and by the deny-all default: the table has no decision and no row policy
        Ids(Run(request, "SELECT id FROM invoices")).ShouldBeEmpty();
    }

    [Fact]
    public async Task PolicySubqueryTable_TheCatalogCannotVerify_DeniesTheTableOfThePolicy()
    {
        var factory = Factory(Catalog(Entry(Orders)), out _);   // invoices is not in the verified catalog
        var decisions = new Dictionary<TableIdentity, TableAccessDecision> { [Orders] = Allowed(OrdersId, CorrelatedPolicy()) };

        var policy = await factory.CreateAsync(Input(decisions), CancellationToken.None);

        // fail closed: a policy that cannot be enforced makes its own table unknown to the compiler (the rewriter answers SQL_POLICY_DENIED)
        policy.Catalog.Resolve(Orders.ToQualifiedName()).ShouldBeNull();
    }

    [Fact]
    public async Task CorrelatedConsentFilter_BuiltByTheProducer_CompilesAndFiltersEndToEnd()
    {
        // The producer of correlated consent filters (AdvancedRlsFilterGenerator) through the assembly and the compiler on a real database.
        var filter = new ConsentRowFilter
        {
            FilterType = RowFilterType.SubqueryCorrelated,
            DependentTable = InvoicesId,
            DependentTableAlias = "dep",
            ForeignKeyColumn = "id",
            PrimaryKeyColumn = "id",
            SubqueryFilterPredicateJson = null
        };
        var predicate = Autheris.Application.Services.AdvancedRlsFilterGenerator.BuildCorrelatedPredicate(filter);
        var policy = new PolicyPredicateRowPolicy(predicate, TargetSqlDialect.Sqlite, Renderer);
        var factory = Factory(Catalog(Entry(Orders), Entry(Invoices)), out _);
        var gp = await factory.CreateAsync(Input(new Dictionary<TableIdentity, TableAccessDecision> { [Orders] = Allowed(OrdersId, policy) }), CancellationToken.None);
        var request = Request(gp);

        Ids(Run(request, "SELECT id FROM orders ORDER BY id")).ShouldBe(new List<int> { 1 });
    }

    // ---- M-A1: dependent (policy-subquery) tables are governed like primary tables ---------------------------------------------

    private static GatewayOptions RequireTenant(params string[] exempt) => new()
    {
        DataSources = new SqlDataSourceOptions { RequireTenantColumn = true, TenantColumnExemptTables = exempt.ToList() }
    };

    private async Task<GovernancePolicy> AssembleDependentAsync(GatewayOptions options, bool withTenant, bool active = true)
    {
        var factory = Factory(Catalog(Entry(Orders), Entry(Invoices)), out _, options,
            id => id.TableName == "invoices" ? DependentMetadata(id, withTenant, active) : DependentMetadata(id, true));
        return await factory.CreateAsync(
            Input(new Dictionary<TableIdentity, TableAccessDecision> { [Orders] = Allowed(OrdersId, CorrelatedPolicy()) }), CancellationToken.None);
    }

    [Fact]
    public async Task DependentTable_WithoutTenantColumn_RequireTenantColumn_DeniesTheTableOfThePolicy()
    {
        var policy = await AssembleDependentAsync(RequireTenant(), withTenant: false);
        policy.Catalog.Resolve(Orders.ToQualifiedName()).ShouldBeNull();
    }

    [Fact]
    public async Task DependentTable_WithoutTenantColumn_Exempt_IsAllowed()
    {
        var policy = await AssembleDependentAsync(RequireTenant("main.invoices"), withTenant: false);
        policy.Catalog.Resolve(Orders.ToQualifiedName()).ShouldNotBeNull();
    }

    [Fact]
    public async Task DependentTable_WithoutTenantColumn_RequirementOff_Unchanged()
    {
        var policy = await AssembleDependentAsync(new GatewayOptions(), withTenant: false);
        policy.Catalog.Resolve(Orders.ToQualifiedName()).ShouldNotBeNull();
    }

    [Fact]
    public async Task DependentTable_Inactive_DeniesTheTableOfThePolicy()
    {
        var policy = await AssembleDependentAsync(new GatewayOptions(), withTenant: true, active: false);
        policy.Catalog.Resolve(Orders.ToQualifiedName()).ShouldBeNull();
    }

    [Fact]
    public async Task DependentTable_WithoutGovernanceRecord_DeniesTheTableOfThePolicy()
    {
        var factory = Factory(Catalog(Entry(Orders), Entry(Invoices)), out _, null, id => id.TableName == "invoices" ? null : DependentMetadata(id, true));
        var policy = await factory.CreateAsync(
            Input(new Dictionary<TableIdentity, TableAccessDecision> { [Orders] = Allowed(OrdersId, CorrelatedPolicy()) }), CancellationToken.None);
        policy.Catalog.Resolve(Orders.ToQualifiedName()).ShouldBeNull();
    }

    // ---- L-1: the governance record that the catalog provider can admit decides, not the first homonymous one ---------------------

    private static TableMetadata HomonymMetadata(TableIdentifier id, bool admittedRecordWithTenant, bool admittedRecordActive)
    {
        // The provider only admits a record whose data source is the one of the statement ("ds"); the homonym of the other domain is
        // active and has a tenant column, i.e. it would pass every check if it were the one that was looked at.
        var admitted = string.Equals(id.Domain, "ds", StringComparison.OrdinalIgnoreCase);
        return DependentMetadata(id, admitted ? admittedRecordWithTenant : true, admitted ? admittedRecordActive : true, admitted ? "ds" : "elsewhere");
    }

    private async Task<GovernancePolicy> AssembleHomonymAsync(GatewayOptions options, bool admittedWithTenant, bool admittedActive)
    {
        var factory = Factory(Catalog(Entry(Orders), Entry(Invoices)), out _, options,
            id => id.TableName == "invoices" ? HomonymMetadata(id, admittedWithTenant, admittedActive) : DependentMetadata(id, true));
        return await factory.CreateAsync(
            Input(new Dictionary<TableIdentity, TableAccessDecision> { [Orders] = Allowed(OrdersId, CorrelatedPolicy("elsewhere")) }), CancellationToken.None);
    }

    [Fact]
    public async Task DependentTable_HomonymInAnotherDomain_InactiveAdmittedRecord_DeniesTheTableOfThePolicy()
    {
        var policy = await AssembleHomonymAsync(new GatewayOptions(), admittedWithTenant: true, admittedActive: false);
        policy.Catalog.Resolve(Orders.ToQualifiedName()).ShouldBeNull();
    }

    [Fact]
    public async Task DependentTable_HomonymInAnotherDomain_TenantlessAdmittedRecord_RequireTenantColumn_DeniesTheTableOfThePolicy()
    {
        var policy = await AssembleHomonymAsync(RequireTenant(), admittedWithTenant: false, admittedActive: true);
        policy.Catalog.Resolve(Orders.ToQualifiedName()).ShouldBeNull();
    }

    [Fact]
    public async Task DependentTable_HomonymInAnotherDomain_UsableAdmittedRecord_IsAllowed()
    {
        var policy = await AssembleHomonymAsync(RequireTenant(), admittedWithTenant: true, admittedActive: true);
        policy.Catalog.Resolve(Orders.ToQualifiedName()).ShouldNotBeNull();
    }
}
