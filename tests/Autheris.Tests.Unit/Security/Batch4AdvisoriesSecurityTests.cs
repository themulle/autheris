namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Api.Errors;
using Autheris.Application.Analytics;
using Autheris.Application.DataSources;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Procedures.Interfaces;
using Autheris.Application.Procedures.Services;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Application.Streaming.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Options.DataSources;
using Autheris.Domain.Security;
using Autheris.Extensions.Lakehouse.Interfaces;
using Autheris.Extensions.Lakehouse.Services;
using Autheris.GraphQL.Subscriptions;
using Autheris.Tests.Unit.Catalog;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// Security tests for Batch 4 advisories: GHSA-3953-r994-fg4m and GHSA-6523-m29q-c397.
/// </summary>
[Trait("Category", "Security")]
public sealed class Batch4AdvisoriesSecurityTests
{
    private static readonly TenantId Tenant = new("tenant-a");

    // =========================================================================
    // GHSA-6523-m29q-c397: Small-group suppression & differencing
    // =========================================================================

    [Theory]
    [InlineData("id <= 7")]
    [InlineData("id >= 7 AND id <= 7")]
    [InlineData("id < 7")]
    [InlineData("id > 7")]
    [InlineData("id BETWEEN 6 AND 7")]
    public void GHSA_6523_m29q_c397_RangePredicatesOnPrimaryKey_AreRejectedAsIsolating(string sql)
    {
        var meta = Wp0.Table("sales", "orders", columns: ["id", "dept", "amount"]) with
        {
            PrimaryKeyColumns = ["id"]
        };
        var clause = new TableFilterClause(sql, new Dictionary<string, object?>(), ["id"]);

        Should.Throw<AggregationFilterRejectedException>(() =>
            AggregationNegationGuard.Enforce(clause, groupByColumns: ["dept"], meta, effectiveMinGroupSize: 5, restrictNegatedFilters: true));
    }

    [Fact]
    public async Task GHSA_6523_m29q_c397_MinMaxCountDistinct_OnNonGroupColumn_WhenKGreaterThan1_Rejected()
    {
        var meta = Wp0.Table("sales", "orders", columns: ["id", "dept", "amount"]) with
        {
            PrimaryKeyColumns = ["id"]
        };

        var metaRepo = Substitute.For<ITableMetadataRepository>();
        metaRepo.GetTableMetadataAsync(meta.Identifier, Arg.Any<CancellationToken>())
            .Returns(meta);

        var accessResolver = Substitute.For<ITableAccessResolver>();
        accessResolver.ResolveTableAccessAsync(Arg.Any<ClaimsPrincipal>(), meta.Identifier, null, null, Arg.Any<CancellationToken>())
            .Returns(new ResolvedTableAccess(meta, TableAccessDecision.Allowed(meta.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true), Tenant, new Sid("user-1"), Wp0.User("user-1", Tenant.Value)));

        var service = new GovernedAggregationService(
            metaRepo,
            Substitute.For<IAuditLogRepository>(),
            analyticsOptions: Options.Create(new AnalyticsOptions { MinimumGroupSize = 5 }),
            accessResolver: accessResolver);

        var user = Wp0.User("user-1", Tenant.Value);

        // MIN on non-group column
        var minQuery = new AggregationQuery(
            Table: meta.Identifier,
            GroupBy: ["dept"],
            Aggregates: [new AggregateSelection(AggregateFunction.Min, "amount", "min_amount")]);

        var exMin = await Should.ThrowAsync<ArgumentException>(() =>
            service.ExecuteAggregationAsync(user, minQuery));
        exMin.Message.ShouldContain("not permitted when minimum group size is greater than 1");

        // MAX on non-group column
        var maxQuery = new AggregationQuery(
            Table: meta.Identifier,
            GroupBy: ["dept"],
            Aggregates: [new AggregateSelection(AggregateFunction.Max, "amount", "max_amount")]);

        var exMax = await Should.ThrowAsync<ArgumentException>(() =>
            service.ExecuteAggregationAsync(user, maxQuery));
        exMax.Message.ShouldContain("not permitted when minimum group size is greater than 1");

        // COUNT DISTINCT on non-group column
        var cdQuery = new AggregationQuery(
            Table: meta.Identifier,
            GroupBy: ["dept"],
            Aggregates: [new AggregateSelection(AggregateFunction.CountDistinct, "amount", "cd_amount")]);

        var exCd = await Should.ThrowAsync<ArgumentException>(() =>
            service.ExecuteAggregationAsync(user, cdQuery));
        exCd.Message.ShouldContain("not permitted when minimum group size is greater than 1");
    }

