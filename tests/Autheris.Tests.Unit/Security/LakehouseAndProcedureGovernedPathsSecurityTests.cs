#pragma warning disable CA2012 // NSubstitute .Returns(...) configures a ValueTask member; the instance is not consumed here.
namespace Autheris.Tests.Unit.Security;

using Autheris.Modules.Lakehouse.Models;
using Autheris.Tests.Common.Fakes;
using Autheris.Tests.Unit.TestDoubles;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Api.Security;
using Autheris.Application.State.Caching.Interfaces;
using Autheris.Application.DataSources.Connectors;
using Autheris.Application.DataSources.Connectors.CrossDomain;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Connectors;
using Autheris.Application.Olap;
using Autheris.Application.Policy;
using Autheris.Application.Procedures.Interfaces;
using Autheris.Application.Procedures.Services;
using Autheris.Application.Security;
using Autheris.Application.Common.Serialization;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Extensions.Lakehouse.Interfaces;
using Autheris.Extensions.Lakehouse.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;
using Autheris.Tests.Unit.Procedures;

/// <summary>Review G4: E-3, D-1, D-2, D-3, D-4 and the related Low findings of the governed data paths.</summary>
[Trait("Category", "Security")]
public sealed class LakehouseAndProcedureGovernedPathsSecurityTests
{
    private static readonly TenantId Tenant1 = new("tenant-1");

    // ------------------------------------------------------------------ E-3 (Delta / Iceberg)

    private static DeltaDataFile DFile(string path, IReadOnlyDictionary<string, string>? partition = null,
        IReadOnlyDictionary<string, string>? min = null, IReadOnlyDictionary<string, string>? max = null) =>
        new(path, partition ?? new Dictionary<string, string>(), 1, 0, true, 1, min, max);

    [Fact]
    public void E3_DeltaPruner_AcceptsOnlyPartitionEqualityOrSingleValuedStatsAsOwnershipProof()
    {
        var pruner = new DeltaPartitionPruner(NullLogger<DeltaPartitionPruner>.Instance);
        var files = new List<DeltaDataFile>
        {
            DFile("partition.parquet", new Dictionary<string, string> { ["tenantId"] = "tenant-1" }),
            DFile("exact-stats.parquet", min: new Dictionary<string, string> { ["tenantId"] = "tenant-1" }, max: new Dictionary<string, string> { ["tenantId"] = "tenant-1" }),
            DFile("overlap.parquet", min: new Dictionary<string, string> { ["tenantId"] = "tenant-0" }, max: new Dictionary<string, string> { ["tenantId"] = "tenant-9" }),
            DFile("no-evidence.parquet"),
            DFile("foreign.parquet", new Dictionary<string, string> { ["tenantId"] = "tenant-2" })
        };

        var kept = pruner.PruneDataFiles(files, ["tenantId"], new Dictionary<string, string> { ["tenantId"] = "tenant-1" }, ["tenantId"]);

        kept.Select(f => f.Path).ShouldBe(["partition.parquet", "exact-stats.parquet"]);
        pruner.PruneDataFiles(files, ["tenantId"], new Dictionary<string, string>(), ["tenantId"]).ShouldBeEmpty();
    }

    [Fact]
    public void E3_IcebergPruner_OverlappingBoundsAreNotOwnershipEvidence()
    {
        var pruner = new IcebergPartitionPruner(NullLogger<IcebergPartitionPruner>.Instance);
        var files = new List<IcebergDataFile>
        {
            new("overlap.parquet", "PARQUET", new Dictionary<string, string>(), 10, 10,
                new Dictionary<string, string> { ["tenantId"] = "tenant-0" }, new Dictionary<string, string> { ["tenantId"] = "tenant-9" }),
            new("exact.parquet", "PARQUET", new Dictionary<string, string>(), 10, 10,
                new Dictionary<string, string> { ["tenantId"] = "tenant-1" }, new Dictionary<string, string> { ["tenantId"] = "tenant-1" })
        };

        var kept = pruner.PruneDataFiles(files, new Dictionary<string, string> { ["tenantId"] = "== tenant-1" }, new IcebergPartitionSpec(0, []), ["tenantId"]);

        kept.Select(f => f.FilePath).ShouldBe(["exact.parquet"]);
    }

