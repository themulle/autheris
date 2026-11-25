namespace Autheris.Tests.Unit.GraphQL;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Compliance;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.GraphQL.Mcp;
using Autheris.GraphQL.Security;
using Autheris.GraphQL.Types;
using Autheris.Tests.Common;
using Autheris.Tests.Common.Fakes;
using HotChocolate;
using HotChocolate.Execution;
using HotChocolate.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class GatewayMcpQueryExecutorPrincipalTests
{
    [Fact]
    public async Task Single_principal_resolution()
    {
        var gdprService = new RecordingGdprDisclosureReportService();
        var approvalRepo = new RecordingConsentApprovalRepository();
        var ambientHttpPrincipal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-AMBIENT-HTTP"),
            new Claim(ClaimTypes.Role, "ClusterAdmin"),
            new Claim("tenant_id", "tenant-ambient")
        ], "Bearer"));
        var httpContext = new DefaultHttpContext { User = ambientHttpPrincipal };
        httpContext.Items["TenantId"] = new TenantId("tenant-ambient");
        var httpAccessor = new StaticHttpContextAccessor(httpContext);

        var services = new ServiceCollection();
        services.AddSingleton<IGdprDisclosureReportService>(gdprService);
        services.AddSingleton<IConsentApprovalRepository>(approvalRepo);
        services.AddSingleton<IHttpContextAccessor>(httpAccessor);
        services.AddSingleton(Options.Create(new GatewayOptions()));

        var realExecutor = await services
            .AddGraphQLServer()
            .AddQueryType<Query>()
            .UseDefaultPipeline()
            .BuildRequestExecutorAsync();

        var capturingExecutor = new CapturingRequestExecutor(realExecutor);
        var provider = new FixedExecutorProvider(capturingExecutor);
        var mcpExecutor = new GatewayMcpQueryExecutor(
            provider,
            new StubGatewayExecutionService(),
            NullLogger<GatewayMcpQueryExecutor>.Instance,
            new StaticOptionsMonitor<GatewayOptions>(new GatewayOptions()),
            new PersistedToolValidator());

        // 1. Curated GraphQL tool with GovernanceAdmin + ClusterAdmin user session.
        var adminSession = new McpSessionContext(
            "sess-admin",
            "agent-client-1",
            "tenant-a",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(5),
            UserSid: "S-1-5-21-GOV-USER",
            Roles: ["GovernanceAdmin", "ClusterAdmin"],
            GroupSids: ["S-1-5-21-1-2-3-5001"]);
        var curatedTool = new McpToolDefinition(
            "gdpr_report",
            "GDPR disclosure report",
            """{"type":"object","properties":{}}""",
            "{ gdprDataDisclosureReport { subjectSid } }");

        var curatedResult = await mcpExecutor.ExecuteOperationAsync(curatedTool, "{}", adminSession);
        curatedResult.ShouldNotContain("\"isError\":true");

        var curatedReq = (OperationRequest)capturingExecutor.Requests[^1];
        var curatedPrincipal = curatedReq.ContextData![nameof(ClaimsPrincipal)].ShouldBeOfType<ClaimsPrincipal>();
        var curatedContext = curatedReq.ContextData[nameof(CallerSecurityContext)].ShouldBeOfType<CallerSecurityContext>();

        curatedPrincipal.GetUserSid().ShouldBe(new Sid("S-1-5-21-GOV-USER"));
        curatedPrincipal.FindFirst("sub")?.Value.ShouldBe("S-1-5-21-GOV-USER");
        curatedPrincipal.FindFirst("client_id")?.Value.ShouldBe("agent-client-1");
        curatedPrincipal.Identity!.AuthenticationType.ShouldBe("McpAuth");

        curatedContext.UserSid.ShouldBe(new Sid("S-1-5-21-GOV-USER"));
        curatedContext.TenantId.ShouldBe(new TenantId("tenant-a"));
        curatedContext.GroupSids.ShouldBe([new Sid("S-1-5-21-1-2-3-5001")]);
        curatedContext.Roles.ShouldContain("GovernanceAdmin");
        curatedContext.Roles.ShouldContain("ClusterAdmin");
        curatedContext.IsGovernanceAdmin.ShouldBeTrue();
        curatedContext.IsClusterAdmin.ShouldBeTrue();

        var capturedGdprCaller = gdprService.Calls.ShouldHaveSingleItem();
        capturedGdprCaller.ShouldBe(curatedContext);
        capturedGdprCaller.UserSid.ShouldNotBe(new Sid("S-1-5-21-AMBIENT-HTTP"));

        // 2. Tenant-prefixed ClusterAdmin never confers ClusterAdmin; tenant-prefixed GovernanceAdmin applies to its own tenant only.
        var prefixedSession = new McpSessionContext(
            "sess-prefixed",
            "agent-client-2",
            "tenant-a",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(5),
            UserSid: "S-1-5-21-SCOPED-USER",
            Roles: ["tenant-a:ClusterAdmin", "tenant-a:GovernanceAdmin"],
            GroupSids: []);
        var prefixedPrincipal = GraphQlCallerPrincipal.BuildMcpSessionPrincipal(prefixedSession);
        var prefixedContext = GraphQlCallerPrincipal.ResolveCallerSecurityContext(prefixedPrincipal, new TenantId("tenant-a"));
        prefixedContext.IsClusterAdmin.ShouldBeFalse();
        prefixedContext.IsGovernanceAdmin.ShouldBeTrue();

        var crossTenantPrefixedSession = prefixedSession with { Roles = ["tenant-b:GovernanceAdmin"] };
        var crossTenantPrincipal = GraphQlCallerPrincipal.BuildMcpSessionPrincipal(crossTenantPrefixedSession);
        var crossTenantContext = GraphQlCallerPrincipal.ResolveCallerSecurityContext(crossTenantPrincipal, new TenantId("tenant-a"));
        crossTenantContext.IsClusterAdmin.ShouldBeFalse();
        crossTenantContext.IsGovernanceAdmin.ShouldBeFalse();

        // 3. App-only session keeps zero group SIDs even if session.GroupSids is populated, and query_graphql uses the shared helper.
        var appSession = new McpSessionContext(
            "sess-app",
            "app-spn-42",
            "tenant-a",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(5),
            UserSid: null,
            Roles: ["Reader"],
            GroupSids: ["S-1-5-21-1-2-3-9999"]);
        var queryGraphQlTool = new McpToolDefinition(
            McpDatasetTools.QueryGraphQl,
            "Query GraphQL",
            """{"type":"object","properties":{"query":{"type":"string"}}}""",
            "query_graphql");

        var gqlResult = await mcpExecutor.ExecuteOperationAsync(
            queryGraphQlTool,
            """{"query":"{ myAccessRequests { requesterSid } }"}""",
            appSession);
        gqlResult.ShouldNotContain("\"isError\":true");

        var gqlReq = (OperationRequest)capturingExecutor.Requests[^1];
        var gqlPrincipal = gqlReq.ContextData![nameof(ClaimsPrincipal)].ShouldBeOfType<ClaimsPrincipal>();
        var gqlContext = gqlReq.ContextData[nameof(CallerSecurityContext)].ShouldBeOfType<CallerSecurityContext>();

        gqlPrincipal.GetGroupSids().ShouldBeEmpty();
        gqlContext.UserSid.ShouldBe(new Sid("app-spn-42"));
        gqlContext.GroupSids.ShouldBeEmpty();
        gqlContext.TenantId.ShouldBe(new TenantId("tenant-a"));
        gqlContext.IsGovernanceAdmin.ShouldBeFalse();
        gqlContext.IsClusterAdmin.ShouldBeFalse();
        approvalRepo.LastRequesterSid.ShouldBe(new Sid("app-spn-42"));
        approvalRepo.LastTenantId.ShouldBe(new TenantId("tenant-a"));
    }

    [Fact]
    public void Global_state_set_once()
    {
        var session = new McpSessionContext(
            "sess-once",
            "spn-once",
            "tenant-a",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(5),
            UserSid: "S-1-5-21-ONCE",
            Roles: ["GovernanceAdmin"],
            GroupSids: ["S-1-5-21-1-2-3-4001"]);
        var principal = GraphQlCallerPrincipal.BuildMcpSessionPrincipal(session);

        var builder = OperationRequestBuilder.New().SetDocument("{ __typename }");
        GraphQlCallerPrincipal.SetMcpSessionState(builder, principal, session);

        // Calling SetMcpSessionState a second time on the same builder must fail closed.
        Should.Throw<InvalidOperationException>(() => GraphQlCallerPrincipal.SetMcpSessionState(builder, principal, session));

        var built = (OperationRequest)builder.Build();
        built.ContextData.ShouldNotBeNull();
        built.ContextData[nameof(ClaimsPrincipal)].ShouldBeSameAs(principal);
        var callerContext = built.ContextData[nameof(CallerSecurityContext)].ShouldBeOfType<CallerSecurityContext>();
        callerContext.UserSid.ShouldBe(new Sid("S-1-5-21-ONCE"));
        callerContext.IsGovernanceAdmin.ShouldBeTrue();

        // Pre-configured ClaimsPrincipal global state must not be silently overwritten.
        var existingPrincipal = new ClaimsPrincipal();
        var prePrincipalBuilder = OperationRequestBuilder.New()
            .SetDocument("{ __typename }")
            .SetGlobalState(nameof(ClaimsPrincipal), existingPrincipal);
        Should.Throw<InvalidOperationException>(() => GraphQlCallerPrincipal.SetMcpSessionState(prePrincipalBuilder, principal, session));
        ((OperationRequest)prePrincipalBuilder.Build()).ContextData![nameof(ClaimsPrincipal)].ShouldBeSameAs(existingPrincipal);

        // Pre-configured CallerSecurityContext global state must fail closed and leave no partial ClaimsPrincipal state.
        var preContextBuilder = OperationRequestBuilder.New()
            .SetDocument("{ __typename }")
            .SetGlobalState(nameof(CallerSecurityContext), callerContext);
        Should.Throw<InvalidOperationException>(() => GraphQlCallerPrincipal.SetMcpSessionState(preContextBuilder, principal, session));
        var partialReq = (OperationRequest)preContextBuilder.Build();
        (partialReq.ContextData?.ContainsKey(nameof(ClaimsPrincipal)) ?? false).ShouldBeFalse();
    }

    private sealed class FixedExecutorProvider(IRequestExecutor executor) : IRequestExecutorProvider
    {
        public ImmutableArray<string> SchemaNames { get; } = [ISchemaDefinition.DefaultName];

        public ValueTask<IRequestExecutor> GetExecutorAsync(string? schemaName = null, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(executor);
    }

    private sealed class CapturingRequestExecutor(IRequestExecutor inner) : IRequestExecutor
    {
        public List<IOperationRequest> Requests { get; } = [];
        public ulong Version => inner.Version;
        public ISchemaDefinition Schema => inner.Schema;
        public IFeatureCollection Features => inner.Features;

        public Task<IExecutionResult> ExecuteAsync(IOperationRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return inner.ExecuteAsync(request, cancellationToken);
        }

        public Task<IResponseStream> ExecuteBatchAsync(OperationRequestBatch requestBatch, CancellationToken cancellationToken = default) =>
            inner.ExecuteBatchAsync(requestBatch, cancellationToken);
    }

    private sealed class StaticHttpContextAccessor(HttpContext? httpContext) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = httpContext;
    }

    private sealed class RecordingGdprDisclosureReportService : IGdprDisclosureReportService
    {
        public List<CallerSecurityContext> Calls { get; } = [];

        public Task<GdprDisclosureReport> GetGdprDataDisclosureReportAsync(
            TableIdentifier? tableId,
            Sid? subjectSid,
            int timeWindowDays = 365,
            CallerSecurityContext? callerContext = null,
            CancellationToken ct = default)
        {
            Calls.Add(callerContext!);
            return Task.FromResult(new GdprDisclosureReport(
                null,
                subjectSid?.Value ?? string.Empty,
                DateTimeOffset.UtcNow,
                timeWindowDays,
                0,
                [],
                [],
                "Article 15"));
        }
    }

    private sealed class RecordingConsentApprovalRepository : IConsentApprovalRepository
    {
        public Sid? LastRequesterSid { get; private set; }
        public TenantId? LastTenantId { get; private set; }

        public Task<IReadOnlyList<ConsentRequest>> GetRequestsByRequesterSidAsync(Sid requesterSid, TenantId tenantId, string? status = null, CancellationToken ct = default)
        {
            LastRequesterSid = requesterSid;
            LastTenantId = tenantId;
            return Task.FromResult<IReadOnlyList<ConsentRequest>>([]);
        }

        public Task<ConsentRequest> CreateConsentRequestAsync(ConsentRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ConsentRequest?> GetConsentRequestAsync(Guid requestId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ConsentRequest>> GetPendingRequestsForApproverAsync(Sid approverSid, TenantId tenantId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ConsentRequest> RejectConsentRequestAsync(Guid requestId, Sid approverSid, string reason, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ActivateConsentAsync(Guid requestId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteConsentRequestAsync(Guid requestId, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
