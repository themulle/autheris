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
using Autheris.Application.Data.Interfaces;
using Autheris.Application.Governance.Contracts;
using Autheris.Application.Interfaces;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Procedures.Interfaces;
using Autheris.Application.Sql.Interfaces;
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
            CatalogView = CatalogView.Full,
            Table = new Table { TableName = id.TableName, SchemaName = id.Schema, Sensitivity = "INTERNAL", Description = $"{id.TableName} table" },
            Columns = columns.Select(c => new TableColumn { ColumnName = c.Name, DataType = c.Type, IsSensitive = c.Sensitive, Description = c.Name }).ToList()
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
            AllowDynamicSql: false,
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

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_SEC_GOV153_01_AntiEnumeration_DescribeDataset_Returns404Parity()
    {
        // SEC-GOV153-01: An unconsented/invisible procedure and an unknown procedure must answer with identical 404 parity.
        var metaRepo = Substitute.For<ITableMetadataRepository>();
        var consentRepo = Substitute.For<IConsentRepository>();
        var gatewayExec = Substitute.For<IGatewayExecutionService>();
        var procRegistry = Substitute.For<IProcedureRegistry>();
        var procService = Substitute.For<IProcedureExecutionService>();

        var procUnconsented = CreateProcedure("ProcUnconsented");
        procRegistry.TryGet("ProcUnconsented", out Arg.Any<RegisteredProcedure?>())
            .Returns(x => { x[1] = procUnconsented; return true; });
        procRegistry.TryGet("ProcNonExistent", out Arg.Any<RegisteredProcedure?>())
            .Returns(x => { x[1] = null; return false; });

        // Unconsented returns false for visibility
        procService.IsVisibleAsync(procUnconsented, Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var catalog = new McpDatasetCatalog(
            metaRepo, consentRepo, gatewayExec,
            auditLogRepository: global::Autheris.Tests.Unit.TestDoubles.NullAuditLogRepository.Instance,
            options: Options.Create(new GatewayOptions()),
            procedureRegistry: procRegistry,
            procedureExecutionService: procService);

        var executor = new GatewayMcpQueryExecutor(
            Substitute.For<IRequestExecutorProvider>(),
            gatewayExec,
            NullLogger<GatewayMcpQueryExecutor>.Instance,
            datasetCatalog: catalog);

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
        var metaRepo = Substitute.For<ITableMetadataRepository>();
        var consentRepo = Substitute.For<IConsentRepository>();
        var gatewayExec = Substitute.For<IGatewayExecutionService>();
        var procRegistry = Substitute.For<IProcedureRegistry>();
        var procService = Substitute.For<IProcedureExecutionService>();

        var tableOrder = CreateTableMetadata(OrdersTableId, ("id", "int", false), ("amount", "decimal", false));
        metaRepo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<TableMetadata> { tableOrder });
        metaRepo.GetTableMetadataAsync(OrdersTableId, Arg.Any<CancellationToken>())
            .Returns(tableOrder);

        var procVisible = CreateProcedure("ProcVisible");
        var procDenied = CreateProcedure("ProcDenied");

        procRegistry.GetAll().Returns([procVisible, procDenied]);

        procService.IsVisibleAsync(procVisible, Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(true);
        procService.IsVisibleAsync(procDenied, Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var visibility = Substitute.For<ICatalogVisibilityService>();
        visibility.GetVisibleAsync(Arg.Any<CatalogCaller>(), Arg.Any<SchemaContractDefinition?>(), Arg.Any<CancellationToken>())
            .Returns(new CatalogVisibleSet([tableOrder], CallerScope.Tenant, false));

        var catalog = new McpDatasetCatalog(
            metaRepo, consentRepo, gatewayExec,
            auditLogRepository: global::Autheris.Tests.Unit.TestDoubles.NullAuditLogRepository.Instance,
            options: Options.Create(new GatewayOptions()),
            visibilityService: visibility,
            procedureRegistry: procRegistry,
            procedureExecutionService: procService);

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
            global::Autheris.Tests.Unit.TestDoubles.NullAuditLogRepository.Instance,
            Options.Create(new GatewayOptions()),
            visibilityService: visibility,
            procedureRegistry: procRegistry,
            procedureExecutionService: procService);

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
        var metaRepo = Substitute.For<ITableMetadataRepository>();
        var consentRepo = Substitute.For<IConsentRepository>();
        var gatewayExec = Substitute.For<IGatewayExecutionService>();
        var procRegistry = Substitute.For<IProcedureRegistry>();
        var procService = Substitute.For<IProcedureExecutionService>();

        var tableCustomers = CreateTableMetadata(
            CustomersTableId,
            ("id", "int", false),
            ("email", "varchar", true),
            ("ssn", "varchar", true));

        metaRepo.GetTableMetadataAsync(CustomersTableId, Arg.Any<CancellationToken>())
            .Returns(tableCustomers);

        var proc = CreateProcedure("GetCustomerProfile", resultTable: "sales.dbo.customers");
        procRegistry.TryGet("GetCustomerProfile", out Arg.Any<RegisteredProcedure?>())
            .Returns(x => { x[1] = proc; return true; });

        procService.IsVisibleAsync(proc, Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(true);

        // Active consent: id=Clear, email=Mask (ssn is not granted -> Denied)
        consentRepo.GetAllActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns([
                new Consent
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
                }
            ]);

        var catalog = new McpDatasetCatalog(
            metaRepo, consentRepo, gatewayExec,
            auditLogRepository: global::Autheris.Tests.Unit.TestDoubles.NullAuditLogRepository.Instance,
            options: Options.Create(new GatewayOptions()),
            procedureRegistry: procRegistry,
            procedureExecutionService: procService);

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
        var sqlExec = Substitute.For<IGovernedSqlExecutionService>();
        var dataQuery = Substitute.For<IGovernedDataQueryService>();
        var catalogDiscovery = Substitute.For<ICatalogDiscoveryService>();
        var apiDispatcher = Substitute.For<IApiDispatcherService>();
        var lineage = Substitute.For<ILineageGraphStore>();
        var metaRepo = Substitute.For<ITableMetadataRepository>();
        var procRegistry = Substitute.For<IProcedureRegistry>();
        var procService = Substitute.For<IProcedureExecutionService>();

        var readProc = CreateProcedure("ProcRead", mode: ProcedureMode.Read);
        var writeProc = CreateProcedure("ProcWrite", mode: ProcedureMode.Write);

        procRegistry.TryGet("ProcRead", out Arg.Any<RegisteredProcedure?>())
            .Returns(x => { x[1] = readProc; return true; });
        procRegistry.TryGet("ProcWrite", out Arg.Any<RegisteredProcedure?>())
            .Returns(x => { x[1] = writeProc; return true; });

        procService.IsVisibleAsync(readProc, Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(true);
        procService.IsVisibleAsync(writeProc, Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(true);

        procService.ExecuteAsync("ProcRead", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(new GovernedProcedureResult(["id"], [new Dictionary<string, object?> { ["id"] = 100 }], 1, false, 10));

        var handler = new McpToolExecutionHandler(
            sqlExec, dataQuery, catalogDiscovery, apiDispatcher, lineage, metaRepo,
            procedureRegistry: procRegistry,
            procedureExecutionService: procService);

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
        var sqlExec = Substitute.For<IGovernedSqlExecutionService>();
        var dataQuery = Substitute.For<IGovernedDataQueryService>();
        var catalogDiscovery = Substitute.For<ICatalogDiscoveryService>();
        var apiDispatcher = Substitute.For<IApiDispatcherService>();
        var lineage = Substitute.For<ILineageGraphStore>();
        var metaRepo = Substitute.For<ITableMetadataRepository>();
        var procRegistry = Substitute.For<IProcedureRegistry>();
        var procService = Substitute.For<IProcedureExecutionService>();

        var proc = CreateProcedure("ProcLarge");
        procRegistry.TryGet("ProcLarge", out Arg.Any<RegisteredProcedure?>())
            .Returns(x => { x[1] = proc; return true; });
        procService.IsVisibleAsync(proc, Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(true);

        // Simulate procedure returning 120 rows
        var rows120 = Enumerable.Range(1, 120)
            .Select(i => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?> { ["id"] = i })
            .ToList();

        procService.ExecuteAsync("ProcLarge", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(new GovernedProcedureResult(["id"], rows120, 120, false, 15));

        var handler = new McpToolExecutionHandler(
            sqlExec, dataQuery, catalogDiscovery, apiDispatcher, lineage, metaRepo,
            procedureRegistry: procRegistry,
            procedureExecutionService: procService);

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
        var sqlExec = Substitute.For<IGovernedSqlExecutionService>();
        var dataQuery = Substitute.For<IGovernedDataQueryService>();
        var catalogDiscovery = Substitute.For<ICatalogDiscoveryService>();
        var apiDispatcher = Substitute.For<IApiDispatcherService>();
        var lineage = Substitute.For<ILineageGraphStore>();
        var metaRepo = Substitute.For<ITableMetadataRepository>();
        var procRegistry = Substitute.For<IProcedureRegistry>();
        var procService = Substitute.For<IProcedureExecutionService>();

        var procUnconsented = CreateProcedure("ProcUnconsented");
        procRegistry.TryGet("ProcUnconsented", out Arg.Any<RegisteredProcedure?>())
            .Returns(x => { x[1] = procUnconsented; return true; });
        procRegistry.TryGet("ProcNonExistent", out Arg.Any<RegisteredProcedure?>())
            .Returns(x => { x[1] = null; return false; });

        procService.IsVisibleAsync(procUnconsented, Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(false);
        procService.ExecuteAsync("ProcNonExistent", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns<GovernedProcedureResult>(_ => throw new KeyNotFoundException("Procedure endpoint not found."));

        var handler = new McpToolExecutionHandler(
            sqlExec, dataQuery, catalogDiscovery, apiDispatcher, lineage, metaRepo,
            procedureRegistry: procRegistry,
            procedureExecutionService: procService);

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
        var metaRepo = Substitute.For<ITableMetadataRepository>();
        var consentRepo = Substitute.For<IConsentRepository>();
        var gatewayExec = Substitute.For<IGatewayExecutionService>();
        var procRegistry = Substitute.For<IProcedureRegistry>();
        var procService = Substitute.For<IProcedureExecutionService>();

        var procNoTable = CreateProcedure("ProcNoTable", resultTable: null);
        var procWrite = CreateProcedure("ProcWrite", mode: ProcedureMode.Write);

        procRegistry.GetAll().Returns([procNoTable, procWrite]);
        procRegistry.TryGet("ProcNoTable", out Arg.Any<RegisteredProcedure?>()).Returns(x => { x[1] = procNoTable; return true; });
        procRegistry.TryGet("ProcWrite", out Arg.Any<RegisteredProcedure?>()).Returns(x => { x[1] = procWrite; return true; });

        var visibility = Substitute.For<ICatalogVisibilityService>();
        visibility.GetVisibleAsync(Arg.Any<CatalogCaller>(), Arg.Any<SchemaContractDefinition?>(), Arg.Any<CancellationToken>())
            .Returns(new CatalogVisibleSet([], CallerScope.Tenant, false));

        var catalog = new McpDatasetCatalog(
            metaRepo, consentRepo, gatewayExec,
            auditLogRepository: global::Autheris.Tests.Unit.TestDoubles.NullAuditLogRepository.Instance,
            options: Options.Create(new GatewayOptions()),
            visibilityService: visibility,
            procedureRegistry: procRegistry,
            procedureExecutionService: procService);

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
        var metaRepo = Substitute.For<ITableMetadataRepository>();
        var consentRepo = Substitute.For<IConsentRepository>();
        var gatewayExec = Substitute.For<IGatewayExecutionService>();
        var procRegistry = Substitute.For<IProcedureRegistry>();
        var procService = Substitute.For<IProcedureExecutionService>();

        var procTenantA = CreateProcedure("ProcTenantA");
        procRegistry.GetAll().Returns([procTenantA]);
        procRegistry.TryGet("ProcTenantA", out Arg.Any<RegisteredProcedure?>()).Returns(x => { x[1] = procTenantA; return true; });

        // Visible only for Tenant A, NOT Tenant B
        procService.IsVisibleAsync(procTenantA, Arg.Any<ClaimsPrincipal>(), Arg.Is<TenantId>(t => t.Value == TenantA), Arg.Any<CancellationToken>())
            .Returns(true);
        procService.IsVisibleAsync(procTenantA, Arg.Any<ClaimsPrincipal>(), Arg.Is<TenantId>(t => t.Value == TenantB), Arg.Any<CancellationToken>())
            .Returns(false);

        var visibility = Substitute.For<ICatalogVisibilityService>();
        visibility.GetVisibleAsync(Arg.Any<CatalogCaller>(), Arg.Any<SchemaContractDefinition?>(), Arg.Any<CancellationToken>())
            .Returns(new CatalogVisibleSet([], CallerScope.Tenant, false));

        var catalog = new McpDatasetCatalog(
            metaRepo, consentRepo, gatewayExec,
            auditLogRepository: global::Autheris.Tests.Unit.TestDoubles.NullAuditLogRepository.Instance,
            options: Options.Create(new GatewayOptions()),
            visibilityService: visibility,
            procedureRegistry: procRegistry,
            procedureExecutionService: procService);

        var callerTenantB = CreatePrincipal(tenant: TenantB);

        // 1. Omitted from list_datasets for Tenant B
        var listTenantB = await catalog.ListDatasetsAsync(callerTenantB);
        listTenantB.Datasets.ShouldBeEmpty();
        listTenantB.Total.ShouldBe(0);

        // 2. DescribeDataset fails closed for Tenant B
        await Should.ThrowAsync<TableNotFoundException>(async () =>
            await catalog.DescribeDatasetAsync(callerTenantB, "procedure:ProcTenantA"));

        // 3. MCP Execution fails closed for Tenant B
        var handler = new McpToolExecutionHandler(
            Substitute.For<IGovernedSqlExecutionService>(),
            Substitute.For<IGovernedDataQueryService>(),
            Substitute.For<ICatalogDiscoveryService>(),
            Substitute.For<IApiDispatcherService>(),
            Substitute.For<ILineageGraphStore>(),
            metaRepo,
            procedureRegistry: procRegistry,
            procedureExecutionService: procService);

        var tool = McpDatasetTools.Definitions().First(t => t.Name == McpDatasetTools.CallProcedure);
        var sessionTenantB = CreateSession(tenant: TenantB);

        var execResult = await handler.ExecuteToolAsync(
            tool,
            JsonSerializer.Serialize(new { procedure_name = "ProcTenantA" }),
            sessionTenantB);

        execResult.ShouldContain("\"code\":\"NOT_FOUND\"");
        execResult.ShouldContain("Procedure endpoint not found.");
    }
}
