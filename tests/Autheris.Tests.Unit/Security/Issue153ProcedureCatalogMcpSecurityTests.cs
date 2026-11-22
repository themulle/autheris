namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Services;
using Autheris.Application.DataSources.Interfaces;
using Autheris.Application.Governance.Contracts;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Lineage;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Procedures.Interfaces;
using Autheris.Application.Procedures.Services;
using Autheris.Application.Sql.Interfaces;
using Autheris.Tests.Common.Fakes;
using Autheris.Tests.Unit.TestDoubles;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.GraphQL.Mcp;
using HotChocolate.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// Security Verification Suite for Issue #153: Governed Stored Procedures in Catalog and MCP.
/// Covers binding guardrails SEC-GOV153-01..05 and conditions T_SEC_GOV153_01..08.
/// </summary>
[Trait("Category", "Security")]
public sealed class Issue153ProcedureCatalogMcpSecurityTests
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";

    private static readonly TableIdentifier OrdersTableId = new("sales", "dbo", "orders");
    private static readonly TableIdentifier CustomersTableId = new("sales", "dbo", "customers");

    private static TableMetadata CreateTableMetadata(TableIdentifier id, params (string Name, string Type, bool Sensitive)[] columns) =>
        new()
        {
            Identifier = id,
            CatalogView = CatalogView.Granted,
            Table = new Table { TableName = id.TableName, SchemaName = id.Schema, Description = $"{id.TableName} table" },
            Columns = columns.Select(c => new TableColumn { ColumnName = c.Name, DataType = c.Type, Description = c.Name }).ToList(),
            ColumnClassifications = TableMetadataClassificationTestExtensions.PiiFor(columns.Where(c => c.Sensitive).Select(c => c.Name).ToArray())
        };

    private static RegisteredProcedure CreateProcedure(
        string name,
        ProcedureMode mode = ProcedureMode.Read,
        string? resultTable = "sales.dbo.orders",
        ProcedureState state = ProcedureState.Active,
        IReadOnlyList<ProcedureParameter>? parameters = null)
    {
        var def = new ProcedureDefinition(
            Name: name,
            Summary: $"{name} summary",
            ProcedureName: $"usp_{name}",
            Mode: mode,
            DataSource: "sales",
            Parameters: parameters ?? [new ProcedureParameter("customerId", "int", typeof(int), true, Description: "Customer ID")],
            ContextBindings: [],
            RlsMode: ProcedureRlsMode.SessionContext,
            ResultTable: resultTable,
            ClearedResultColumns: [],
            RequiredRoles: [],
            TimeoutSeconds: 30)
        {
            Description = $"{name} description"
        };

        var validation = new ProcedureValidationResult(true, [], [], [], new Dictionary<string, string>());
        return new RegisteredProcedure(def, state, validation, DateTimeOffset.UtcNow, null);
    }

    private static ClaimsPrincipal CreatePrincipal(string userSid = "S-1-USER", string tenant = TenantA, bool isReadOnly = false)
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.PrimarySid, userSid),
                new Claim("sub", userSid),
                new Claim("tid", tenant),
                new Claim("tenant_id", tenant),
                new Claim(ClaimTypes.Role, "DataAnalyst")
            ],
            "Bearer");

        if (isReadOnly)
        {
            TokenAccessScope.MarkReadOnly(identity);
        }

        return new ClaimsPrincipal(identity);
    }

    private static McpSessionContext CreateSession(string userSid = "S-1-USER", string tenant = TenantA, bool isReadOnly = false) =>
        new(
            SessionId: "session-153",
            ServicePrincipalId: userSid,
            TenantId: tenant,
            CreatedAt: DateTimeOffset.UtcNow,
            LastActiveAt: DateTimeOffset.UtcNow,
            UserSid: userSid,
            Roles: ["DataAnalyst"],
            GroupSids: ["group-analysts"],
            ClientIp: "127.0.0.1",
            IsReadOnly: isReadOnly);

    private static readonly IGovernedDataQueryService PassiveDataQuery = Substitute.For<IGovernedDataQueryService>();
    private static readonly ICatalogDiscoveryService PassiveCatalogDiscovery = Substitute.For<ICatalogDiscoveryService>();
    private static readonly IApiDispatcherService PassiveApiDispatcher = Substitute.For<IApiDispatcherService>();
    private static readonly IRequestExecutorProvider PassiveRequestExecutorProvider = Substitute.For<IRequestExecutorProvider>();

    private static InMemoryProcedureRegistry CreateRegistry(params RegisteredProcedure[] procedures)
    {
        var registry = new InMemoryProcedureRegistry();
        foreach (var p in procedures)
        {
            registry.Register(p.Definition);
            if (p.State == ProcedureState.Active)
            {
                registry.MarkActive(p.Definition.Name, p.Validation ?? new ProcedureValidationResult(true, [], [], [], new Dictionary<string, string>()));
            }
            else if (p.State == ProcedureState.Disabled)
            {
                registry.MarkDisabled(p.Definition.Name, p.DisabledReason ?? "Disabled");
            }
        }
        return registry;
    }

    private static McpDatasetCatalog CreateCatalog(
        ITableMetadataRepository? metaRepo = null,
        IConsentRepository? consentRepo = null,
        IProcedureRegistry? procRegistry = null,
        IProcedureExecutionService? procService = null,
        ICatalogVisibilityService? visibilityService = null) =>
        new(
            metaRepo ?? new InMemoryTableMetadataRepository(),
            consentRepo ?? new InMemoryConsentRepository(),
            new StubGatewayExecutionService(),
            auditLogRepository: NullAuditLogRepository.Instance,
            options: Options.Create(new GatewayOptions()),
            visibilityService: visibilityService,
            procedureCatalog: new ProcedureCatalog(
                procRegistry ?? new InMemoryProcedureRegistry(),
                procService ?? new StubProcedureExecutionService()));

    private static McpToolExecutionHandler CreateHandler(
        IProcedureRegistry procRegistry,
        IProcedureExecutionService procService,
        ITableMetadataRepository? metaRepo = null) =>
        new(
            StubGovernedSqlExecutionService.Empty,
            PassiveDataQuery,
            PassiveCatalogDiscovery,
            PassiveApiDispatcher,
            new InMemoryLineageGraphStore(),
            metaRepo ?? new InMemoryTableMetadataRepository(),
            procedureCatalog: new ProcedureCatalog(procRegistry, procService),
            procedureExecutionService: procService);

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_SEC_GOV153_01_AntiEnumeration_DescribeDataset_Returns404Parity()
    {
        // SEC-GOV153-01: An unconsented/invisible procedure and an unknown procedure must answer with identical 404 parity.
        var procUnconsented = CreateProcedure("ProcUnconsented");
        var procRegistry = CreateRegistry(procUnconsented);
        var procService = new StubProcedureExecutionService(isVisible: (_, _, _) => false);
        var catalog = CreateCatalog(procRegistry: procRegistry, procService: procService);

        var executor = new GatewayMcpQueryExecutor(
            PassiveRequestExecutorProvider,
            new StubGatewayExecutionService(),
            NullLogger<GatewayMcpQueryExecutor>.Instance, Autheris.Tests.Unit.Mcp.McpRowCapTests.Options(100),
            datasetCatalog: catalog, persistedToolValidator: new Autheris.Application.Mcp.Services.PersistedToolValidator());

        var tool = McpDatasetTools.Definitions().First(t => t.Name == McpDatasetTools.DescribeDataset);
        var session = CreateSession();

        // 1. Describe unconsented procedure
        var resultUnconsented = await executor.ExecuteOperationAsync(tool, JsonSerializer.Serialize(new { dataset = "procedure:ProcUnconsented" }), session);

        // 2. Describe non-existent procedure
        var resultNonExistent = await executor.ExecuteOperationAsync(tool, JsonSerializer.Serialize(new { dataset = "procedure:ProcNonExistent" }), session);

        // Byte-exact error status and payload check
        resultUnconsented.ShouldBe(resultNonExistent);
        resultUnconsented.ShouldContain("\"code\":\"NOT_FOUND\"");
        resultUnconsented.ShouldContain("Dataset not found or not visible.");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_SEC_GOV153_02_AntiEnumeration_ListAndSearch_Omission()
    {
        // SEC-GOV153-01 / SEC-GOV153-02: Invisible procedures are completely omitted from list_datasets and search_catalog,
        // and pagination total reflects only visible items.
        var tableOrder = CreateTableMetadata(OrdersTableId, ("id", "int", false), ("amount", "decimal", false));
        var metaRepo = new InMemoryTableMetadataRepository(tableOrder);
        var procVisible = CreateProcedure("ProcVisible");
        var procDenied = CreateProcedure("ProcDenied");
        var procRegistry = CreateRegistry(procVisible, procDenied);
        var procService = new StubProcedureExecutionService(
            isVisible: (p, _, _) => string.Equals(p.Definition.Name, "ProcVisible", StringComparison.Ordinal));
        var visibility = new StubCatalogVisibilityService(tableOrder);
        var catalog = CreateCatalog(metaRepo, procRegistry: procRegistry, procService: procService, visibilityService: visibility);

        var user = CreatePrincipal();

        // List datasets
        var listResult = await catalog.ListDatasetsAsync(user, search: null, domain: null, CancellationToken.None);

        // Denied procedure must be completely absent
        listResult.Datasets.Any(d => string.Equals(d.Id, "procedure:ProcDenied", StringComparison.OrdinalIgnoreCase)).ShouldBeFalse();
        listResult.Datasets.Any(d => string.Equals(d.Id, "procedure:ProcVisible", StringComparison.OrdinalIgnoreCase)).ShouldBeTrue();
        listResult.Datasets.Any(d => d.Id == McpDatasetCatalog.DatasetId(OrdersTableId)).ShouldBeTrue();

        // Total count must be 2 (table + 1 visible procedure), not 3!
        listResult.Total.ShouldBe(2);

        // Catalog Discovery Search
        var discoveryService = new CatalogDiscoveryService(
            metaRepo,
            AuditWriterTestServices.Over(NullAuditLogRepository.Instance),
            Options.Create(new GatewayOptions()),
            visibilityService: visibility,
            procedureCatalog: new ProcedureCatalog(procRegistry, procService));

        var reqContext = Autheris.Domain.Model.RequestContext.FromCaller(new CallerSecurityContext(new Sid("S-1-USER"), [], ["DataAnalyst"], new TenantId(TenantA), false, false));
        reqContext = reqContext with { User = user };

        var searchDetailed = await discoveryService.SearchCatalogDetailedAsync(
            new CatalogSearchQuery("ProcDenied"),
            reqContext);

        searchDetailed.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_SEC_GOV153_03_CallerAwareMasking_StrippedColumnsOmitted_MaskedMarked()
    {
        // SEC-GOV153-03: In describe_dataset, denied columns are completely omitted, masked columns marked "mask", clear marked "clear".
        var tableCustomers = CreateTableMetadata(
            CustomersTableId,
            ("id", "int", false),
            ("email", "varchar", true),
            ("ssn", "varchar", true));
        var metaRepo = new InMemoryTableMetadataRepository(tableCustomers);
        var proc = CreateProcedure("GetCustomerProfile", resultTable: "sales.dbo.customers");
        var procRegistry = CreateRegistry(proc);
        var procService = new StubProcedureExecutionService(isVisible: (_, _, _) => true);

        // Active consent: id=Clear, email=Mask (ssn is not granted -> Denied)
        var consentRepo = new InMemoryConsentRepository(new Consent
        {
            TableIdentifier = CustomersTableId,
            TenantId = new TenantId(TenantA),
            Effect = ConsentEffect.Allow,
            GranteeSid = new Sid("S-1-USER"),
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(1),
            ColumnRules =
            [
                new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear },
                new ConsentColumnRule { ColumnName = "email", AccessLevel = ColumnAccessLevel.Mask }
            ]
        });

        var catalog = CreateCatalog(metaRepo, consentRepo, procRegistry, procService);

        var user = CreatePrincipal();
        var desc = await catalog.DescribeDatasetAsync(user, "procedure:GetCustomerProfile");

        desc.Kind.ShouldBe("procedure");
        desc.Id.ShouldBe("procedure:GetCustomerProfile");

        // Denied column "ssn" must be completely omitted!
        desc.Columns.Any(c => c.Name.Equals("ssn", StringComparison.OrdinalIgnoreCase)).ShouldBeFalse();

        // "email" must have access = "mask"
        var emailCol = desc.Columns.FirstOrDefault(c => c.Name.Equals("email", StringComparison.OrdinalIgnoreCase));
        emailCol.ShouldNotBeNull();
        emailCol.Access.ShouldBe("mask");

        // "id" must have access = "clear"
        var idCol = desc.Columns.FirstOrDefault(c => c.Name.Equals("id", StringComparison.OrdinalIgnoreCase));
        idCol.ShouldNotBeNull();
        idCol.Access.ShouldBe("clear");

        // Parameters must be present
        desc.Parameters.ShouldNotBeNull();
        desc.Parameters.Count.ShouldBe(1);
        desc.Parameters[0].Name.ShouldBe("customerId");
        desc.Parameters[0].ClrType.ShouldBe("Int32");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_SEC_GOV153_04_CallProcedure_ReadOnlyToken_PermittedForRead_ForbiddenForWrite()
    {
        // SEC-GOV153-02: Read-only token succeeds for read procedures, rejected with 403 on write procedures.
        var readProc = CreateProcedure("ProcRead", mode: ProcedureMode.Read);
        var writeProc = CreateProcedure("ProcWrite", mode: ProcedureMode.Write);
        var procRegistry = CreateRegistry(readProc, writeProc);
        var procService = new StubProcedureExecutionService(
            isVisible: (_, _, _) => true,
            execute: name => name == "ProcRead"
                ? new GovernedProcedureResult(["id"], [new Dictionary<string, object?> { ["id"] = 100 }], 1, false, 10)
                : throw new InvalidOperationException("Unexpected execution"));

        var handler = CreateHandler(procRegistry, procService);

        var tool = McpDatasetTools.Definitions().First(t => t.Name == McpDatasetTools.CallProcedure);
        var readOnlySession = CreateSession(isReadOnly: true);

        // 1. Invoking read procedure with read-only session must succeed
        var readResultJson = await handler.ExecuteToolAsync(
            tool,
            JsonSerializer.Serialize(new { procedure_name = "ProcRead" }),
            readOnlySession);

        readResultJson.ShouldContain("\"rowCount\":1");
        readResultJson.ShouldNotContain("\"isError\":true");

        // 2. Invoking write procedure with read-only session must be forbidden (403)
        var writeResultJson = await handler.ExecuteToolAsync(
            tool,
            JsonSerializer.Serialize(new { procedure_name = "ProcWrite" }),
            readOnlySession);

        writeResultJson.ShouldContain("\"isError\":true");
        writeResultJson.ShouldContain("\"code\":\"FORBIDDEN\"");
        writeResultJson.ShouldContain("read access");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_SEC_GOV153_05_CallProcedure_RowClampingAndTruncationFlag()
    {
        // SEC-GOV153-04: max_rows is clamped [1, 1000] (default 100) and marks truncated: true.
        var proc = CreateProcedure("ProcLarge");
        var procRegistry = CreateRegistry(proc);

        // Simulate procedure returning 120 rows
        var rows120 = Enumerable.Range(1, 120)
            .Select(i => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?> { ["id"] = i })
            .ToList();

        var procService = new StubProcedureExecutionService(
            isVisible: (_, _, _) => true,
            execute: _ => new GovernedProcedureResult(["id"], rows120, 120, false, 15));

        var handler = CreateHandler(procRegistry, procService);

        var tool = McpDatasetTools.Definitions().First(t => t.Name == McpDatasetTools.CallProcedure);
        var session = CreateSession();

        // Execute with max_rows = 50
        var resultJson = await handler.ExecuteToolAsync(
            tool,
            JsonSerializer.Serialize(new { procedure_name = "ProcLarge", max_rows = 50 }),
            session);

        using var doc = JsonDocument.Parse(resultJson);
        var root = doc.RootElement;
        root.GetProperty("rowCount").GetInt32().ShouldBe(50);
        root.GetProperty("truncated").GetBoolean().ShouldBeTrue();
        root.GetProperty("rows").GetArrayLength().ShouldBe(50);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_SEC_GOV153_06_CallProcedure_AntiEnumeration_ExecutionDenialParity()
    {
        // SEC-GOV153-01 / SEC-GOV153-02: Executing unconsented procedure returns identical 404 to non-existent procedure.
        var procUnconsented = CreateProcedure("ProcUnconsented");
        var procRegistry = CreateRegistry(procUnconsented);
        // the executor's own visibility stage answers a hidden procedure like an unknown one
        var procService = new StubProcedureExecutionService(
            isVisible: (_, _, _) => false,
            execute: _ => throw new KeyNotFoundException("Procedure endpoint not found."));

        var handler = CreateHandler(procRegistry, procService);

        var tool = McpDatasetTools.Definitions().First(t => t.Name == McpDatasetTools.CallProcedure);
        var session = CreateSession();

        var resultUnconsented = await handler.ExecuteToolAsync(
            tool,
            JsonSerializer.Serialize(new { procedure_name = "ProcUnconsented" }),
            session);

        var resultNonExistent = await handler.ExecuteToolAsync(
            tool,
            JsonSerializer.Serialize(new { procedure_name = "ProcNonExistent" }),
            session);

        resultUnconsented.ShouldBe(resultNonExistent);
        resultUnconsented.ShouldContain("\"code\":\"NOT_FOUND\"");
        resultUnconsented.ShouldContain("Procedure endpoint not found.");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_SEC_GOV153_07_FailClosed_ProceduresWithoutResultTable_HiddenFromCatalog()
    {
        // SEC-GOV153-01 / D-153-1: Procedures without ResultTable or in write mode fail closed and are hidden from catalog.
        var procNoTable = CreateProcedure("ProcNoTable", resultTable: null);
        var procWrite = CreateProcedure("ProcWrite", mode: ProcedureMode.Write);
        var procRegistry = CreateRegistry(procNoTable, procWrite);
        var procService = new StubProcedureExecutionService();
        var visibility = new StubCatalogVisibilityService();
        var catalog = CreateCatalog(procRegistry: procRegistry, procService: procService, visibilityService: visibility);

        var user = CreatePrincipal();

        // 1. List datasets must be empty
        var list = await catalog.ListDatasetsAsync(user);
        list.Datasets.ShouldBeEmpty();
        list.Total.ShouldBe(0);

        // 2. Describe dataset must fail closed with TableNotFoundException
        await Should.ThrowAsync<TableNotFoundException>(async () =>
            await catalog.DescribeDatasetAsync(user, "procedure:ProcNoTable"));

        await Should.ThrowAsync<TableNotFoundException>(async () =>
            await catalog.DescribeDatasetAsync(user, "procedure:ProcWrite"));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_SEC_GOV153_08_TenantIsolation_CrossTenantProcedureLeakageBlocked()
    {
        // SEC-GOV153-01 / SEC-GOV153-08: Procedures strictly evaluate visibility against caller's tenant context.
        var procTenantA = CreateProcedure("ProcTenantA");
        var procRegistry = CreateRegistry(procTenantA);
        var procService = new StubProcedureExecutionService(
            isVisible: (_, _, t) => t.Value == TenantA,
            execute: _ => throw new KeyNotFoundException("Procedure endpoint not found."));
        var visibility = new StubCatalogVisibilityService();
        var catalog = CreateCatalog(procRegistry: procRegistry, procService: procService, visibilityService: visibility);

        var callerTenantB = CreatePrincipal(tenant: TenantB);

        // 1. Omitted from list_datasets for Tenant B
        var listTenantB = await catalog.ListDatasetsAsync(callerTenantB);
        listTenantB.Datasets.ShouldBeEmpty();
        listTenantB.Total.ShouldBe(0);

        // 2. DescribeDataset fails closed for Tenant B
        await Should.ThrowAsync<TableNotFoundException>(async () =>
            await catalog.DescribeDatasetAsync(callerTenantB, "procedure:ProcTenantA"));

        // 3. MCP Execution fails closed for Tenant B
        var handler = CreateHandler(procRegistry, procService);

        var tool = McpDatasetTools.Definitions().First(t => t.Name == McpDatasetTools.CallProcedure);
        var sessionTenantB = CreateSession(tenant: TenantB);

        var execResult = await handler.ExecuteToolAsync(
            tool,
            JsonSerializer.Serialize(new { procedure_name = "ProcTenantA" }),
            sessionTenantB);

        execResult.ShouldContain("\"code\":\"NOT_FOUND\"");
        execResult.ShouldContain("Procedure endpoint not found.");
    }

    private sealed class StubProcedureExecutionService(
        Func<RegisteredProcedure, ClaimsPrincipal, TenantId, bool>? isVisible = null,
        Func<string, GovernedProcedureResult>? execute = null) : IProcedureExecutionService
    {
        public Task<GovernedProcedureResult> ExecuteAsync(
            string name,
            IReadOnlyDictionary<string, object?>? rawInputs,
            ClaimsPrincipal user,
            TenantId tenantId,
            CancellationToken ct = default)
            => execute is not null
                ? Task.FromResult(execute(name))
                : Task.FromResult(new GovernedProcedureResult([], [], 0, false, 0));

        public Task<bool> IsVisibleAsync(RegisteredProcedure procedure, ClaimsPrincipal user, TenantId tenantId, CancellationToken ct = default)
            => Task.FromResult(isVisible?.Invoke(procedure, user, tenantId) ?? false);
    }

    private sealed class StubCatalogVisibilityService(params TableMetadata[] tables) : ICatalogVisibilityService
    {
        public ValueTask<CatalogVisibleSet> GetVisibleAsync(CatalogCaller caller, SchemaContractDefinition? contract = null, CancellationToken ct = default)
            => ValueTask.FromResult(new CatalogVisibleSet(tables, CallerScope.Tenant, false));

        public ValueTask<CatalogVisibleRow?> FindVisibleAsync(CatalogCaller caller, TableIdentifier table, CancellationToken ct = default)
        {
            var found = tables.FirstOrDefault(t => t.Identifier == table);
            return ValueTask.FromResult<CatalogVisibleRow?>(found is null ? null : new CatalogVisibleRow(found, Autheris.Application.Services.CatalogOutputShape.Full));
        }

        public ValueTask<IReadOnlySet<TableIdentifier>> FilterVisibleAsync(CatalogCaller caller, IReadOnlyCollection<TableIdentifier> candidates, CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlySet<TableIdentifier>>(tables.Select(t => t.Identifier).Where(candidates.Contains).ToHashSet());
    }
}