    private static (DeltaLakeDataSourceExecutor Executor, DataSourceExecutionContext Context) NewDelta(
        string tenantColumn, IReadOnlyList<DeltaDataFile> files, bool demoData = true)
    {
        var reader = Substitute.For<IDeltaMetadataReader>();
        var snapshot = new DeltaSnapshot(
            "t", 1, 0,
            new DeltaTableMetadata("id", "t", null, "parquet",
                new DeltaSchema("struct", [new DeltaField("id", "string"), new DeltaField(tenantColumn, "string")]),
                [tenantColumn], 0, new Dictionary<string, string>()),
            files,
            new DeltaProtocol(1, 2));
        reader.LoadSnapshotAsync(Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<DeltaSnapshot>(snapshot));

        var executor = new DeltaLakeDataSourceExecutor(
            reader, new DeltaPartitionPruner(NullLogger<DeltaPartitionPruner>.Instance), StubColumnMaskingProvider.Null,
            Options.Create(new GatewayOptions()), NullLogger<DeltaLakeDataSourceExecutor>.Instance, new DemoDataSwitch(demoData));

        var tableId = new TableIdentifier("lake", "default", "delta_table");
        var metadata = new TableMetadata
        {
            Identifier = tableId,
            Table = new Table { SourceName = "lake", SchemaName = "default", TableName = "delta_table", DataSourceType = DataSourceType.LakehouseDelta },
            Columns = [new TableColumn { ColumnName = "id", DataType = "string" }, new TableColumn { ColumnName = tenantColumn, DataType = "string" }]
        };
        var context = new DataSourceExecutionContext(
            "lake", metadata, new ClaimsPrincipal(new ClaimsIdentity()),
            TableAccessDecision.Allowed(tableId, new Dictionary<string, ColumnAccessLevel>
            {
                ["id"] = ColumnAccessLevel.Clear,
                [tenantColumn] = ColumnAccessLevel.Clear
            }),
            new Dictionary<string, object?>(), new List<string> { "id", tenantColumn }, null, 1000, 0, Tenant1);
        return (executor, context);
    }

    [Fact]
    public async Task EXT4_DeltaExecutor_WithoutDemoData_RefusesSyntheticRows()
    {
        // EXT-4: rows are synthesized from file metadata; outside demo mode the executor answers 501 instead.
        var (executor, context) = NewDelta("tenantId",
        [
            DFile("owned.parquet", min: new Dictionary<string, string> { ["tenantId"] = "tenant-1" }, max: new Dictionary<string, string> { ["tenantId"] = "tenant-1" })
        ], demoData: false);

        await Should.ThrowAsync<Autheris.Domain.Exceptions.GatewayNotImplementedException>(() => executor.ExecuteAsync(context));
    }

    [Fact]
    public async Task E3_DeltaExecutor_DropsFilesWithoutTenantEvidenceAndNeverStampsSessionTenant()
    {
        var (executor, context) = NewDelta("tenantId",
        [
            DFile("no-evidence.parquet"),
            DFile("overlap.parquet", min: new Dictionary<string, string> { ["tenantId"] = "a" }, max: new Dictionary<string, string> { ["tenantId"] = "z" }),
            DFile("owned.parquet", min: new Dictionary<string, string> { ["tenantId"] = "tenant-1" }, max: new Dictionary<string, string> { ["tenantId"] = "tenant-1" })
        ]);

        var rows = await executor.ExecuteAsync(context);

        rows.Count.ShouldBe(1);
        rows[0]["tenantId"].ShouldBe("tenant-1"); // taken from the stored statistics, not stamped
    }

    [Fact]
    public async Task E3_DeltaExecutor_HonoursTheTablesTenantColumnName()
    {
        var (executor, context) = NewDelta("tenant_id",
        [
            DFile("a.parquet", new Dictionary<string, string> { ["tenant_id"] = "tenant-1" }),
            DFile("b.parquet", new Dictionary<string, string> { ["tenant_id"] = "tenant-2" }),
            DFile("c.parquet") // legacy-named column absent: no evidence
        ]);

        var rows = await executor.ExecuteAsync(context);

        rows.Count.ShouldBe(1);
        rows[0]["tenant_id"].ShouldBe("tenant-1");
    }

    // ------------------------------------------------------------------ D-1 (semantic cache hit)