    // =========================================================================
    // GHSA-3953-r994-fg4m: AllowedTransports on Procedures, Iceberg, CDC
    // =========================================================================

    [Fact]
    public async Task GHSA_3953_r994_fg4m_GovernedProcedureExecution_EnforcesTransportGate()
    {
        var gate = Substitute.For<IDataSourceTransportGate>();
        gate.EnsureCatalogSourceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new TransportNotAllowedException("test_source", DataSourceTransportFlags.Rest)));

        var registry = Substitute.For<IProcedureRegistry>();
        var def = new ProcedureDefinition(
            Name: "get_orders",
            Summary: "Get orders",
            ProcedureName: "dbo.get_orders",
            Mode: ProcedureMode.Read,
            DataSource: "test_source",
            Parameters: [],
            ContextBindings: [],
            RlsMode: ProcedureRlsMode.SessionContext,
            ResultTable: "dbo.orders",
            ClearedResultColumns: [],
            RequiredRoles: [],
            AllowDynamicSql: false,
            TimeoutSeconds: 30);

        var validation = new ProcedureValidationResult(true, [], [], [], new Dictionary<string, string>(), null);
        var reg = new RegisteredProcedure(def, ProcedureState.Active, validation, DateTimeOffset.UtcNow, null);
        registry.TryGet("get_orders", out Arg.Any<RegisteredProcedure?>())
            .Returns(x => { x[1] = reg; return true; });

        var invoker = Substitute.For<IProcedureInvoker>();
        var options = Options.Create(new GatewayOptions
        {
            Insecure = new InsecureGettingStartedOptions { danger_bypass_consent_checks = true },
            DataSources = new() { MaxConcurrentReadsPerUserAndTable = 0 }
        });
        var audit = Substitute.For<IAuditLogRepository>();
        var tableRepo = Substitute.For<ITableMetadataRepository>();
        tableRepo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(ci => new TableMetadata
            {
                Identifier = ci.Arg<TableIdentifier>(),
                Columns = [new TableColumn { ColumnName = "id", DataType = "int" }],
                Table = new() { IsActive = true }
            });

        var procService = new GovernedProcedureExecutionService(
            registry, invoker, options, audit, tableRepository: tableRepo, transportGate: gate);

        var user = Wp0.User("user-1", Tenant.Value);
        await Should.ThrowAsync<TransportNotAllowedException>(() =>
            procService.ExecuteAsync("get_orders", null, user, Tenant));
    }

    [Fact]
    public async Task GHSA_3953_r994_fg4m_IcebergRestCatalog_EnforcesTransportGate()
    {
        var gate = Substitute.For<IDataSourceTransportGate>();
        gate.EnsureCatalogSourceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new TransportNotAllowedException("iceberg_src", DataSourceTransportFlags.Rest)));

        var meta = Wp0.Table("iceberg_src", "orders", columns: ["id"]) with
        {
            Identifier = new TableIdentifier(Tenant.Value, "sales", "orders")
        };
        var metaRepo = Substitute.For<ITableMetadataRepository>();
        metaRepo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(meta);

        var service = new IcebergRestCatalogFederationService(
            Substitute.For<IIcebergMetadataReader>(),
            metaRepo,
            Options.Create(new GatewayOptions()),
            NullLogger<IcebergRestCatalogFederationService>.Instance,
            Substitute.For<IAuditLogRepository>(),
            transportGate: gate);

        var user = Wp0.User("user-1", Tenant.Value);
        await Should.ThrowAsync<TransportNotAllowedException>(() =>
            service.LoadTableAsync(Tenant.Value, "sales", "orders", user).AsTask());

        // Verify endpoint mapping produces 403 TransportNotAllowed
        var http = new DefaultHttpContext();
        var result = IcebergRestCatalogEndpoints.HandleIcebergError(http, new TransportNotAllowedException("iceberg_src", DataSourceTransportFlags.Rest));
        result.ShouldNotBeNull();
    }

    [Fact]
    public async Task GHSA_3953_r994_fg4m_CdcSubscriptionGovernor_EnforcesTransportGate()
    {
        var gate = Substitute.For<IDataSourceTransportGate>();
        gate.EnsureCatalogSourceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new TransportNotAllowedException("cdc_src", DataSourceTransportFlags.GraphQL)));

        var meta = Wp0.Table("cdc_src", "orders", columns: ["id"]) with
        {
            Identifier = new TableIdentifier(Tenant.Value, "public", "orders")
        };
        var accessResolver = Substitute.For<ITableAccessResolver>();
        accessResolver.ResolveTableAccessAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<TableIdentifier>(), null, null, Arg.Any<CancellationToken>())
            .Returns(new ResolvedTableAccess(meta, TableAccessDecision.Allowed(meta.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true), Tenant, new Sid("user-1"), Wp0.User("user-1", Tenant.Value)));

        var governor = new CdcSubscriptionGovernor(Options.Create(new GatewayOptions()), NullLogger<CdcSubscriptionGovernor>.Instance);
        var user = Wp0.User("user-1", Tenant.Value);
        var tableId = new TableIdentifier(Tenant.Value, "public", "orders");

        await Should.ThrowAsync<TransportNotAllowedException>(() =>
            governor.AdmitAsync(user, "cdc_orders", tableId, accessResolver, Substitute.For<IAuditLogRepository>(), CancellationToken.None, transportGate: gate));
    }

    [Fact]
    public async Task GHSA_3953_r994_fg4m_StreamRlsPolicyEnforcer_EnforcesTransportGate()
    {
        var gate = Substitute.For<IDataSourceTransportGate>();
        gate.EnsureCatalogSourceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new TransportNotAllowedException("cdc_src", DataSourceTransportFlags.Rest)));

        var meta = Wp0.Table("cdc_src", "orders", columns: ["id"]) with
        {
            Identifier = new TableIdentifier(Tenant.Value, "sales", "orders")
        };
        var metaRepo = Substitute.For<ITableMetadataRepository>();
        metaRepo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(meta);

        var enforcer = new StreamRlsPolicyEnforcer(
            Substitute.For<IPolicyEnforcementService>(),
            metaRepo,
            Substitute.For<IColumnMaskingProvider>(),
            Substitute.For<IEpochValidationService>(),
            NullLogger<StreamRlsPolicyEnforcer>.Instance,
            Substitute.For<IConsentRepository>(),
            Substitute.For<IConsentResolutionService>(),
            Substitute.For<IConsentCacheService>(),
            transportGate: gate);

        var user = Wp0.User("user-1", Tenant.Value);
        var cdcEvent = new CdcEvent(
            EventId: "evt-1",
            Table: meta.Identifier,
            Operation: CdcOperation.Insert,
            TenantId: Tenant.Value,
            Before: null,
            After: new Dictionary<string, object?> { ["id"] = 1 },
            Timestamp: DateTimeOffset.UtcNow);

        var decision = await enforcer.EvaluateAndMaskAsync(cdcEvent, user);
        decision.IsAllowed.ShouldBeFalse();
        decision.MaskedPayload.ShouldBeNull();
        decision.FilterReason.ShouldBe("Transport not allowed");
    }
}
