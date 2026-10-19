namespace Autheris.Tests.Unit.Identity.Attributes;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Identity.Attributes;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Infrastructure.Persistence;
using Autheris.Tests.Unit.TestDoubles;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>PLAN-GOV-129 test 12 (endpoints), ST-129-13 (REST), ST-129-16 (BC-129-03, BC-129-10, BC-129-13): the administration API.</summary>
[Trait("Category", "Security")]
public sealed class CallerAttributeAdminEndpointTests : IDisposable
{
    private const string Tenant = "tenant_a";

    private readonly SqliteGovernanceRepository _store = GovernanceStoreFactory.CreateSqlite(
        Substitute.For<IEpochValidationService>(),
        Options.Create(new GatewayOptions { GovernanceDb = new GovernanceDbOptions { ConnectionString = $"Data Source=cae_{Guid.NewGuid():N};Mode=Memory;Cache=Shared" } }));

    private readonly CallerAttributeAdministrationService _service;
    private readonly CallerAttributeRegistry _registry;

    public CallerAttributeAdminEndpointTests()
    {
        var options = Options.Create(new GatewayOptions { CallerAttributes = new CallerAttributeOptions { Enabled = true, Claims = CallerAttributeTestSupport.Options().Claims } });
        _registry = new CallerAttributeRegistry(_store, options);
        _service = new CallerAttributeAdministrationService(_store, _registry, Substitute.For<IAuditLogRepository>(), options, TimeProvider.System,
            [new ClaimsAttributeDefinitionValidator(options)]);
    }

    public void Dispose()
    {
        _registry.Dispose();
        _store.Dispose();
    }