    // ------------------------------------------------------------------ D-2 (result column sources)

    private const string Header = """
        name: get_orders
        procedure: api.usp_GetOrders
        mode: read
        parameters:
          - name: customer_id
            type: int
            description: Customer number
        context:
          tenant_id: TenantId
          user_sid: ActorSid
        result_table: sales.orders
        cleared_columns: [order_id]
        """;

    private sealed class ProcFixture
    {
        public InMemoryProcedureRegistry Registry { get; } = new();
        public IProcedureInvoker Invoker { get; set; } = Substitute.For<IProcedureInvoker>();
        public InMemoryTableMetadataRepository Tables { get; } = new();
        public InMemoryConsentRepository Consents { get; } = new();
        public IConsentResolutionService Resolution { get; set; } = StubConsentResolutionService.AllowUnconstrained();
        public IColumnMaskingProvider Masking { get; set; } = StubColumnMaskingProvider.Asterisks;
        public InMemoryAuditLogRepository Audit { get; } = new();
        public List<MaskingRule> MaskRules { get; } = [];
        public IClientIpResolver? IpResolver { get; set; }

        public GovernedProcedureExecutionService Create()
        {
            return new GovernedProcedureExecutionService(
                Registry, Invoker, Options.Create(new GatewayOptions()), Audit, Tables, Consents, Resolution, Masking, concurrencyGate: global::Autheris.Tests.Unit.TestDoubles.GrantAllReadConcurrencyGate.Instance, clientIpResolver: IpResolver);
        }
    }

    private static ClaimsPrincipal ProcUser(params Claim[] extra)
    {
        var claims = new List<Claim> { new(ClaimTypes.PrimarySid, "S-1-5-21-1001") };
        claims.AddRange(extra);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        return extra.Any(c => c.Type is Autheris.Domain.Identity.GroupResolutionClaims.StatusClaimType or Autheris.Domain.Identity.GroupResolutionClaims.MarkerClaimType)
            ? principal
            : Autheris.Tests.Unit.TestSupport.PipelinePrincipals.MarkComplete(principal);
    }

    private static ProcFixture NewProc(
        IReadOnlyDictionary<string, ResultColumnSource>? sources,
        string[] resultColumns,
        Func<ProcedureDefinition, ProcedureDefinition>? tweak = null,
        bool markActive = true)
    {
        var f = new ProcFixture();
        var def = TestProcedureYaml.Definition(Header, "x", false, 60);
        if (tweak != null) def = tweak(def);
        f.Registry.Register(def);
        if (markActive)
        {
            f.Registry.MarkActive(def.Name, new ProcedureValidationResult(true, [], resultColumns, ["sales.orders", "hr.people"],
                new Dictionary<string, string>(), sources));
        }

        f.Tables.Add(new TableMetadata { Identifier = new("default", "sales", "orders"), Columns = [new TableColumn { ColumnName = "order_id" }, new TableColumn { ColumnName = "salary" }] });
        f.Tables.Add(new TableMetadata { Identifier = new("default", "hr", "people"), Columns = [new TableColumn { ColumnName = "name" }, new TableColumn { ColumnName = "title" }, new TableColumn { ColumnName = "order_id" }] });
        var columns = new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
        {
            ["order_id"] = ColumnAccessLevel.Clear,
            ["salary"] = ColumnAccessLevel.Mask,
            ["name"] = ColumnAccessLevel.Mask,
            ["title"] = ColumnAccessLevel.Clear
        };
        f.Resolution = StubConsentResolutionService.Fixed(TableAccessDecision.Allowed(new("default", "sales", "orders"), columns, null));
        f.Masking = new StubColumnMaskingProvider((_, _, r) => { f.MaskRules.Add(r); return "***"; });
        f.Invoker.ExecuteReadAsync(Arg.Any<ProcedureDefinition>(), Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<ProcedureSecurityContext>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new RawProcedureResult(resultColumns, [new object?[] { 1, "x", "y", "z" }[..resultColumns.Length]], false));
        return f;
    }

    private static readonly Dictionary<string, object?> Inputs = new() { ["customer_id"] = 7 };

