namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Analytics;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Models;
using Autheris.Application.Catalog.Semantic;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Common.Fakes;
using HotChocolate.Execution;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

public sealed partial class AdvisoryRegressionTests
{
    [Fact]
    [Trait("Category", "Security")]
    public async Task Ghsa_xwpx_93pp_regression()
    {
        var httpIdentity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-HTTP"),
            new Claim(ClaimTypes.Role, "GovernanceAdmin"),
            new Claim(ClaimTypes.GroupSid, "S-1-5-21-1-2-3-400", ClaimValueTypes.String, IdentityClaimIssuers.Pipeline),
            new Claim("tenant_id", "tenant-a")
        ], "Bearer");
        var httpUser = new ClaimsPrincipal(httpIdentity);

        var mcpAppPrincipal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-MCP-APP"),
            new Claim("idtyp", "app"),
            new Claim("tenant_id", "tenant-a")
        ], "McpAuth"));

        var aggregation = new RecordingAggregationService();
        var metrics = new RecordingMetricQueryService();
        var approvalRepo = new RecordingConsentApprovalRepo();
        var catalogClient = new StubDataCatalogClient();
        var catalogSync = new RecordingCatalogSyncService();

        var table = Autheris.Tests.Unit.GraphQL.TypedAggregateTestHost.DefaultTable();
        var metadataRepo = new InMemoryTableMetadataRepository(table);
        var relationRepo = new InMemoryTableRelationRepository();

        var httpContext = new DefaultHttpContext { User = httpUser };
        httpContext.Items["TenantId"] = new TenantId("tenant-a");
        var accessor = new FixedHttpContextAccessor(httpContext);

        var services = new ServiceCollection();
        services.AddSingleton<ITableMetadataRepository>(metadataRepo);
        services.AddSingleton<ITableRelationRepository>(relationRepo);
        services.AddSingleton<IGovernedTreeQueryService>(new NoOpTreeQueryService());
        services.AddSingleton<IGovernedAggregationService>(aggregation);
        services.AddSingleton<ISemanticMetricQueryService>(metrics);
        services.AddSingleton<IConsentApprovalRepository>(approvalRepo);
        services.AddSingleton<IDataCatalogClient>(catalogClient);
        services.AddSingleton<IDataCatalogSyncService>(catalogSync);
        services.AddSingleton<IHttpContextAccessor>(accessor);
        services.AddSingleton(Options.Create(new GatewayOptions()));
        services.AddSingleton(Options.Create(new AnalyticsOptions()));

        var sp = services.BuildServiceProvider();
        httpContext.RequestServices = sp;

        var executor = await services
            .AddGraphQLServer()
            .AddQueryType<Autheris.GraphQL.Types.Query>()
            .AddMutationType<Autheris.GraphQL.Types.Mutation>()
            .AddTypeModule(_ => new Autheris.GraphQL.Catalog.CatalogGraphQlTypeModule(metadataRepo, relationRepo, null, null, null, true))
            .UseRequest<Autheris.GraphQL.Catalog.CatalogOperationCleanupMiddleware>()
            .UseDefaultPipeline()
            .BuildRequestExecutorAsync();

        // 1. Aggregate resolver uses GlobalState ClaimsPrincipal (app-only, zero groups), not HttpContext.User.
        var aggRes = await executor.ExecuteAsync(OperationRequestBuilder.New()
            .SetDocument("{ sales_dbo_orders_aggregate(aggregates: [{ fn: COUNT, alias: \"n\" }]) { count } }")
            .AddGlobalState(nameof(ClaimsPrincipal), mcpAppPrincipal)
            .Build());
        Autheris.Tests.Unit.GraphQL.TypedAggregateTestHost.Errors(aggRes).ShouldBeEmpty();
        var aggCaller = aggregation.Calls.ShouldHaveSingleItem();
        aggCaller.GetUserSid().ShouldBe(new Sid("S-1-5-21-MCP-APP"));
        aggCaller.GetGroupSids().ShouldBeEmpty();

        // 2. Metric resolver uses GlobalState ClaimsPrincipal, not HttpContext.User.
        var metRes = await executor.ExecuteAsync(OperationRequestBuilder.New()
            .SetDocument("{ queryMetrics(metrics: [\"revenue\"]) { count } }")
            .AddGlobalState(nameof(ClaimsPrincipal), mcpAppPrincipal)
            .Build());
        Autheris.Tests.Unit.GraphQL.TypedAggregateTestHost.Errors(metRes).ShouldBeEmpty();
        var metricCaller = metrics.Calls.ShouldHaveSingleItem();
        metricCaller.GetUserSid().ShouldBe(new Sid("S-1-5-21-MCP-APP"));
        metricCaller.GetGroupSids().ShouldBeEmpty();

        // 3. Query.* resolver uses GlobalState ClaimsPrincipal SID rather than HTTP caller's SID.
        var reqRes = await executor.ExecuteAsync(OperationRequestBuilder.New()
            .SetDocument("{ myAccessRequests { requesterSid } }")
            .AddGlobalState(nameof(ClaimsPrincipal), mcpAppPrincipal)
            .Build());
        Autheris.Tests.Unit.GraphQL.TypedAggregateTestHost.Errors(reqRes).ShouldBeEmpty();
        approvalRepo.RequesterLookups.ShouldBe([(new Sid("S-1-5-21-MCP-APP"), (string?)null)]);

        // 4. Mutation.* resolver rejects non-admin GlobalState principal despite HttpContext.User holding GovernanceAdmin.
        var mutRes = await executor.ExecuteAsync(OperationRequestBuilder.New()
            .SetDocument("mutation { syncDataCatalog { success } }")
            .AddGlobalState(nameof(ClaimsPrincipal), mcpAppPrincipal)
            .Build());
        Autheris.Tests.Unit.GraphQL.TypedAggregateTestHost.Errors(mutRes).ShouldContain(e => e.Code == "FORBIDDEN");
        catalogSync.SyncCallCount.ShouldBe(0);
    }

    private sealed class RecordingAggregationService : IGovernedAggregationService
    {
        public List<ClaimsPrincipal> Calls { get; } = [];

        public Task<AggregationResult> ExecuteAggregationAsync(ClaimsPrincipal principal, AggregationQuery query, CancellationToken ct = default)
        {
            Calls.Add(principal);
            return Task.FromResult(Autheris.Tests.Unit.GraphQL.TypedAggregateTestHost.Result([], 2));
        }
    }

    private sealed class RecordingMetricQueryService : ISemanticMetricQueryService
    {
        public List<ClaimsPrincipal> Calls { get; } = [];

        public Task<MetricQueryResult> QueryMetricsAsync(ClaimsPrincipal principal, MetricQueryRequest request, CancellationToken ct = default)
        {
            Calls.Add(principal);
            return Task.FromResult(new MetricQueryResult(
                ["revenue"],
                "v1",
                [],
                new AggregationGroupRow(new Dictionary<string, object?>(), 1, new Dictionary<string, object?>()),
                new AggregationMetadata(5, SmallGroupStrategy.Suppress, false, 0, 0, false, false),
                new Dictionary<string, MetricDefinitionSummary>()));
        }
    }

    private sealed class RecordingConsentApprovalRepo : IConsentApprovalRepository
    {
        public List<(Sid RequesterSid, string? Status)> RequesterLookups { get; } = [];

        public Task<IReadOnlyList<ConsentRequest>> GetRequestsByRequesterSidAsync(Sid requesterSid, TenantId tenantId, string? status = null, CancellationToken ct = default)
        {
            RequesterLookups.Add((requesterSid, status));
            return Task.FromResult<IReadOnlyList<ConsentRequest>>([]);
        }

        public Task<ConsentRequest> CreateConsentRequestAsync(ConsentRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ConsentRequest?> GetConsentRequestAsync(Guid requestId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ConsentRequest>> GetPendingRequestsForApproverAsync(Sid approverSid, TenantId tenantId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ConsentRequest> ApproveConsentRequestStepAsync(Guid requestId, Sid approverSid, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ConsentRequest> RejectConsentRequestAsync(Guid requestId, Sid approverSid, string reason, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ActivateConsentAsync(Guid requestId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteConsentRequestAsync(Guid requestId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class RecordingCatalogSyncService : IDataCatalogSyncService
    {
        public int SyncCallCount { get; private set; }

        public Task<CatalogSyncResult> SyncCatalogAsync(bool dryRun = false, TenantId? restrictToTenant = null, CancellationToken ct = default)
        {
            SyncCallCount++;
            throw new NotSupportedException();
        }

        public Task<TableMetadata?> EnrichOrReferenceTableAsync(TableIdentifier table, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class NoOpTreeQueryService : IGovernedTreeQueryService
    {
        public Task<JsonDocument> ExecuteAsync(ClaimsPrincipal? principal, TreeQueryNode root, IReadOnlyDictionary<string, string[]>? requestHeaders, string operationId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public IReadOnlyList<TreeMaskedColumn> GetMaskedColumns(ClaimsPrincipal? principal, TreeQueryNode root, string operationId)
            => [];

        public void ClearOperation(string operationId) { }
    }

    private sealed class FixedHttpContextAccessor(HttpContext? httpContext) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = httpContext;
    }
}
