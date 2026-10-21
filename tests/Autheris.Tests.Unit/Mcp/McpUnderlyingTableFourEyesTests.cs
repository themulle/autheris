namespace Autheris.Tests.Unit.Mcp;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Dbt.Services;
using Autheris.Application.Interfaces;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Procedures.Services;
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
/// The four-eyes / inactive gate of call_procedure and query_metrics runs on the real tables behind the call (procedure registry,
/// semantic model base table), not on the pseudo ABAC object (procedure.dbo.&lt;name&gt;, the semantic catalog). Unresolvable is fail-closed.
/// </summary>
public sealed class McpUnderlyingTableFourEyesTests
{
    private static readonly TableIdentifier Secret = new("sales", "dbo", "secret");
    private static readonly TableIdentifier Pub = new("sales", "dbo", "pub");
    private static readonly TableIdentifier Unknown = new("sales", "dbo", "unknown");
    private static readonly TableIdentifier Inactive = new("sales", "dbo", "inactive");

    private sealed class NoDemo : IDemoDataSwitch
    {
        public bool Enabled => false;
    }

    private static ITableMetadataRepository MetadataRepo()
    {
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var id = ci.Arg<TableIdentifier>();
                if (id.Equals(Secret))
                {
                    return new TableMetadata { Identifier = id, Table = new Table { TableName = "secret", RequiresFourEyes = true, IsActive = true } };
                }

                if (id.Equals(Inactive))
                {
                    return new TableMetadata { Identifier = id, Table = new Table { TableName = "inactive", IsActive = false } };
                }

                return id.Equals(Pub)
                    ? new TableMetadata { Identifier = id, Table = new Table { TableName = "pub", IsActive = true } }
                    : null;
            });
        repo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<TableMetadata>());
        return repo;
    }

    private static IMcpQueryExecutor Executor()
    {
        var executor = Substitute.For<IMcpQueryExecutor>();
        executor.ExecuteOperationAsync(Arg.Any<McpToolDefinition>(), Arg.Any<string>(), Arg.Any<McpSessionContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("""{"rows":[]}"""));
        return executor;
    }

    private static AiDataGuardrailService Guardrail(
        IMcpQueryExecutor executor,
        InMemoryProcedureRegistry? procedures = null,
        InMemorySemanticCatalog? semanticCatalog = null,
        bool withMetadata = true) =>
        new(
            new McpToolRegistry(Options.Create(new GatewayOptions()), new NoDemo()),
            Options.Create(new GatewayOptions { Mcp = new McpOptions { Enabled = true } }),
            NullLogger<AiDataGuardrailService>.Instance,
            global::Autheris.Tests.Unit.TestDoubles.NullAuditLogRepository.Instance,
            queryExecutor: executor,
            tableMetadataRepository: withMetadata ? MetadataRepo() : null,
            procedureRegistry: procedures,
            semanticCatalog: semanticCatalog);

    private static McpSessionContext Session() =>
        new("s1", "agent", "tenant-a", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, UserSid: "S-1-USER", Roles: ["DataAnalyst"]);

    private static InMemoryProcedureRegistry Procedure(string name, string? resultTable, IReadOnlyList<string>? referencedTables = null)
    {
        var definition = new ProcedureDefinition(
            Name: name,
            Summary: name,
            ProcedureName: $"usp_{name}",
            Mode: ProcedureMode.Read,
            DataSource: "sales",
            Parameters: [],
            ContextBindings: [],
            RlsMode: ProcedureRlsMode.SessionContext,
            ResultTable: resultTable,
            ClearedResultColumns: [],
            RequiredRoles: [],
            AllowDynamicSql: false,
            TimeoutSeconds: 30);

        var registry = new InMemoryProcedureRegistry();
        registry.Register(definition);
        registry.MarkActive(name, new ProcedureValidationResult(true, [], [], referencedTables ?? [], new Dictionary<string, string>()));
        return registry;
    }

    private static ValueTask<McpToolCallResult> CallProcedure(AiDataGuardrailService guardrail, string args) =>
        guardrail.ExecuteToolWithGuardrailAsync(new McpToolCallRequest(McpDatasetTools.CallProcedure, args), Session());

    [Theory]
    [InlineData("dbo.secret", null)]
    [InlineData("dbo.pub", "dbo.secret")]
    public async Task CallProcedure_FourEyesTableBehindTheProcedure_NeedsApproval(string resultTable, string? referencedTable)
    {
        var executor = Executor();
        var guardrail = Guardrail(executor, Procedure("GetData", resultTable, referencedTable == null ? null : [referencedTable]));

        var result = await CallProcedure(guardrail, """{"procedure_name":"GetData"}""");

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage!.ShouldContain("Four-Eyes");
        await executor.DidNotReceiveWithAnyArgs().ExecuteOperationAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task CallProcedure_OnlyPlainTables_IsAllowed()
    {
        var executor = Executor();
        var guardrail = Guardrail(executor, Procedure("GetData", "dbo.pub", ["dbo.pub"]));

        var result = await CallProcedure(guardrail, """{"procedure_name":"procedure:GetData"}""");

        result.IsSuccess.ShouldBeTrue(result.ErrorMessage);
        await executor.ReceivedWithAnyArgs(1).ExecuteOperationAsync(default!, default!, default!, default);
    }

    [Theory]
    [InlineData("dbo.unknown")]
    [InlineData("dbo.inactive")]
    public async Task CallProcedure_TableWithoutMetadataOrInactive_FailsClosed(string resultTable)
    {
        var executor = Executor();
        var guardrail = Guardrail(executor, Procedure("GetData", resultTable));

        var result = await CallProcedure(guardrail, """{"procedure_name":"GetData"}""");

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage!.ShouldContain("not accessible");
        await executor.DidNotReceiveWithAnyArgs().ExecuteOperationAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task CallProcedure_WithoutProcedureRegistry_FailsClosed()
    {
        var executor = Executor();

        var result = await CallProcedure(Guardrail(executor), """{"procedure_name":"GetData"}""");

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage!.ShouldContain("not accessible");
        await executor.DidNotReceiveWithAnyArgs().ExecuteOperationAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task CallProcedure_AliasArgument_DoesNotRedirectTheCheck()
    {
        // The executor runs "procedure_name"; an alias argument naming a harmless procedure must not be what the gate checks.
        var executor = Executor();
        var guardrail = Guardrail(executor, Procedure("GetSecret", "dbo.secret"));

        var result = await CallProcedure(guardrail, """{"procedure":"Harmless","procedure_name":"GetSecret"}""");

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage!.ShouldContain("Four-Eyes");
    }

    [Fact]
    public async Task CallProcedure_RepeatedProcedureName_FailsClosed()
    {
        var executor = Executor();
        var guardrail = Guardrail(executor, Procedure("GetSecret", "dbo.secret"));

        var result = await CallProcedure(guardrail, """{"procedure_name":"GetSecret","Procedure_Name":"Other"}""");

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage!.ShouldContain("could not be resolved");
        await executor.DidNotReceiveWithAnyArgs().ExecuteOperationAsync(default!, default!, default!, default);
    }

    private static readonly DbtMetric Revenue = new(
        Name: "revenue",
        Type: "simple",
        Description: "Revenue",
        Label: "Revenue",
        Filter: null,
        TypeParams: null,
        Dimensions: ["region"],
        Measures: ["revenue_sum"],
        Meta: new Dictionary<string, string>());

    private static async Task<InMemorySemanticCatalog> CatalogOver(TableIdentifier baseTable)
    {
        var model = new DbtSemanticModel(
            Name: "orders",
            Description: "Orders",
            NodeRelation: baseTable.ToString(),
            Entities: [],
            Measures: [new DbtMeasure("revenue_sum", "sum", "amount", "Revenue")],
            Dimensions: [new DbtDimension("region", "categorical", "Region")]);
        var catalog = new InMemorySemanticCatalog();
        await catalog.RegisterManifestAsync(new DbtSemanticManifest([model], [Revenue], "1.0"), new TenantId("tenant-a"));
        return catalog;
    }

    private static ValueTask<McpToolCallResult> QueryMetrics(AiDataGuardrailService guardrail, string args) =>
        guardrail.ExecuteToolWithGuardrailAsync(new McpToolCallRequest(McpSemanticTools.QueryMetrics, args), Session());

    [Fact]
    public async Task QueryMetrics_FourEyesBaseTable_NeedsApproval()
    {
        var executor = Executor();
        var guardrail = Guardrail(executor, semanticCatalog: await CatalogOver(Secret));

        var result = await QueryMetrics(guardrail, """{"metrics":["revenue"]}""");

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage!.ShouldContain("Four-Eyes");
        await executor.DidNotReceiveWithAnyArgs().ExecuteOperationAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task QueryMetrics_PlainBaseTable_IsAllowed()
    {
        var executor = Executor();
        var guardrail = Guardrail(executor, semanticCatalog: await CatalogOver(Pub));

        var result = await QueryMetrics(guardrail, """{"metric":"revenue"}""");

        result.IsSuccess.ShouldBeTrue(result.ErrorMessage);
    }

    [Theory]
    [InlineData("""{"metrics":["unknown_metric"]}""")]
    [InlineData("""{"metrics":["revenue","unknown_metric"]}""")]
    public async Task QueryMetrics_UnresolvableMetric_FailsClosed(string args)
    {
        var executor = Executor();
        var guardrail = Guardrail(executor, semanticCatalog: await CatalogOver(Pub));

        var result = await QueryMetrics(guardrail, args);

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage!.ShouldContain("not accessible");
        await executor.DidNotReceiveWithAnyArgs().ExecuteOperationAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task QueryMetrics_BaseTableWithoutMetadata_FailsClosed()
    {
        var executor = Executor();
        var guardrail = Guardrail(executor, semanticCatalog: await CatalogOver(Unknown));

        var result = await QueryMetrics(guardrail, """{"metrics":["revenue"]}""");

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage!.ShouldContain("not accessible");
    }

    [Fact]
    public async Task QueryMetrics_WithoutSemanticCatalog_FailsClosed()
    {
        var executor = Executor();

        var result = await QueryMetrics(Guardrail(executor), """{"metrics":["revenue"]}""");

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage!.ShouldContain("not accessible");
    }
}
