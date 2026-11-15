namespace Autheris.Tests.Unit.Catalog.Discovery;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Discovery;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.Procedures.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class ProcedureDiscoveryTests
{
    private sealed class FakeProcedureCatalog(IReadOnlyList<RegisteredProcedure> entries) : IProcedureCatalog
    {
        public Task<IReadOnlyList<RegisteredProcedure>> ListVisibleAsync(ClaimsPrincipal user, TenantId tenantId, CancellationToken ct)
            => Task.FromResult(entries);

        public Task<RegisteredProcedure?> FindVisibleAsync(string name, ClaimsPrincipal user, TenantId tenantId, CancellationToken ct)
            => Task.FromResult(entries.FirstOrDefault(e => string.Equals(e.Definition.Name, name, StringComparison.OrdinalIgnoreCase)));
    }

    private sealed class NoopExecutionService : IGatewayExecutionService
    {
        public Task<(IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows, TableAccessDecision Decision)> ExecuteTableQueryAsync(ClaimsPrincipal? principal, TableIdentifier table, int? first = null, int? after = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows, TableAccessDecision Decision)> ExecuteTableQueryAsync(ClaimsPrincipal? principal, TableIdentifier table, int? first, int? after, IReadOnlyDictionary<string, object?>? queryArguments, IReadOnlyList<string>? requestedFields = null, IReadOnlyDictionary<string, string[]>? requestHeaders = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<TableAccessDecision> CheckTableAccessAsync(ClaimsPrincipal? principal, TableIdentifier table, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static RegisteredProcedure MakeProc(
        string name,
        string domain,
        ProcedureMode mode,
        ProcedureState state,
        string? resultTable,
        params string[] referencedTables)
    {
        var def = new ProcedureDefinition(
            Name: name,
            Summary: $"Summary for {name}",
            ProcedureName: $"dbo.{name}",
            Mode: mode,
            DataSource: domain,
            Parameters:
            [
                new ProcedureParameter("from_date", "date", typeof(DateTime), IsRequired: true, Description: "Start date")
            ],
            ContextBindings: [],
            RlsMode: ProcedureRlsMode.SessionContext,
            ResultTable: resultTable,
            ClearedResultColumns: [],
            RequiredRoles: [],
            TimeoutSeconds: 30)
        {
            Description = $"Description for {name}"
        };

        var validation = new ProcedureValidationResult(
            IsValid: true,
            Errors: [],
            ResultColumns: [],
            ReferencedTables: referencedTables,
            ParameterSqlTypes: new Dictionary<string, string>());

        return new RegisteredProcedure(def, state, validation, DateTimeOffset.UtcNow, null);
    }

    [Fact]
    public async Task DiscoveryService_SurfacesActiveReadProcedures_AndOmitsNonEligibleOnes()
    {
        var tables = new FakeDiscoveryTableRepository();
        var orders = DiscoveryTestBuilder.MakeTable("sales", "orders", ownerTenantId: "tenant-a");
        var hidden = DiscoveryTestBuilder.MakeTable("sales", "hidden_orders", ownerTenantId: "tenant-a", hidden: true);
        var foreign = DiscoveryTestBuilder.MakeTable("sales", "foreign_orders", ownerTenantId: "tenant-b");
        tables.Tables.AddRange([orders, hidden, foreign]);

        var catalog = new FakeProcedureCatalog([
            MakeProc("get_orders", "sales", ProcedureMode.Read, ProcedureState.Active, "dbo.orders", "dbo.orders"),
            MakeProc("pending_proc", "sales", ProcedureMode.Read, ProcedureState.Pending, "dbo.orders", "dbo.orders"),
            MakeProc("disabled_proc", "sales", ProcedureMode.Read, ProcedureState.Disabled, "dbo.orders", "dbo.orders"),
            MakeProc("write_proc", "sales", ProcedureMode.Write, ProcedureState.Active, "dbo.orders", "dbo.orders"),
            MakeProc("empty_governed", "sales", ProcedureMode.Read, ProcedureState.Active, null),
            MakeProc("uses_hidden", "sales", ProcedureMode.Read, ProcedureState.Active, "dbo.hidden_orders", "dbo.hidden_orders"),
            MakeProc("uses_foreign", "sales", ProcedureMode.Read, ProcedureState.Active, "dbo.foreign_orders", "dbo.foreign_orders")
        ]);

        var pdp = new FakeAccessDecisionPoint(req =>
            req.Operation == AccessOperation.Execute && req.Object.Identifier.Equals(orders.Identifier)
                ? TableAccessDecision.Allowed(orders.Identifier, new Dictionary<string, ColumnAccessLevel>(), "tenant_id = @t", hasUnconstrainedColumnAllow: true)
                : TableAccessDecision.Allowed(req.Object.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true));

        var service = new DiscoveryService(
            tables,
            pdp,
            new FakeDiscoveryConsentRepository(),
            new FakeDiscoveryApprovalRepository(),
            new FakeDiscoveryOwnershipRepository(),
            new FakeDiscoveryArtifactStore(),
            new FakeAuditWriter(),
            Options.Create(new GatewayOptions { Tenancy = TenancyShape.Multi.Options() }),
            Options.Create(new CatalogDiscoveryOptions()),
            procedureCatalog: catalog,
            logger: NullLogger<DiscoveryService>.Instance);

        var caller = DiscoveryTestBuilder.Caller(DiscoveryTestBuilder.User("S-USER", "tenant-a"));

        var page = await service.ListAsync(new DiscoveryQuery(Surface: DiscoverySurface.Rest), caller);
        var procedures = page.Items.Where(static i => i.Kind == DiscoveryObjectKind.Procedure).ToList();
        procedures.Count.ShouldBe(1);
        var proc = procedures[0];
        proc.Domain.ShouldBe("sales");
        proc.Schema.ShouldBe("procedure");
        proc.Name.ShouldBe("get_orders");
        proc.Access.ShouldBe(DiscoveryAccessState.Granted);
        proc.RowScope.ShouldBe(DiscoveryRowScope.RowRestricted);
        proc.Columns.Count.ShouldBe(1);
        proc.Columns[0].Name.ShouldBe("from_date");
        proc.Columns[0].Pii.ShouldBeFalse();

        pdp.Requests.ShouldContain(r => r.Operation == AccessOperation.Execute && r.Object.Identifier.Equals(orders.Identifier));

        // GetAsync & SearchAsync find the procedure too
        var fetched = await service.GetAsync(new TableIdentifier("sales", "procedure", "get_orders"), caller);
        fetched.ShouldNotBeNull();
        fetched!.Kind.ShouldBe(DiscoveryObjectKind.Procedure);

        var searched = await service.SearchAsync("from_date", new DiscoverySearchOptions(Surface: DiscoverySurface.Search), caller);
        searched.ShouldContain(o => o.Kind == DiscoveryObjectKind.Procedure && o.Name == "get_orders");
    }

    [Fact]
    public async Task DiscoveryService_ProcedureAccessStates_ReflectExecutePdpAndPendingOrExpired()
    {
        var tables = new FakeDiscoveryTableRepository();
        var orders = DiscoveryTestBuilder.MakeTable("sales", "orders", ownerTenantId: "tenant-a");
        tables.Tables.Add(orders);

        var catalog = new FakeProcedureCatalog([
            MakeProc("get_orders", "sales", ProcedureMode.Read, ProcedureState.Active, "dbo.orders", "dbo.orders")
        ]);

        var pdp = FakeAccessDecisionPoint.DenyAll();
        var approvals = new FakeDiscoveryApprovalRepository();
        approvals.Requests.Add(new ConsentRequest
        {
            Id = Guid.NewGuid(),
            TableIdentifier = orders.Identifier,
            RequesterSid = new Sid("S-REQ"),
            Status = "PENDING"
        });
        var consents = new FakeDiscoveryConsentRepository
        {
            RecentlyExpired = new HashSet<TableIdentifier> { orders.Identifier }
        };

        var service = new DiscoveryService(
            tables,
            pdp,
            consents,
            approvals,
            new FakeDiscoveryOwnershipRepository(),
            new FakeDiscoveryArtifactStore(),
            new FakeAuditWriter(),
            Options.Create(new GatewayOptions { Tenancy = TenancyShape.Multi.Options() }),
            Options.Create(new CatalogDiscoveryOptions()),
            procedureCatalog: catalog);

        var requestedCaller = DiscoveryTestBuilder.Caller(DiscoveryTestBuilder.User("S-REQ", "tenant-a"));
        var requested = await service.GetAsync(new TableIdentifier("sales", "procedure", "get_orders"), requestedCaller);
        requested!.Access.ShouldBe(DiscoveryAccessState.Requested);

        var expiredCaller = DiscoveryTestBuilder.Caller(DiscoveryTestBuilder.User("S-EXP", "tenant-a"));
        var expired = await service.GetAsync(new TableIdentifier("sales", "procedure", "get_orders"), expiredCaller);
        expired!.Access.ShouldBe(DiscoveryAccessState.Expired);

        consents.RecentlyExpired = new HashSet<TableIdentifier>();
        var noneCaller = DiscoveryTestBuilder.Caller(DiscoveryTestBuilder.User("S-NONE", "tenant-a"));
        var none = await service.GetAsync(new TableIdentifier("sales", "procedure", "get_orders"), noneCaller);
        none!.Access.ShouldBe(DiscoveryAccessState.None);
        none.RowScope.ShouldBeNull();
    }

    [Fact]
    public async Task McpDatasetCatalog_WithDiscoveryServiceAndProcedureCatalog_DoesNotDuplicateProcedures()
    {
        var tables = new FakeDiscoveryTableRepository();
        var orders = DiscoveryTestBuilder.MakeTable("sales", "orders", ownerTenantId: "tenant-a");
        tables.Tables.Add(orders);

        var proc = MakeProc("get_orders", "sales", ProcedureMode.Read, ProcedureState.Active, "dbo.orders", "dbo.orders");
        var procCatalog = new FakeProcedureCatalog([proc]);

        var discovery = new DiscoveryService(
            tables,
            FakeAccessDecisionPoint.AllowAll(),
            new FakeDiscoveryConsentRepository(),
            new FakeDiscoveryApprovalRepository(),
            new FakeDiscoveryOwnershipRepository(),
            new FakeDiscoveryArtifactStore(),
            new FakeAuditWriter(),
            Options.Create(new GatewayOptions { Tenancy = TenancyShape.Multi.Options() }),
            Options.Create(new CatalogDiscoveryOptions()),
            procedureCatalog: procCatalog);

        var mcp = new McpDatasetCatalog(
            tables,
            new FakeDiscoveryConsentRepository(),
            new NoopExecutionService(),
            procedureCatalog: procCatalog,
            discoveryService: discovery);

        var caller = DiscoveryTestBuilder.User("S-USER", "tenant-a");
        var list = await mcp.ListDatasetsAsync(caller);

        list.Datasets.Count(d => string.Equals(d.Kind, "procedure", StringComparison.OrdinalIgnoreCase)).ShouldBe(1);
    }
}