    private static HttpContext Context(string sid, object? body = null, string? query = null, params string[] roles)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().AddAuditWriterOver(Substitute.For<IAuditLogRepository>()).BuildServiceProvider()
        };
        context.Items[SecurityPrincipalContext.ItemKey] = new SecurityPrincipalContext
        {
            UserSid = new Sid(sid),
            TenantId = new TenantId(Tenant),
            GroupSids = new HashSet<Sid>(),
            TenantRoles = new HashSet<string>(roles.Where(r => r is not ("ClusterAdmin" or "GovernanceAdmin"))),
            ClusterRoles = new HashSet<string>(roles.Where(r => r is "ClusterAdmin" or "GovernanceAdmin")),
            AuthenticationScheme = "Test"
        };
        context.Response.Body = new MemoryStream();
        if (query != null) context.Request.QueryString = new QueryString(query);
        if (body != null)
        {
            context.Request.ContentType = "application/json";
            context.Request.Body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(body));
        }

        return context;
    }

    private static async Task<(int Status, string Body)> Run(IResult result, HttpContext context)
    {
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        return (context.Response.StatusCode, await new StreamReader(context.Response.Body).ReadToEndAsync());
    }

    private static object ClaimsBody(string? tenant = null) => new
    {
        tenant,
        value_type = "Text",
        cardinality = "List",
        max_values = 20,
        ttl_seconds = 60,
        provider = new { kind = "claims", config = new Dictionary<string, string> { ["claim"] = "department", ["issuer"] = CallerAttributeTestSupport.TrustedIssuer, ["idpControlledAck"] = "true" } },
        description = "cost centre"
    };

    private static string HashOf(string body) => JsonDocument.Parse(body).RootElement.GetProperty("hash").GetString()!;

    [Theory]
    [InlineData("Consumer")]
    [InlineData("DataOwner")]
    [InlineData("FilterAdmin")]
    [InlineData("CallerAttributeApprover")]
    [InlineData("SecurityAuditor")]
    public async Task Put_WithoutGovernanceAdmin_Returns403_AndStoresNothing(string role)
    {
        var context = Context("alice", ClaimsBody(), roles: role);
        (await Run(await CallerAttributeAdminEndpoints.PutDefinitionAsync("cost_center", context, _service), context)).Status.ShouldBe(StatusCodes.Status403Forbidden);
        (await _store.LoadCallerAttributeSnapshotAsync()).Attributes.ShouldBeEmpty();
    }

    [Fact]
    public async Task FourEyes_Flow_ThroughTheApi()
    {
        var put = Context("alice", ClaimsBody(), roles: "GovernanceAdmin");
        var (status, body) = await Run(await CallerAttributeAdminEndpoints.PutDefinitionAsync("cost_center", put, _service), put);
        status.ShouldBe(StatusCodes.Status202Accepted);
        var hash = HashOf(body);

        var self = Context("alice", query: $"?hash={hash}", roles: "GovernanceAdmin");
        (await Run(await CallerAttributeAdminEndpoints.ApproveDefinitionAsync("cost_center", self, _service), self)).Status.ShouldBe(StatusCodes.Status409Conflict);

        var stale = Context("bob", query: "?hash=stale", roles: "GovernanceAdmin");
        (await Run(await CallerAttributeAdminEndpoints.ApproveDefinitionAsync("cost_center", stale, _service), stale)).Status.ShouldBe(StatusCodes.Status409Conflict);

        var missing = Context("bob", roles: "GovernanceAdmin");
        (await Run(await CallerAttributeAdminEndpoints.ApproveDefinitionAsync("cost_center", missing, _service), missing)).Status.ShouldBe(StatusCodes.Status400BadRequest);

        var ok = Context("bob", query: $"?hash={hash}", roles: "GovernanceAdmin");
        (await Run(await CallerAttributeAdminEndpoints.ApproveDefinitionAsync("cost_center", ok, _service), ok)).Status.ShouldBe(StatusCodes.Status200OK);
        (await _registry.GetAsync(default)).TryGetDefinition(new TenantId(Tenant), "cost_center", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task InvalidBodies_AreBadRequests()
    {
        foreach (var body in new object[]
        {
            new { value_type = "Nonsense", provider = new { kind = "claims" } },
            new { value_type = "Text" },
            new { value_type = "Text", provider = new { kind = "claims", config = new Dictionary<string, string> { ["claim"] = "x" } } }   // no issuer
        })
        {
            var context = Context("alice", body, roles: "GovernanceAdmin");
            (await Run(await CallerAttributeAdminEndpoints.PutDefinitionAsync("cost_center", context, _service), context)).Status.ShouldBe(StatusCodes.Status400BadRequest);
        }

        var badName = Context("alice", ClaimsBody(), roles: "GovernanceAdmin");
        (await Run(await CallerAttributeAdminEndpoints.PutDefinitionAsync("Bad__Name", badName, _service), badName)).Status.ShouldBe(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task AnotherTenant_NeedsClusterAdmin()
    {
        var context = Context("alice", ClaimsBody(tenant: "tenant_b"), roles: "GovernanceAdmin");
        (await Run(await CallerAttributeAdminEndpoints.PutDefinitionAsync("cost_center", context, _service), context)).Status.ShouldBe(StatusCodes.Status403Forbidden);
        var cluster = Context("alice", ClaimsBody(tenant: "tenant_b"), roles: "ClusterAdmin");
        (await Run(await CallerAttributeAdminEndpoints.PutDefinitionAsync("cost_center", cluster, _service), cluster)).Status.ShouldBe(StatusCodes.Status202Accepted);
    }

    [Fact]
    public async Task List_IsLimitedToTheTenant_AndCarriesNoValues()
    {
        var put = Context("alice", ClaimsBody(), roles: "GovernanceAdmin");
        await Run(await CallerAttributeAdminEndpoints.PutDefinitionAsync("cost_center", put, _service), put);
        var other = Context("alice", ClaimsBody(tenant: "tenant_b"), roles: "ClusterAdmin");
        await Run(await CallerAttributeAdminEndpoints.PutDefinitionAsync("cost_center", other, _service), other);

        var list = Context("auditor", roles: "SecurityAuditor");
        var (status, body) = await Run(await CallerAttributeAdminEndpoints.ListAsync(list, _service), list);
        status.ShouldBe(StatusCodes.Status200OK);
        var attributes = JsonDocument.Parse(body).RootElement.GetProperty("attributes");
        attributes.GetArrayLength().ShouldBe(1);
        attributes[0].GetProperty("tenant").GetString().ShouldBe(Tenant);

        var denied = Context("nobody", roles: "Consumer");
        (await Run(await CallerAttributeAdminEndpoints.ListAsync(denied, _service), denied)).Status.ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task ServiceSet_Flow_ThroughTheApi_NeedsAnActiveAttribute_AndNeverReturnsValues()
    {
        var early = Context("alice", new { application_id = "app-1", attribute = "cost_center", values = new[] { "secret-1" } }, roles: "GovernanceAdmin");
        (await Run(await CallerAttributeAdminEndpoints.PutServiceSetAsync(early, _service), early)).Status.ShouldBe(StatusCodes.Status400BadRequest);

        var put = Context("alice", ClaimsBody(), roles: "GovernanceAdmin");
        var hash = HashOf((await Run(await CallerAttributeAdminEndpoints.PutDefinitionAsync("cost_center", put, _service), put)).Body);
        var approve = Context("bob", query: $"?hash={hash}", roles: "GovernanceAdmin");
        await Run(await CallerAttributeAdminEndpoints.ApproveDefinitionAsync("cost_center", approve, _service), approve);

        var putSet = Context("alice", new { application_id = "app-1", attribute = "cost_center", values = new[] { "secret-1" } }, roles: "GovernanceAdmin");
        var (status, body) = await Run(await CallerAttributeAdminEndpoints.PutServiceSetAsync(putSet, _service), putSet);
        status.ShouldBe(StatusCodes.Status202Accepted);
        var setHash = HashOf(body);

        // a service set needs the separate approver role
        var noRole = Context("bob", new { application_id = "app-1", attribute = "cost_center", hash = setHash }, roles: "GovernanceAdmin");
        (await Run(await CallerAttributeAdminEndpoints.ApproveServiceSetAsync(noRole, _service), noRole)).Status.ShouldBe(StatusCodes.Status403Forbidden);

        var approver = Context("bob", new { application_id = "app-1", attribute = "cost_center", hash = setHash }, roles: ["GovernanceAdmin", "CallerAttributeApprover"]);
        (await Run(await CallerAttributeAdminEndpoints.ApproveServiceSetAsync(approver, _service), approver)).Status.ShouldBe(StatusCodes.Status200OK);

        var list = Context("auditor", roles: "SecurityAuditor");
        var (listStatus, listBody) = await Run(await CallerAttributeAdminEndpoints.ListServiceSetsAsync(list, _service), list);
        listStatus.ShouldBe(StatusCodes.Status200OK);
        listBody.ShouldNotContain("secret-1");
        listBody.ShouldContain("\"value_count\":1");
    }

    [Fact]
    public async Task Delete_And_Reject_RequireGovernanceAdmin_AndAreFourEyes()
    {
        var put = Context("alice", ClaimsBody(), roles: "GovernanceAdmin");
        var hash = HashOf((await Run(await CallerAttributeAdminEndpoints.PutDefinitionAsync("cost_center", put, _service), put)).Body);
        var approve = Context("bob", query: $"?hash={hash}", roles: "GovernanceAdmin");
        await Run(await CallerAttributeAdminEndpoints.ApproveDefinitionAsync("cost_center", approve, _service), approve);

        var denied = Context("carol", roles: "Consumer");
        (await Run(await CallerAttributeAdminEndpoints.DeleteDefinitionAsync("cost_center", denied, _service), denied)).Status.ShouldBe(StatusCodes.Status403Forbidden);

        var del = Context("carol", roles: "GovernanceAdmin");
        (await Run(await CallerAttributeAdminEndpoints.DeleteDefinitionAsync("cost_center", del, _service), del)).Status.ShouldBe(StatusCodes.Status202Accepted);
        var selfApprove = Context("carol", query: $"?hash={hash}", roles: "GovernanceAdmin");
        (await Run(await CallerAttributeAdminEndpoints.ApproveDefinitionAsync("cost_center", selfApprove, _service), selfApprove)).Status.ShouldBe(StatusCodes.Status409Conflict);

        var reject = Context("bob", roles: "GovernanceAdmin");
        (await Run(await CallerAttributeAdminEndpoints.RejectDefinitionAsync("cost_center", reject, _service), reject)).Status.ShouldBe(StatusCodes.Status200OK);
        (await _registry.GetAsync(default)).TryGetDefinition(new TenantId(Tenant), "cost_center", out _).ShouldBeTrue();   // still active
    }

    // ---- simulation ------------------------------------------------------------------------------------------------------------------

    private CallerAttributeSimulationService Simulation()
    {
        var resolver = Substitute.For<ICallerAttributeResolver>();
        resolver.IsEnabled.Returns(true);
        resolver.ResolveAsync(Arg.Any<CallerSubject>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(new CallerAttributeSet(new Dictionary<string, CallerAttributeResult>
            {
                ["cost_center"] = CallerAttributeResult.Resolved(new CallerAttributeValue(["clear-secret-9"], TrinoSqlEngine.Ast.Emit.SqlParameterType.String))
            }));
        return new CallerAttributeSimulationService(resolver, Substitute.For<IAuditLogRepository>(),
            Options.Create(new GatewayOptions { CallerAttributes = new CallerAttributeOptions { Enabled = true } }));
    }

    private static object SimBody(bool reveal = false, string? reason = null, string? tenant = null) => new
    {
        tenant, key_kind = "EntraObjectId", key_value = "11111111-1111-1111-1111-111111111111", attributes = new[] { "cost_center" }, reveal, reason
    };

    [Fact]
    public async Task ST_129_16_Simulation_DefaultHasNoClearValues_RevealNeedsTheInspectorRole()
    {
        var simulation = Simulation();
        var plain = Context("admin", SimBody(), roles: "GovernanceAdmin");
        var (status, body) = await Run(await CallerAttributeAdminEndpoints.SimulateAsync(plain, simulation), plain);
        status.ShouldBe(StatusCodes.Status200OK);
        body.ShouldNotContain("clear-secret-9");
        body.ShouldContain("value_hashes");

        var noRole = Context("admin", SimBody(reveal: true, reason: "case 7"), roles: "GovernanceAdmin");
        (await Run(await CallerAttributeAdminEndpoints.SimulateAsync(noRole, simulation), noRole)).Status.ShouldBe(StatusCodes.Status403Forbidden);

        var inspector = Context("admin", SimBody(reveal: true, reason: "case 7"), roles: ["GovernanceAdmin", "CallerAttributeInspector"]);
        var revealed = await Run(await CallerAttributeAdminEndpoints.SimulateAsync(inspector, simulation), inspector);
        revealed.Status.ShouldBe(StatusCodes.Status200OK);
        revealed.Body.ShouldContain("clear-secret-9");
    }

    [Theory]
    [InlineData("Consumer")]
    [InlineData("SecurityAuditor")]
    [InlineData("CallerAttributeInspector")]
    public async Task Simulation_WithoutGovernanceOrFilterAdmin_Returns403(string role)
    {
        var context = Context("admin", SimBody(), roles: role);
        (await Run(await CallerAttributeAdminEndpoints.SimulateAsync(context, Simulation()), context)).Status.ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task Simulation_ForASubjectOfAnotherTenant_IsForbidden()
    {
        var context = Context("admin", SimBody(tenant: "tenant_b"), roles: "GovernanceAdmin");
        (await Run(await CallerAttributeAdminEndpoints.SimulateAsync(context, Simulation()), context)).Status.ShouldBe(StatusCodes.Status403Forbidden);
    }
}
