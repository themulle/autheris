namespace Autheris.Tests.Unit.Usage;

using Autheris.Application.Policy.Pdp;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Api.Middleware;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.DataSources.Interfaces;
using Autheris.Application.DataSources.Services;
using Autheris.Application.Policy;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Usage;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Diagnostics;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Tests.Unit.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class UsageGrantSecurityTests
{
    private static MeterListener StartAllowWithoutGrantListener(Action<long> onMeasurement)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == GatewayDiagnostics.Meter.Name &&
                    instrument.Name == "autheris_usage_allow_without_grant_total")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => onMeasurement(value));
        listener.Start();
        return listener;
    }

    private static DbDataReader CreateSingleRowReader()
    {
        var dt = new DataTable();
        dt.Columns.Add("id", typeof(int));
        dt.Columns.Add("amount", typeof(decimal));
        dt.Rows.Add(1, 42.5m);
        return dt.CreateDataReader();
    }

    [Fact]
    public void Allow_fact_without_grant_recorded_invalid_and_counted()
    {
        long withoutGrantCount = 0;
        using var listener = StartAllowWithoutGrantListener(v => Interlocked.Add(ref withoutGrantCount, v));

        var now = new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
        var aggregator = new UsageAggregator(timeProvider: new FixedTimeProvider(now));
        var recorder = new UsageRecorder(aggregator);
        var tenant = new TenantId("tenant-no-grant");
        var actor = new Sid("S-1-5-21-NO-GRANT");
        var table = new TableIdentifier("sales", "dbo", "orders");

        recorder.Record(
            new UsageFact(table, "sales-pg", UsageOutcome.Allow, 10, RowsMeasured: true, AnalysisCost: 0, Grants: [], Source: default),
            tenant,
            actor);

        var snap = aggregator.Swap();
        snap.GrantSeries.Count.ShouldBe(1);
        var invalidSeries = snap.GrantSeries.Single();
        invalidSeries.Key.GrantId.ShouldBe(GrantRef.Invalid.GrantId);
        invalidSeries.Value.Accesses.ShouldBe(1);
        snap.GrantRefs.ContainsKey((tenant, GrantRef.Invalid.GrantId)).ShouldBeTrue();
        Interlocked.Read(ref withoutGrantCount).ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public void Expired_grant_on_allow_is_flagged_invalid()
    {
        long withoutGrantCount = 0;
        using var listener = StartAllowWithoutGrantListener(v => Interlocked.Add(ref withoutGrantCount, v));

        var now = new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
        var aggregator = new UsageAggregator(timeProvider: new FixedTimeProvider(now));
        var recorder = new UsageRecorder(aggregator);
        var tenant = new TenantId("tenant-expired");
        var actor = new Sid("S-1-5-21-EXP");
        var table = new TableIdentifier("sales", "dbo", "invoices");

        var expiredGrant = new GrantRef("grant-expired", now.AddDays(-10), now.AddSeconds(-1));
        var notYetValidGrant = new GrantRef("grant-future", now.AddSeconds(1), now.AddDays(10));

        recorder.Record(
            new UsageFact(table, "sales-pg", UsageOutcome.Allow, 5, RowsMeasured: true, AnalysisCost: 0, Grants: [expiredGrant], Source: default),
            tenant,
            actor);

        recorder.Record(
            new UsageFact(table, "sales-pg", UsageOutcome.Allow, 3, RowsMeasured: true, AnalysisCost: 0, Grants: [notYetValidGrant], Source: default),
            tenant,
            actor);

        var snap = aggregator.Swap();
        snap.GrantSeries.Keys.ShouldNotContain(k => k.GrantId == "grant-expired");
        snap.GrantSeries.Keys.ShouldNotContain(k => k.GrantId == "grant-future");
        snap.GrantRefs.ContainsKey((tenant, "grant-expired")).ShouldBeFalse();
        snap.GrantRefs.ContainsKey((tenant, "grant-future")).ShouldBeFalse();

        var invalidEntry = snap.GrantSeries.Single(kv => kv.Key.GrantId == GrantRef.Invalid.GrantId);
        invalidEntry.Value.Accesses.ShouldBe(2);
        invalidEntry.Value.Rows.ShouldBe(8);
        Interlocked.Read(ref withoutGrantCount).ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task Grant_id_never_taken_from_request()
    {
        var now = DateTimeOffset.UtcNow;
        var validFrom = now.AddDays(-1);
        var validUntil = now.AddDays(10);
        const string pdpGrantId = "pdp-authoritative-grant-007";
        const string spoofedHeaderGrant = "spoofed-header-grant";
        const string spoofedQueryGrant = "spoofed-query-grant";
        const string spoofedBodyGrant = "spoofed-body-grant";

        var aggregator = new UsageAggregator(timeProvider: new FixedTimeProvider(now));
        var recorder = new UsageRecorder(aggregator);

        var tableId = new TableIdentifier("sales", "dbo", "orders");
        var metadata = new TableMetadata
        {
            Identifier = tableId,
            Table = new Table
            {
                TableName = "orders",
                SchemaName = "dbo",
                SourceName = "sales-pg",
                SourceType = "PostgreSQL",
                IsActive = true
            },
            PrimaryKeyColumns = ["id"],
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "integer" },
                new TableColumn { ColumnName = "amount", DataType = "numeric" }
            ]
        };

        var decision = TableAccessDecision.Allowed(
            tableId,
            new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = ColumnAccessLevel.Clear,
                ["amount"] = ColumnAccessLevel.Clear
            },
            rowFilterSql: null,
            hasUnconstrainedColumnAllow: true) with
        {
            Grants = [new DecisionGrantRef(pdpGrantId, GrantSourceKind.ConsentRequest, validFrom, validUntil)]
        };

        var repo = NullFake.Create<ITableMetadataRepository>((method, _) =>
            method.Name == nameof(ITableMetadataRepository.GetTableMetadataAsync)
                ? Task.FromResult<TableMetadata?>(metadata)
                : NullFake.UseDefault);

        var pdp = NullFake.Create<IAccessDecisionPoint>((method, _) =>
            method.Name == nameof(IAccessDecisionPoint.DecideAsync)
                ? new ValueTask<TableAccessDecision>(decision)
                : NullFake.UseDefault);

        var sqlExec = NullFake.Create<IGovernedSqlExecutionService>((method, args) =>
        {
            if (method.Name == nameof(IGovernedSqlExecutionService.ExecuteGovernedQueryAsync) && args is { Length: >= 4 } && args[3] is Func<DbDataReader, CancellationToken, Task> handler)
            {
                return RunReaderAsync(handler);
            }

            return NullFake.UseDefault;

            static async Task RunReaderAsync(Func<DbDataReader, CancellationToken, Task> handler)
            {
                using var reader = CreateSingleRowReader();
                await handler(reader, CancellationToken.None).ConfigureAwait(false);
            }
        });

        var tenant = new TenantId("tenant-stream");
        var userSid = new Sid("S-1-5-21-STREAM-USER");
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/tables/sales.dbo.orders/stream";
        context.Request.Headers["X-Grant-Id"] = spoofedHeaderGrant;
        context.Request.QueryString = new QueryString($"?grantId={spoofedQueryGrant}");
        var bodyBytes = System.Text.Encoding.UTF8.GetBytes($"{{\"limit\":10,\"grantId\":\"{spoofedBodyGrant}\"}}");
        context.Request.Body = new MemoryStream(bodyBytes);
        context.Request.ContentType = "application/json";
        context.Request.ContentLength = bodyBytes.Length;
        context.Response.Body = new MemoryStream();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", "stream-user"),
            new Claim("tenant", tenant.Value),
            new Claim("sid", userSid.Value)
        ], "Bearer"));
        context.Items[SecurityPrincipalContext.ItemKey] = new SecurityPrincipalContext
        {
            UserSid = userSid,
            ResolvedTenant = tenant,
            GroupSids = new HashSet<Sid>(),
            TenantRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            ClusterRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            AuthenticationScheme = "Bearer",
            IsAuthenticated = true
        };

        var services = new ServiceCollection();
        services.AddSingleton<IOptions<GatewayOptions>>(Options.Create(new GatewayOptions()));
        services.AddSingleton<IUsageRecorder>(recorder);
        context.RequestServices = services.BuildServiceProvider();

        var middleware = new UsageMeteringMiddleware(ctx =>
            TableStreamEndpoints.HandleBulkStreamAsync(
                "sales",
                "dbo",
                "orders",
                ctx,
                repo,
                pdp,
                new GovernedTableReadPlanner(),
                sqlExec,
                new KeysetCursorEncoder("endpoint-planner-shared-signing-key-0123456789"),
                new PassThroughMaskingProvider(),
                NullLoggerFactory.Instance,
                CancellationToken.None));

        await middleware.InvokeAsync(context, aggregator);

        var snap = aggregator.Swap();
        snap.GrantSeries.Count.ShouldBe(1);
        snap.GrantSeries.Keys.Single().GrantId.ShouldBe(pdpGrantId);
        snap.GrantSeries.Keys.ShouldNotContain(k => k.GrantId == spoofedHeaderGrant);
        snap.GrantSeries.Keys.ShouldNotContain(k => k.GrantId == spoofedQueryGrant);
        snap.GrantSeries.Keys.ShouldNotContain(k => k.GrantId == spoofedBodyGrant);
        snap.GrantRefs.Keys.ShouldNotContain(k => k.GrantId.StartsWith("spoofed-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Data_access_audit_entry_contains_grant_fields()
    {
        var validFrom1 = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        var validUntil1 = new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);
        var validFrom2 = new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero);

        var table = new TableIdentifier("sales", "dbo", "orders");
        var decision = TableAccessDecision.Allowed(
            table,
            new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase) { ["id"] = ColumnAccessLevel.Clear },
            rowFilterSql: null,
            hasUnconstrainedColumnAllow: true) with
        {
            Grants =
            [
                new DecisionGrantRef("grant-consent-1", GrantSourceKind.ConsentRequest, validFrom1, validUntil1),
                new DecisionGrantRef("grant-owner-2", GrantSourceKind.Owner, validFrom2, ValidUntil: null)
            ]
        };

        var audit = new CapturingAuditWriter();
        await GovernedQueryStages.RecordTableReadAsync(
            audit,
            new TenantId("default"),
            new Sid("S-1-5-21-AUDIT-USER"),
            table,
            allowed: true,
            GovernedQueryStages.DecisionDetails(decision),
            CancellationToken.None,
            grants: decision.Grants);

        var entry = audit.Entries.ShouldHaveSingleItem();
        entry.EventType.ShouldBe(AuditEventTypes.TableQuery);
        entry.Decision.ShouldBe("ALLOW");

        var redactedJson = AuditDetailsBuilder.RedactJson(entry.DetailsJson);
        var persisted = new AuditLogEntry
        {
            Id = entry.Id,
            TenantId = entry.TenantId,
            OccurredAt = entry.OccurredAt,
            EventType = entry.EventType,
            ActorSid = entry.ActorSid,
            TargetTable = entry.TargetTable,
            TargetColumn = entry.TargetColumn,
            Decision = entry.Decision,
            TraceId = entry.TraceId,
            DetailsJson = redactedJson
        };

        using var doc = JsonDocument.Parse(redactedJson);
        doc.RootElement.TryGetProperty("grants", out var grantsEl).ShouldBeTrue();
        grantsEl.ValueKind.ShouldBe(JsonValueKind.Array);
        grantsEl.GetArrayLength().ShouldBe(2);

        var first = grantsEl[0];
        first.GetProperty("grantId").GetString().ShouldBe("grant-consent-1");
        first.GetProperty("grantValidFrom").GetDateTimeOffset().ShouldBe(validFrom1);
        first.GetProperty("grantValidUntil").GetDateTimeOffset().ShouldBe(validUntil1);

        var second = grantsEl[1];
        second.GetProperty("grantId").GetString().ShouldBe("grant-owner-2");
        second.GetProperty("grantValidFrom").GetDateTimeOffset().ShouldBe(validFrom2);
        second.GetProperty("grantValidUntil").ValueKind.ShouldBe(JsonValueKind.Null);

        // Verify cryptographic hash-chain engine handles the new grants array cleanly and verifies identically.
        var lastHash = AuditChainEngine.ComputeBatchChain([persisted], AuditChainEngine.AuditGenesisHash, hmacKey: null, startSequence: 1);
        persisted.EntryHash.ShouldBe(lastHash);
        var recomputed = AuditChainEngine.ComputeEntryHash(
            hmacKey: null,
            persisted.ChainSequence,
            persisted.Id.ToString(),
            persisted.PrevHash,
            persisted.OccurredAt,
            persisted.EventType,
            persisted.ActorSid.Value,
            persisted.TargetTable,
            persisted.TargetColumn,
            persisted.Decision,
            persisted.TraceId,
            persisted.DetailsJson,
            persisted.TenantId.Value);
        recomputed.ShouldBe(persisted.EntryHash);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class CapturingAuditWriter : IAuditWriter
    {
        public List<AuditLogEntry> Entries { get; } = [];

        public Task RecordRequiredAsync(AuditLogEntry entry, CancellationToken ct = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task RecordBestEffortAsync(AuditLogEntry entry, CancellationToken ct = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task RecordAsync(AuditLogEntry entry, CancellationToken ct = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }

    private sealed class PassThroughMaskingProvider : IColumnMaskingProvider
    {
        public object? MaskValue(string columnName, object? value, MaskingRule rule) => value;
        public string BuildSqlMaskExpression(string columnName, MaskingRule rule, DatabaseDialect dialect) => columnName;
    }
}