    [Fact]
    public async Task D2_ResultColumns_AreGovernedBySourceTable_ForeignAndComputedColumnsFailClosed()
    {
        var sources = new Dictionary<string, ResultColumnSource>(StringComparer.OrdinalIgnoreCase)
        {
            ["order_id"] = new("sales", "orders", "order_id"),
            ["total"] = new(null, null, null),                      // computed
            ["name"] = new("hr", "people", "name"),                  // foreign table, column Mask -> removed
            ["title"] = new("hr", "people", "title"),                // foreign table, column Clear -> kept
        };
        var f = NewProc(sources, ["order_id", "total", "name", "title"]);

        var result = await f.Create().ExecuteAsync("get_orders", Inputs, ProcUser(), Tenant1);

        result.Columns.ShouldBe(["order_id", "title"]);
    }

    [Fact]
    public async Task D2_AliasedColumn_IsGovernedByItsSourceColumnNotByItsOutputName()
    {
        // 'order_id' is really hr.people.name (Mask on a foreign table) - the old name based check would have treated it as the cleared order id.
        var sources = new Dictionary<string, ResultColumnSource>(StringComparer.OrdinalIgnoreCase)
        {
            ["order_id"] = new("hr", "people", "name")
        };
        var f = NewProc(sources, ["order_id"], d => d with { ClearedResultColumns = [] });

        var result = await f.Create().ExecuteAsync("get_orders", Inputs, ProcUser(), Tenant1);

        result.Columns.ShouldBeEmpty();
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData(true, 1, true)]
    [InlineData(true, 0, false)]
    [InlineData(false, 1, false)]
    public async Task ProcedureResult_HasMaskedValues_FollowsTheReturnedMaskedColumns(bool includeSalary, int rowCount, bool expected)
    {
        var sources = new Dictionary<string, ResultColumnSource>(StringComparer.OrdinalIgnoreCase)
        {
            ["order_id"] = new("sales", "orders", "order_id"),
            ["salary"] = new("sales", "orders", "salary")
        };
        string[] columns = includeSalary ? ["order_id", "salary"] : ["order_id"];
        var f = NewProc(sources, columns);
        var rows = Enumerable.Range(0, rowCount).Select(_ => new object?[] { 1, "x" }[..columns.Length]).ToList();
        f.Invoker.ExecuteReadAsync(Arg.Any<ProcedureDefinition>(), Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<ProcedureSecurityContext>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new RawProcedureResult(columns, rows, false));

        var result = await f.Create().ExecuteAsync("get_orders", Inputs, ProcUser(), Tenant1);

        result.HasMaskedValues.ShouldBe(expected);
    }

    // ------------------------------------------------------------------ Low: role before health, client IP

    [Fact]
    public async Task Low_ProcedureRoleIsCheckedBeforeHealth_AndUnseenEndpointsAnswerAsNotFound()
    {
        var f = NewProc(null, ["order_id"], d => d with { RequiredRoles = ["order-reader"] }, markActive: false);
        var svc = f.Create();

        // Pending (unhealthy) endpoint: a caller without the role must not learn about it (404, not 503).
        await Should.ThrowAsync<KeyNotFoundException>(() => svc.ExecuteAsync("get_orders", Inputs, ProcUser(), Tenant1));

        // A caller with the role does see the 503.
        var withRole = ProcUser(new Claim(ClaimTypes.Role, "order-reader"));
        await Should.ThrowAsync<ProcedureUnavailableException>(() => svc.ExecuteAsync("get_orders", Inputs, withRole, Tenant1));
    }

