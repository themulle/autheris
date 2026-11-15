namespace Autheris.Tests.Unit.Procedures;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Configuration;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.Procedures.Interfaces;
using Autheris.Application.Procedures.Services;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Tests.Unit.TestDoubles;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

public sealed class ProcedureDeclaredOnlyTests
{
    private static readonly TenantId Tenant = new("tenant-a");

    private static ClaimsPrincipal Caller() => new(new ClaimsIdentity(
    [
        new Claim("sub", "user-1"),
        new Claim("sid", "S-1-5-21-1"),
        new Claim("tenant_id", Tenant.Value)
    ], "TestAuth"));

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("validation: declared")]
    [InlineData("validation: catalog")]
    public void ValidationKey_Rejected(string validationLine)
    {
        string yaml = $"""
            name: get_orders
            procedure: api.usp_GetOrders
            {validationLine}
            result_table: sales.orders
            tables:
              - sales.orders
            outputs:
              - order_id
            """;

        Should.Throw<FormatException>(() => ProcedureDefinitionParser.ParseYaml(yaml, "erp", allowRlsNone: false, maxTimeoutSeconds: 60));
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("name: p\nprocedure: api.usp_P\nresult_table: sales.orders\noutputs:\n  - order_id\n")] // missing tables
    [InlineData("name: p\nprocedure: api.usp_P\nresult_table: sales.orders\ntables:\n  - sales.orders\n")] // missing outputs
    [InlineData("name: p\nprocedure: api.usp_P\ntables:\n  - sales.orders\noutputs:\n  - order_id\n")] // missing result_table
    public async Task MissingTablesOrOutputs_Pending(string yaml)
    {
        var (svc, registry) = CreateRegistrationHarness(
            tableRepo: new FakeTableMetadataRepository(SalesOrdersMeta()),
            filters: null);

        registry.Register(ProcedureDefinitionParser.ParseYaml(yaml, "erp", false, 60));
        await svc.ValidateDueAsync(CancellationToken.None);

        registry.TryGet("p", out var entry).ShouldBeTrue();
        entry!.State.ShouldBe(ProcedureState.Pending);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task UnregisteredTable_Pending()
    {
        const string yaml = """
            name: get_orders
            procedure: api.usp_GetOrders
            result_table: sales.orders
            tables:
              - sales.orders
              - sales.missing_table
            outputs:
              - order_id
            """;

        var repo = new FakeTableMetadataRepository(SalesOrdersMeta());
        var (svc, registry) = CreateRegistrationHarness(tableRepo: repo, filters: null);

        registry.Register(ProcedureDefinitionParser.ParseYaml(yaml, "erp", false, 60));
        await svc.ValidateDueAsync(CancellationToken.None);

        registry.TryGet("get_orders", out var pending).ShouldBeTrue();
        pending!.State.ShouldBe(ProcedureState.Pending);
        pending.DisabledReason.ShouldNotBeNull();
        pending.DisabledReason.ShouldContain("TABLE_NOT_REGISTERED");

        // Once the missing table is registered, the next validation round activates it.
        repo.Add(TableMeta("default", "sales", "missing_table", "id"));
        await svc.ValidateDueAsync(CancellationToken.None);

        registry.TryGet("get_orders", out var active).ShouldBeTrue();
        active!.State.ShouldBe(ProcedureState.Active);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task RlsSessionContextWithRowFilters_RequiresRowScopeKey()
    {
        const string yamlWithoutKey = """
            name: get_orders
            procedure: api.usp_GetOrders
            rls: session_context
            result_table: sales.orders
            tables:
              - sales.orders
            outputs:
              - order_id
            """;

        var snapshot = ActiveOrderFilterSnapshot("default.sales.orders");
        var filterProvider = new FakeVirtualFilterSnapshotProvider(snapshot);

        // Loader check rejects fail-closed with ROW_SCOPE_REQUIRED.
        string dir = Path.Combine(Path.GetTempPath(), "autheris-decl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var options = Options.Create(new GatewayOptions
            {
                DataSources = ProcedureTestData.SqlServerSources("procedures"),
                SqlEndpoints = new SqlEndpointsOptions
                {
                    Procedures = new ProcedureEndpointsOptions
                    {
                        Enabled = true,
                        Directory = dir,
                        AllowedSchemas = ["api"],
                        RequireDdlHashForDeclared = false
                    }
                }
            });

            var registry = new InMemoryProcedureRegistry();
            var loader = new ProcedureDefinitionLoader(registry, options, virtualFilters: filterProvider);
            var def = ProcedureDefinitionParser.ParseYaml(yamlWithoutKey, "erp", false, 60);
            loader.CheckPolicy(def).ShouldBe("ROW_SCOPE_REQUIRED");

            // RegistrationService also disables with ROW_SCOPE_REQUIRED if registered directly.
            var (svc, reg2) = CreateRegistrationHarness(
                tableRepo: new FakeTableMetadataRepository(SalesOrdersMeta()),
                filters: filterProvider);
            reg2.Register(def);
            await svc.ValidateDueAsync(CancellationToken.None);
            reg2.TryGet("get_orders", out var disabled).ShouldBeTrue();
            disabled!.State.ShouldBe(ProcedureState.Disabled);
            disabled.DisabledReason.ShouldBe("ROW_SCOPE_REQUIRED");

            // Adding row_scope_key satisfies the check.
            const string yamlWithKey = yamlWithoutKey + "\nrow_scope_key: order_id\n";
            var defWithKey = ProcedureDefinitionParser.ParseYaml(yamlWithKey, "erp", false, 60);
            loader.CheckPolicy(defWithKey).ShouldBeNull();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task UndeclaredResultColumn_Dropped()
    {
        const string yaml = """
            name: get_orders
            procedure: api.usp_GetOrders
            result_table: sales.orders
            tables:
              - sales.orders
            outputs:
              - order_id
            """;

        var def = ProcedureDefinitionParser.ParseYaml(yaml, "default", false, 60);
        var invoker = new FakeProcedureInvoker(new RawProcedureResult(
            Columns: ["order_id", "secret_token"],
            Rows: [[42, "leaked-secret"]],
            MoreRowsAvailable: false));

        var ordersMeta = TableMeta("default", "sales", "orders", "order_id", "secret_token");
        var repo = new FakeTableMetadataRepository(ordersMeta);
        var pdp = new FakeAccessDecisionPoint(id =>
            TableAccessDecision.Allowed(
                id,
                new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
                {
                    ["order_id"] = ColumnAccessLevel.Clear,
                    ["secret_token"] = ColumnAccessLevel.Clear
                },
                null));

        var svc = CreateExecutionService(def, invoker, repo, pdp);
        var result = await svc.ExecuteAsync("get_orders", null, Caller(), Tenant);

        result.Columns.ShouldBe(["order_id"]);
        result.Rows.Single().Keys.ShouldBe(["order_id"]);
        result.Rows.Single().ShouldNotContainKey("secret_token");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task DeclaredSourceColumn_GovernedBySourceTable()
    {
        const string yaml = """
            name: get_order_details
            procedure: api.usp_GetOrderDetails
            result_table: sales.orders
            tables:
              - sales.orders
              - sales.customers
            outputs:
              - name: order_id
                type: int
              - name: amount
                type: decimal(18,2)
              - name: customer_email
                type: nvarchar(200)
                source: sales.customers.email
              - name: customer_phone
                type: nvarchar(50)
                source: sales.customers.phone
            """;

        var def = ProcedureDefinitionParser.ParseYaml(yaml, "default", false, 60);
        var invoker = new FakeProcedureInvoker(new RawProcedureResult(
            Columns: ["order_id", "amount", "customer_email", "customer_phone"],
            Rows: [[10, 99.50m, "alice@example.test", "+41-555-0100"]],
            MoreRowsAvailable: false));

        var ordersMeta = TableMeta("default", "sales", "orders", "order_id", "amount");
        var customersMeta = TableMeta("default", "sales", "customers", "email", "phone");
        var repo = new FakeTableMetadataRepository(ordersMeta, customersMeta);

        var pdp = new FakeAccessDecisionPoint(id =>
        {
            if (string.Equals(id.TableName, "orders", StringComparison.OrdinalIgnoreCase))
            {
                return TableAccessDecision.Allowed(
                    id,
                    new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["order_id"] = ColumnAccessLevel.Clear,
                        ["amount"] = ColumnAccessLevel.Mask
                    },
                    null);
            }

            return TableAccessDecision.Allowed(
                id,
                new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
                {
                    ["email"] = ColumnAccessLevel.Clear,
                    ["phone"] = ColumnAccessLevel.Mask // foreign column masked -> dropped (needs explicit Clear)
                },
                null);
        });

        var svc = CreateExecutionService(def, invoker, repo, pdp);
        var result = await svc.ExecuteAsync("get_order_details", null, Caller(), Tenant);

        // order_id (no source -> result_table Clear), amount (no source -> result_table Mask),
        // customer_email (foreign Clear -> survives), customer_phone (foreign Mask -> dropped).
        result.Columns.ShouldBe(["order_id", "amount", "customer_email"]);
        var row = result.Rows.Single();
        row["order_id"].ShouldBe(10);
        row["amount"].ShouldBe("***");
        row["customer_email"].ShouldBe("alice@example.test");
        row.ShouldNotContainKey("customer_phone");
    }

    [Theory]
    [InlineData("Gateway:SqlEndpoints:Procedures:AllowDeclaredValidation", "true")]
    [InlineData("Gateway:SqlEndpoints:Procedures:AllowDeclaredValidation", "false")]
    [InlineData("Gateway:Procedures:AllowDeclaredValidation", "true")]
    [InlineData("Gateway:Procedures:AllowDeclaredValidation", "false")]
    [InlineData("Gateway:Rest:AllowDeclaredValidation", "true")]
    [InlineData("Gateway:Rest:AllowDeclaredValidation", "false")]
    public void AllowDeclaredValidationKey_FailsStartup(string key, string value)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
            .Build();

        var ex = Should.Throw<InvalidOperationException>(() => UnknownGatewayKeyGuard.ThrowIfUnknownKeys(config));
        ex.Message.ShouldContain("AllowDeclaredValidation");
    }

    private static TableMetadata SalesOrdersMeta() => TableMeta("default", "sales", "orders", "order_id", "amount");

    private static TableMetadata TableMeta(string domain, string schema, string table, params string[] columns) => new()
    {
        Identifier = new TableIdentifier(domain, schema, table),
        Table = new Table { SourceName = domain, SchemaName = schema, TableName = table },
        Columns = columns.Select(c => new TableColumn { ColumnName = c }).ToList()
    };

    private static VirtualFilterSnapshot ActiveOrderFilterSnapshot(string pattern) => new(
        1,
        [
            new VirtualFilter
            {
                TenantId = Tenant,
                Name = "order-scope",
                Source = "default",
                KeyColumns = ["t.order_id"],
                Structured = new StructuredFilterDefinition { From = new TableIdentifier("default", "sales", "orders"), FromAlias = "t" }
            }
        ],
        [
            new VirtualFilterAccessProfile
            {
                TenantId = Tenant,
                Name = "reader-profile",
                Scope = "default.sales.*",
                Uncovered = UncoveredPolicy.Skip,
                Bindings = [new FilterBinding { FilterName = "order-scope", TargetPattern = pattern }]
            }
        ]);

    private static (ProcedureRegistrationService Service, InMemoryProcedureRegistry Registry) CreateRegistrationHarness(
        ITableMetadataRepository? tableRepo,
        IVirtualFilterSnapshotProvider? filters)
    {
        var options = Options.Create(new GatewayOptions
        {
            DataSources = ProcedureTestData.SqlServerSources("procedures"),
            SqlEndpoints = new SqlEndpointsOptions
            {
                Procedures = new ProcedureEndpointsOptions
                {
                    Enabled = true,
                    AllowedSchemas = ["api"],
                    RequireDdlHashForDeclared = false
                }
            }
        });

        var registry = new InMemoryProcedureRegistry();
        var loader = new ProcedureDefinitionLoader(registry, options);
        var services = new ServiceCollection();
        if (tableRepo != null) services.AddSingleton(tableRepo);
        if (filters != null) services.AddSingleton(filters);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        return (new ProcedureRegistrationService(loader, registry, scopeFactory, options), registry);
    }

    private static GovernedProcedureExecutionService CreateExecutionService(
        ProcedureDefinition def,
        IProcedureInvoker invoker,
        ITableMetadataRepository repo,
        IAccessDecisionPoint pdp)
    {
        var registry = new InMemoryProcedureRegistry();
        registry.Register(def);
        registry.MarkActive(def.Name, new ProcedureValidationResult(
            IsValid: true,
            Errors: [],
            ResultColumns: def.DeclaredOutputs,
            ReferencedTables: def.ReferencedTables,
            ParameterSqlTypes: new Dictionary<string, string>(),
            ResultColumnSources: def.DeclaredOutputSources.Count > 0 ? def.DeclaredOutputSources : null));

        var options = Options.Create(new GatewayOptions
        {
            SqlEndpoints = new SqlEndpointsOptions
            {
                Procedures = new ProcedureEndpointsOptions { Enabled = true, AllowedSchemas = ["api"] }
            }
        });

        return new GovernedProcedureExecutionService(
            registry,
            invoker,
            options,
            NullAuditLogRepository.Instance,
            tableRepository: repo,
            masking: new RedactMaskingProvider(),
            concurrencyGate: GrantAllReadConcurrencyGate.Instance,
            accessDecisionPoint: pdp);
    }

    private sealed class FakeProcedureInvoker(RawProcedureResult result) : IProcedureInvoker
    {
        public Task<RawProcedureResult> ExecuteReadAsync(
            ProcedureDefinition definition,
            IReadOnlyDictionary<string, object?> clientValues,
            ProcedureSecurityContext security,
            int rowBudget,
            CancellationToken ct) => Task.FromResult(result);
    }

    private sealed class FakeVirtualFilterSnapshotProvider(VirtualFilterSnapshot snapshot) : IVirtualFilterSnapshotProvider
    {
        public ValueTask<VirtualFilterSnapshot> GetAsync(CancellationToken ct = default) => ValueTask.FromResult(snapshot);
        public void Invalidate() { }
    }

    private sealed class FakeTableMetadataRepository : ITableMetadataRepository
    {
        private readonly Dictionary<TableIdentifier, TableMetadata> _tables = new();

        public FakeTableMetadataRepository(params TableMetadata[] initial)
        {
            foreach (var t in initial) _tables[t.Identifier] = t;
        }

        public void Add(TableMetadata meta) => _tables[meta.Identifier] = meta;

        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default) =>
            Task.FromResult(_tables.TryGetValue(table, out var meta) ? meta : null);

        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TableMetadata>>(_tables.Values.ToList());

        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default)
        {
            _tables[metadata.Identifier] = metadata;
            return Task.FromResult(metadata);
        }

        public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(
            TableIdentifier table,
            IReadOnlyList<string> primaryKey,
            IReadOnlyList<TableIndexDefinition> indexes,
            IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null,
            CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeAccessDecisionPoint(Func<TableIdentifier, TableAccessDecision> decide) : IAccessDecisionPoint
    {
        public ValueTask<TableAccessDecision> DecideAsync(AccessRequest request, CancellationToken ct = default) =>
            ValueTask.FromResult(decide(request.Object.Identifier));

        public ValueTask<IReadOnlyList<TableAccessDecision>> DecideManyAsync(
            SecurityPrincipalContext subject,
            IReadOnlyList<TableMetadata> objects,
            AccessOperation operation,
            AccessTransport transport,
            CancellationToken ct = default) =>
            ValueTask.FromResult<IReadOnlyList<TableAccessDecision>>(objects.Select(o => decide(o.Identifier)).ToList());

        public ValueTask<AccessExplanation> ExplainAsync(AccessRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class RedactMaskingProvider : IColumnMaskingProvider
    {
        public object? MaskValue(string columnName, object? rawValue, MaskingRule rule) => "***";
    }
}
