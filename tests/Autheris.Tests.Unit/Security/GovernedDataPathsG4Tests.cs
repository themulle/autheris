namespace Autheris.Tests.Unit.Security;

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
using Autheris.Application.Caching.Interfaces;
using Autheris.Application.Connectors;
using Autheris.Application.Connectors.CrossDomain;
using Autheris.Application.Interfaces;
using Autheris.Domain.Connectors;
using Autheris.Application.Olap;
using Autheris.Application.Policy;
using Autheris.Application.Procedures.Interfaces;
using Autheris.Application.Procedures.Services;
using Autheris.Application.Security;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Application.Serialization;
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

/// <summary>Review G4: E-3, D-1, D-2, D-3, D-4 and the related Low findings of the governed data paths.</summary>
public sealed class GovernedDataPathsG4Tests
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
            reader, new DeltaPartitionPruner(NullLogger<DeltaPartitionPruner>.Instance), Substitute.For<IColumnMaskingProvider>(),
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
        -- @name get_orders
        -- @procedure api.usp_GetOrders
        -- @mode read
        -- @param customer_id int required Customer number
        -- @context tenant_id -> @TenantId
        -- @context user_sid  -> @ActorSid
        -- @result-table sales.orders
        -- @result-column order_id clear
        """;

    private sealed class ProcFixture
    {
        public InMemoryProcedureRegistry Registry { get; } = new();
        public IProcedureInvoker Invoker { get; } = Substitute.For<IProcedureInvoker>();
        public ITableMetadataRepository Tables { get; } = Substitute.For<ITableMetadataRepository>();
        public IConsentRepository Consents { get; } = Substitute.For<IConsentRepository>();
        public IConsentResolutionService Resolution { get; } = Substitute.For<IConsentResolutionService>();
        public IColumnMaskingProvider Masking { get; } = Substitute.For<IColumnMaskingProvider>();
        public IAuditLogRepository Audit { get; } = Substitute.For<IAuditLogRepository>();
        public List<MaskingRule> MaskRules { get; } = [];
        public IClientIpResolver? IpResolver { get; set; }

        public GovernedProcedureExecutionService Create()
        {
            Audit.RecordAuditEventAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
            return new GovernedProcedureExecutionService(
                Registry, Invoker, Options.Create(new GatewayOptions()), Tables, Consents, Resolution, Masking,
                policyEnforcement: null, audit: Audit, clientIpResolver: IpResolver);
        }
    }

    private static ClaimsPrincipal ProcUser(params Claim[] extra)
    {
        var claims = new List<Claim> { new(ClaimTypes.PrimarySid, "S-1-5-21-1001") };
        claims.AddRange(extra);
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static ProcFixture NewProc(
        IReadOnlyDictionary<string, ResultColumnSource>? sources,
        string[] resultColumns,
        Func<ProcedureDefinition, ProcedureDefinition>? tweak = null,
        bool markActive = true)
    {
        var f = new ProcFixture();
        var def = ProcedureDefinitionParser.Parse(Header, "x", false, 60);
        if (tweak != null) def = tweak(def);
        f.Registry.Register(def);
        if (markActive)
        {
            f.Registry.MarkActive(def.Name, new ProcedureValidationResult(true, [], resultColumns, ["sales.orders", "hr.people"],
                new Dictionary<string, string>(), sources));
        }

        f.Tables.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            var id = ci.Arg<TableIdentifier>();
            return id.TableName == "orders"
                ? new TableMetadata { Identifier = id, Columns = [new TableColumn { ColumnName = "order_id" }, new TableColumn { ColumnName = "salary" }] }
                : new TableMetadata { Identifier = id, Columns = [new TableColumn { ColumnName = "name" }, new TableColumn { ColumnName = "title" }, new TableColumn { ColumnName = "order_id" }] };
        });
        f.Consents.GetActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(new List<Consent>());
        var columns = new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
        {
            ["order_id"] = ColumnAccessLevel.Clear,
            ["salary"] = ColumnAccessLevel.Mask,
            ["name"] = ColumnAccessLevel.Mask,
            ["title"] = ColumnAccessLevel.Clear
        };
        f.Resolution.ResolveAccess(Arg.Any<Sid>(), Arg.Any<IReadOnlySet<Sid>>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<TableIdentifier>(), Arg.Any<IReadOnlyList<Consent>>(), Arg.Any<DatabaseDialect>())
            .Returns(ci => TableAccessDecision.Allowed(ci.ArgAt<TableIdentifier>(3), columns, null));
        f.Masking.MaskValue(Arg.Any<string>(), Arg.Any<object?>(), Arg.Do<MaskingRule>(f.MaskRules.Add)).Returns("***");
        f.Invoker.ExecuteReadAsync(Arg.Any<ProcedureDefinition>(), Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<ProcedureSecurityContext>(), Arg.Any<CancellationToken>())
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
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(context);

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
        f.Tables.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns(ci => new TableMetadata
        {
            Identifier = ci.Arg<TableIdentifier>(),
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
    {
        Identifier = id,
        Columns = [new TableColumn { ColumnName = "email", IsSensitive = true }],
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
        var provider = Substitute.For<IColumnMaskingProvider>();
        MaskingRule? seen = null;
        provider.MaskValue(Arg.Any<string>(), Arg.Any<object?>(), Arg.Do<MaskingRule>(r => seen = r)).Returns("pseudo");

        GovernedConnectorReader.ProjectRow(new Dictionary<string, object?> { ["email"] = "a@b.c" }, meta, decision, "tenant-1", new GovernedRowPolicy(provider, HmacKeyId: null), alreadyMasked: false);

        seen!.HmacKeyId.ShouldBe("default|tenant:tenant-1");
    }

    [Fact]
    public void SQL204_GovernedRowProjection_WhenAlreadyMasked_DoesNotDoubleMask()
    {
        var id = new TableIdentifier("d", "s", "t");
        var meta = HmacMeta(id);
        var decision = TableAccessDecision.Allowed(id, new Dictionary<string, ColumnAccessLevel> { ["email"] = ColumnAccessLevel.Mask });
        var provider = Substitute.For<IColumnMaskingProvider>();
        provider.MaskValue(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<MaskingRule>()).Returns("SECOND_MASK");

        var row = new Dictionary<string, object?> { ["email"] = "ALREADY_MASKED_PSEUDO" };

        var result = GovernedConnectorReader.ProjectRow(row, meta, decision, "tenant-1", new GovernedRowPolicy(provider, HmacKeyId: null), alreadyMasked: true);

        // Value must remain the first pseudonym, MaskValue must not be invoked again
        result["email"].ShouldBe("ALREADY_MASKED_PSEUDO");
        provider.DidNotReceive().MaskValue(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<MaskingRule>());
    }

    // ------------------------------------------------------------------ D-4 (OLAP audit)

    private static async Task<(DefaultHttpContext Context, IDuckDbOlapEngine Engine)> RunOlapAsync(IAuditLogRepository? audit)
    {
        var context = new DefaultHttpContext();
        var services = new ServiceCollection();
        if (audit != null) services.AddSingleton(audit);
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

        await DuckDbOlapEndpoints.HandleOlapQueryAsync(
            context, engine, Substitute.For<ITableMetadataRepository>(), Substitute.For<IAutherisConnectorRegistry>(),
            Substitute.For<ICrossDomainAccessResolver>(), Substitute.For<IColumnMaskingProvider>(),
            Options.Create(new GatewayOptions { DuckDbOlap = new DuckDbOlapOptions { Enabled = true } }), NullLoggerFactory.Instance);
        return (context, engine);
    }

    [Fact]
    public async Task D4_OlapQuery_WritesAnAuditEventWithSqlHashBeforeReturningData()
    {
        var entries = new List<AuditLogEntry>();
        var audit = Substitute.For<IAuditLogRepository>();
        audit.RecordAuditEventAsync(Arg.Do<AuditLogEntry>(entries.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var (context, _) = await RunOlapAsync(audit);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        var entry = entries.Single(e => e.EventType == "OLAP_QUERY");
        entry.TenantId.ShouldBe(Tenant1);
        entry.ActorSid.Value.ShouldBe("S-1-U1");
        entry.DetailsJson.ShouldContain(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("SELECT 1"))));
        entry.DetailsJson.ShouldNotContain("SELECT 1");
    }

    [Fact]
    public async Task D4_OlapQuery_FailsClosedWhenTheAuditWriteFails_OrNoAuditIsAvailable()
    {
        var audit = Substitute.For<IAuditLogRepository>();
        audit.RecordAuditEventAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>()).Returns(Task.FromException(new InvalidOperationException("db down")));

        var (failed, _) = await RunOlapAsync(audit);
        failed.Response.StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);
        failed.Response.Body.Position = 0;
        new StreamReader(failed.Response.Body).ReadToEnd().ShouldNotContain("totalRows");

        var (missing, _) = await RunOlapAsync(null);
        missing.Response.StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);
    }

    // ------------------------------------------------------------------ Low: Arrow Flight SQL tickets

    private static ArrowFlightSqlServer NewFlight(string? key = null) => new(
        Substitute.For<IArrowExportService>(),
        Substitute.For<ITableMetadataRepository>(),
        Options.Create(new GatewayOptions
        {
            Arrow = new ArrowExportOptions { FlightTicketSigningKey = key }
        }),
        NullLogger<ArrowFlightSqlServer>.Instance);

    private static ClaimsPrincipal FlightUser(string sid, string tenant) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, sid), new Claim("tenant_id", tenant)], "Bearer"));

    [Fact]
    public async Task Low_FlightTicket_IsBoundToSidAndTenantOfTheIssuedCaller()
    {
        var server = NewFlight("0123456789abcdef0123456789abcdef");
        var owner = FlightUser("S-1-5-21-1", "tenant-1");
        var info = await server.GetFlightInfoAsync("SELECT 1", owner, Tenant1);
        info.Ticket.UserSid.ShouldBe("S-1-5-21-1");

        // owner can redeem
        var count = 0;
        await foreach (var _ in server.DoGetStreamAsync(info.Ticket, owner, Tenant1)) count++;
        count.ShouldBe(1);

        // another user of the same tenant and the same user in another tenant cannot
        await Should.ThrowAsync<SecurityException>(async () =>
        {
            await foreach (var _ in server.DoGetStreamAsync(info.Ticket, FlightUser("S-1-5-21-2", "tenant-1"), Tenant1)) { }
        });
        await Should.ThrowAsync<SecurityException>(async () =>
        {
            await foreach (var _ in server.DoGetStreamAsync(info.Ticket, FlightUser("S-1-5-21-1", "tenant-2"), new TenantId("tenant-2"))) { }
        });

        // swapping the SID inside the ticket invalidates the signature
        var forged = info.Ticket with { UserSid = "S-1-5-21-2" };
        await Should.ThrowAsync<SecurityException>(async () =>
        {
            await foreach (var _ in server.DoGetStreamAsync(forged, FlightUser("S-1-5-21-2", "tenant-1"), Tenant1)) { }
        });
    }

    [Fact]
    public async Task Low_FlightTicket_IsNotSignedWithTheForwardAuthSecret()
    {
        var options = Options.Create(new GatewayOptions
        {
            Authentication = new AuthenticationOptions { ForwardAuth = new ForwardAuthOptions { SharedSecret = "forward-auth-shared-secret-value" } }
        });
        var server = new ArrowFlightSqlServer(
            Substitute.For<IArrowExportService>(), Substitute.For<ITableMetadataRepository>(), options, NullLogger<ArrowFlightSqlServer>.Instance);
        var info = await server.GetFlightInfoAsync("SELECT 1", FlightUser("S-1-5-21-1", "tenant-1"), Tenant1);

        var withSharedSecret = ArrowFlightSqlServer.ComputeSignature(
            info.Ticket.TicketId, info.Ticket.TenantId, info.Ticket.Query, info.Ticket.CreatedAtUtc, "forward-auth-shared-secret-value", info.Ticket.UserSid);
        info.Ticket.Signature.ShouldNotBe(withSharedSecret);
    }
}