    [Fact]
    public void Low_HttpContextClientIpResolver_IgnoresTheTokenIpClaim()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("ip", "10.0.0.5")], "Bearer"))
        };
        IHttpContextAccessor accessor = new HttpContextAccessor { HttpContext = context };

        new HttpContextClientIpResolver(accessor).ResolveClientIp().ShouldBe(IPAddress.None);
    }

    // ------------------------------------------------------------------ D-3 (tenant-scoped HMAC)

    [Fact]
    public async Task D3_ProcedureHmacPseudonymsAreTenantScoped()
    {
        var sources = new Dictionary<string, ResultColumnSource>(StringComparer.OrdinalIgnoreCase)
        {
            ["salary"] = new("sales", "orders", "salary")
        };
        var f = NewProc(sources, ["salary"]);
        f.Tables.Add(new TableMetadata
        {
            Identifier = new("default", "sales", "orders"),
            Columns = [new TableColumn { ColumnName = "order_id" }, new TableColumn { ColumnName = "salary" }, new TableColumn { ColumnName = "name" }, new TableColumn { ColumnName = "title" }],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
            {
                ["salary"] = new MaskingRule { RuleType = "HMAC_SHA256", HmacKeyId = "k1" }
            }
        });

        await f.Create().ExecuteAsync("get_orders", Inputs, ProcUser(), Tenant1);

        f.MaskRules.ShouldNotBeEmpty();
        f.MaskRules[0].HmacKeyId.ShouldBe("k1|tenant:tenant-1");
    }

    [Fact]
    public void D3_TenantScoping_IsIdempotentAndLeavesNonHmacRulesAlone()
    {
        var hmac = new MaskingRule { RuleType = "HMAC", HmacKeyId = "k" };
        var once = GatewayExecutionService.ScopeRuleForTenant(hmac, "t1", null);
        once.HmacKeyId.ShouldBe("k|tenant:t1");
        GatewayExecutionService.ScopeRuleForTenant(once, "t1", null).HmacKeyId.ShouldBe("k|tenant:t1");

        var redact = new MaskingRule { RuleType = "REDACT" };
        GatewayExecutionService.ScopeRuleForTenant(redact, "t1", null).ShouldBeSameAs(redact);
        GatewayExecutionService.ScopeRuleForTenant(hmac, null, null).ShouldBeSameAs(hmac);
    }

    private static TableMetadata HmacMeta(TableIdentifier id) => new()
    { ColumnClassifications = TableMetadataClassificationTestExtensions.PiiFor("email"),
        Identifier = id,
        Columns = [new TableColumn { ColumnName = "email" }],
        ColumnMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
        {
            ["email"] = new MaskingRule { RuleType = "HMAC_SHA256" }
        }
    };

    [Fact]
    public void D3_GovernedRowProjection_ScopesHmacRulesToTheTenant()
    {
        var id = new TableIdentifier("d", "s", "t");
        var meta = HmacMeta(id);
        var decision = TableAccessDecision.Allowed(id, new Dictionary<string, ColumnAccessLevel> { ["email"] = ColumnAccessLevel.Mask });
        MaskingRule? seen = null;
        var provider = new StubColumnMaskingProvider((_, _, r) => { seen = r; return "pseudo"; });

        GovernedConnectorReader.ProjectRow(new Dictionary<string, object?> { ["email"] = "a@b.c" }, meta, decision, "tenant-1", new GovernedRowPolicy(provider, HmacKeyId: null), alreadyMasked: false);

        seen!.HmacKeyId.ShouldBe("default|tenant:tenant-1");
    }

    [Fact]
    public void SQL204_GovernedRowProjection_WhenAlreadyMasked_DoesNotDoubleMask()
    {
        var id = new TableIdentifier("d", "s", "t");
        var meta = HmacMeta(id);
        var decision = TableAccessDecision.Allowed(id, new Dictionary<string, ColumnAccessLevel> { ["email"] = ColumnAccessLevel.Mask });
        var maskCalls = 0;
        var provider = new StubColumnMaskingProvider((_, _, _) => { maskCalls++; return "SECOND_MASK"; });

        var row = new Dictionary<string, object?> { ["email"] = "ALREADY_MASKED_PSEUDO" };

        var result = GovernedConnectorReader.ProjectRow(row, meta, decision, "tenant-1", new GovernedRowPolicy(provider, HmacKeyId: null), alreadyMasked: true);

        // Value must remain the first pseudonym, MaskValue must not be invoked again
        result["email"].ShouldBe("ALREADY_MASKED_PSEUDO");
        maskCalls.ShouldBe(0);
    }

    // ------------------------------------------------------------------ D-4 (OLAP audit)

    private static async Task<(DefaultHttpContext Context, IDuckDbOlapEngine Engine)> RunOlapAsync(IAuditLogRepository? audit)
    {
        var context = new DefaultHttpContext();
        var services = new ServiceCollection();
        if (audit != null) services.AddAuditWriterOver(audit);
        context.RequestServices = services.BuildServiceProvider();
        context.Items["SecurityPrincipalContext"] = new SecurityPrincipalContext
        {
            UserSid = new Sid("S-1-U1"),
            TenantId = Tenant1,
            GroupSids = new HashSet<Sid>(),
            TenantRoles = new HashSet<string>(),
            ClusterRoles = new HashSet<string>(),
            AuthenticationScheme = "Bearer"
        };
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("user_sid", "S-1-U1")], "Bearer"));
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"sql\":\"SELECT 1\"}"));
        context.Response.Body = new MemoryStream();

        var engine = Substitute.For<IDuckDbOlapEngine>();
        engine.ExecuteOlapQueryAsync(Arg.Any<OlapQueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new OlapQueryResult(["x"], [new List<object?> { 1 }], 1, TimeSpan.FromMilliseconds(1)));

        await Autheris.Tests.Unit.TestDoubles.OlapEndpointTestHost.HandleAsync(
            context, engine, new InMemoryTableMetadataRepository(), new Autheris.Infrastructure.Connectors.InMemoryConnectorRegistry(),
            new NoopCrossDomainResolver(), StubColumnMaskingProvider.Null,
            Options.Create(new GatewayOptions { DuckDbOlap = new DuckDbOlapOptions { Enabled = true } }), NullLoggerFactory.Instance);
        return (context, engine);
    }

    [Fact]
    public async Task D4_OlapQuery_WritesAnAuditEventWithSqlHashBeforeReturningData()
    {
        var audit = new InMemoryAuditLogRepository();

        var (context, _) = await RunOlapAsync(audit);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        var entry = audit.Entries.Single(e => e.EventType == "OLAP_QUERY");
        entry.TenantId.ShouldBe(Tenant1);
        entry.ActorSid.Value.ShouldBe("S-1-U1");
        entry.DetailsJson.ShouldContain(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("SELECT 1"))));
        entry.DetailsJson.ShouldNotContain("SELECT 1");
    }

    [Fact]
    public async Task D4_OlapQuery_FailsClosedWhenTheAuditWriteFails_OrNoAuditIsAvailable()
    {
        var (failed, _) = await RunOlapAsync(new ThrowingAuditRepo());
        failed.Response.StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);
        failed.Response.Body.Position = 0;
        new StreamReader(failed.Response.Body).ReadToEnd().ShouldNotContain("totalRows");

        // WP-ARC-A-06: a missing audit writer is a composition error, never a silently unaudited read.
        await Should.ThrowAsync<InvalidOperationException>(() => RunOlapAsync(null));
    }

    private sealed class ThrowingAuditRepo : IAuditLogRepository
    {
        public Task RecordAuditEventAsync(AuditLogEntry entry, CancellationToken ct = default)
            => throw new InvalidOperationException("db down");
        public Task RecordAuditEventAsync(AuditLogEntry entry, System.Data.Common.DbTransaction tx, CancellationToken ct = default)
            => throw new InvalidOperationException("db down");
        public Task<IReadOnlyList<AuditLogEntry>> GetAuditLogEntriesAsync(int limit = 100, TenantId? tenantId = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AuditLogEntry>>([]);
        public Task<IReadOnlyList<AuditLogEntry>> QueryAuditLogsAsync(string? targetTable = null, Sid? actorSid = null, DateTimeOffset? since = null, int limit = 1000, TenantId? tenantId = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AuditLogEntry>>([]);
        public Task<bool> VerifyAuditHashChainAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<global::Autheris.Domain.Audit.AuditPurgeCheckpoint?> GetLatestPurgeCheckpointAsync(CancellationToken ct = default) => Task.FromResult<global::Autheris.Domain.Audit.AuditPurgeCheckpoint?>(null);
        public Task SavePurgeCheckpointAsync(global::Autheris.Domain.Audit.AuditPurgeCheckpoint checkpoint, CancellationToken ct = default) => Task.CompletedTask;
        public Task<(long MaxEligibleSequence, long FirstSurvivingSequence, string SurvivingPrevHash, long PurgeCount)?> PlanPurgePrefixAsync(DateTimeOffset thresholdUtc, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> ExecutePurgeBatchAsync(long maxSequenceInclusive, int batchSize, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class NoopCrossDomainResolver : ICrossDomainAccessResolver
    {
        public Task<TableAccessDecision> ResolveAccessAsync(
            ClaimsPrincipal principal,
            TableIdentifier table,
            TableMetadata metadata,
            TenantId? tenant,
            CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
