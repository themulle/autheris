namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Audit;
using Autheris.Application.Governance.Grants;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Governance.Policies;
using Autheris.Application.Governance.Services;
using Autheris.Application.Interfaces;
using Autheris.Application.Mcp.Tools;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Application.Security.Rebac.Services;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class RebacSubjectCanonicalizationTests
{
    private const string Tenant = "tenant-rebac";
    private const string AdminSid = "S-1-5-21-ADMIN";
    private const string DatasetId = "sales.public.orders";

    [Theory]
    [InlineData("S-1-5-21-3623811015-3361044348-30300820-1013")]
    [InlineData("alice")]
    [InlineData("bob.analyst")]
    [InlineData("user_123")]
    [InlineData("c56a4180-65aa-42ec-a945-5fd21dec0538")]
    public void Parse_BareSid_IsIndividual(string bareSid)
    {
        var result = RebacSubject.Parse(bareSid);
        result.Kind.ShouldBe(RebacSubjectKind.Individual);
        result.Canonical.ShouldBe(bareSid);
        result.IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData("user:alice", "alice")]
    [InlineData("USER:bob", "bob")]
    [InlineData("user:S-1-5-21-1001", "S-1-5-21-1001")]
    [InlineData("sp:app-svc-01", "app-svc-01")]
    [InlineData("SP:client-42", "client-42")]
    public void Parse_UserPrefix_CanonicalizesToBareSid(string raw, string expectedCanonical)
    {
        var result = RebacSubject.Parse(raw);
        result.Kind.ShouldBe(RebacSubjectKind.Individual);
        result.Canonical.ShouldBe(expectedCanonical);
        result.IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("user:")]
    [InlineData("sp:")]
    [InlineData("user:invalid:colons")]
    [InlineData("invalid_prefix:something")]
    [InlineData("group:#empty_relation")]
    [InlineData("unknown_entity:123#member")]
    [InlineData("foo#bar#baz")]
    public void Parse_UnknownForm_IsInvalid(string raw)
    {
        var result = RebacSubject.Parse(raw);
        result.Kind.ShouldBe(RebacSubjectKind.Invalid);
        result.IsValid.ShouldBeFalse();
        result.Canonical.ShouldBe(string.Empty);
    }

    [Fact]
    public async Task RebacEndpoints_AddBareSidWithoutExpiry_Returns400()
    {
        var store = new InMemoryRebacStore(NullLogger<InMemoryRebacStore>.Instance);
        var evaluator = Substitute.For<IRebacEvaluator>();
        var auditWriter = Substitute.For<IAuditWriter>();
        var grantPolicy = new GrantPolicy();

        var context = CreateContext(AdminSid, ["GovernanceAdmin"], Tenant);
        var tuple = new RebacTuple(Tenant, "alice", "viewer", "table:sales.public.orders") { ExpiresAt = null };

        var result = await RebacEndpoints.AddTuplesAsync([tuple], context.Request, store, evaluator, auditWriter, grantPolicy);

        await result.ExecuteAsync(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task RebacEndpoints_AddUserPrefixedSid_StoredAsBareSid()
    {
        var store = new InMemoryRebacStore(NullLogger<InMemoryRebacStore>.Instance);
        var evaluator = Substitute.For<IRebacEvaluator>();
        var auditWriter = Substitute.For<IAuditWriter>();
        var grantPolicy = new GrantPolicy();

        var context = CreateContext(AdminSid, ["GovernanceAdmin"], Tenant);
        var tuple = new RebacTuple(Tenant, "user:alice", "viewer", "table:sales.public.orders")
        {
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7)
        };

        var result = await RebacEndpoints.AddTuplesAsync([tuple], context.Request, store, evaluator, auditWriter, grantPolicy);

        await result.ExecuteAsync(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);

        var storedTuples = await store.GetTuplesAsync(Tenant, user: "alice", relation: "viewer", obj: "table:sales.public.orders");
        storedTuples.Count.ShouldBe(1);
        storedTuples[0].User.ShouldBe("alice");
    }

    [Fact]
    public async Task RebacEndpoints_InvalidSubject_Returns400()
    {
        var store = new InMemoryRebacStore(NullLogger<InMemoryRebacStore>.Instance);
        var evaluator = Substitute.For<IRebacEvaluator>();
        var auditWriter = Substitute.For<IAuditWriter>();

        var context = CreateContext(AdminSid, ["GovernanceAdmin"], Tenant);
        var tuple = new RebacTuple(Tenant, "invalid:user:bad", "viewer", "table:sales.public.orders")
        {
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(1)
        };

        var result = await RebacEndpoints.AddTuplesAsync([tuple], context.Request, store, evaluator, auditWriter, null);

        await result.ExecuteAsync(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        body.ShouldContain("INVALID_REBAC_SUBJECT");
    }

    [Fact]
    public async Task Store_AddNonCanonicalSubject_Throws()
    {
        var store = new InMemoryRebacStore(NullLogger<InMemoryRebacStore>.Instance);
        var tuple = new RebacTuple(Tenant, "user:alice", "viewer", "table:sales.public.orders");

        await Should.ThrowAsync<ArgumentException>(() => store.AddTupleAsync(tuple).AsTask());
    }

    [Fact]
    public async Task Evaluator_InvalidSubject_Denies()
    {
        var store = new InMemoryRebacStore(NullLogger<InMemoryRebacStore>.Instance);
        var options = Options.Create(new GatewayOptions
        {
            Rebac = new RebacOptions { Enabled = true }
        });
        var evaluator = new ZanzibarRebacEvaluator(store, options, NullLogger<ZanzibarRebacEvaluator>.Instance);

        var checkResult = await evaluator.CheckAsync(new RebacCheckRequest(Tenant, "bad:user:format", "viewer", "table:sales.public.orders"));
        checkResult.ShouldBe(RebacCheckResult.Denied);
    }

    [Fact]
    public async Task Evaluator_BareSidAndUserPrefixed_BothMatchStoredBareSid()
    {
        var store = new InMemoryRebacStore(NullLogger<InMemoryRebacStore>.Instance);
        var options = Options.Create(new GatewayOptions
        {
            Rebac = new RebacOptions { Enabled = true }
        });
        var evaluator = new ZanzibarRebacEvaluator(store, options, NullLogger<ZanzibarRebacEvaluator>.Instance);

        const string granteeSid = "S-1-5-21-ALICE";
        // Stored with canonical bare SID
        await store.AddTuplesAsync([
            new RebacTuple(Tenant, granteeSid, "viewer", "table:sales.public.orders")
            {
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(7)
            }
        ]);

        // Check using bare SID
        var bareCheck = await evaluator.CheckAsync(new RebacCheckRequest(Tenant, granteeSid, "viewer", "table:sales.public.orders"));
        bareCheck.Allowed.ShouldBeTrue();

        // Check using user: prefixed SID
        var prefixedCheck = await evaluator.CheckAsync(new RebacCheckRequest(Tenant, $"user:{granteeSid}", "viewer", "table:sales.public.orders"));
        prefixedCheck.Allowed.ShouldBeTrue();
    }

    [Fact]
    public async Task McpAdminPlanApply_BareSidGrant_IsTimeBound()
    {
        var store = Substitute.For<IRebacStore>();
        var planning = Substitute.For<IAccessPlanningService>();

        const string granteeSid = "S-1-5-21-BOB";
        var planReq = new AdminPlanAccessRequest(
            DatasetId: DatasetId,
            Grants: [new(granteeSid, new Dictionary<string, string> { ["id"] = "clear" }, null, DateTimeOffset.UtcNow.AddDays(14))],
            Reason: "MCP grant");

        var planResult = new AdminPlanAccessResult("plan-mcp", DatasetId, [], [], DateTimeOffset.UtcNow.AddMinutes(10));
        planning.PlanAccessAsync(Arg.Any<AdminPlanAccessRequest>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(planResult);

        var sessionContext = new McpSessionContext(
            SessionId: "sess-1",
            ServicePrincipalId: "sp-admin",
            TenantId: Tenant,
            CreatedAt: DateTimeOffset.UtcNow,
            LastActiveAt: DateTimeOffset.UtcNow,
            UserSid: AdminSid);

        var planArgs = System.Text.Json.JsonSerializer.Serialize(planReq);
        var mcpResult = await McpAdminTools.ExecuteAdminToolAsync(McpAdminTools.PlanAccess, planArgs, sessionContext, planning);
        mcpResult.IsSuccess.ShouldBeTrue();

        await planning.Received(1).PlanAccessAsync(
            Arg.Is<AdminPlanAccessRequest>(r => r.Grants.All(g => g.ValidUntil != null)),
            AdminSid,
            Tenant,
            Arg.Any<CancellationToken>());
    }

    private static DefaultHttpContext CreateContext(string sid, string[] roles, string tenant)
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddLogging();
        var context = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            Response = { Body = new MemoryStream() }
        };
        var claims = new List<Claim>
        {
            new(ClaimTypes.PrimarySid, sid),
            new("tenant_id", tenant)
        };
        foreach (var role in roles)
        {
            claims.Add(new(ClaimTypes.Role, role));
        }
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));

        context.Items[SecurityPrincipalContext.ItemKey] = new SecurityPrincipalContext
        {
            UserSid = new Sid(sid),
            TenantId = new TenantId(tenant),
            GroupSids = new HashSet<Sid>(),
            TenantRoles = roles.ToHashSet(),
            ClusterRoles = roles.ToHashSet(),
            AuthenticationScheme = "TestAuth"
        };

        return context;
    }
}
