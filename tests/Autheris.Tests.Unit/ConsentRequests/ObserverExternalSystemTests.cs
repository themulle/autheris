using System;
using System.Collections.Generic;
using System.Security.Claims;
using Autheris.Api.Endpoints;
using Autheris.Api.Security;
using Autheris.Application.ConsentRequests;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.ConsentRequests;

/// <summary>
/// Issue #1161: observer external systems. Role mapping, the observer authorization rule (SEC-1161-03), the request read rule for service
/// principals (SEC-1161-01/-03/-04) and the role a saved system gets (SEC-1161-02).
/// </summary>
public class ObserverExternalSystemTests
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);

    private static ExternalWorkflowSystem System(
        ExternalWorkflowSystemRole role,
        bool enabled = true,
        string tenant = TenantA,
        Guid? id = null) =>
        new(id ?? Guid.NewGuid(), tenant, "cockpit", "client-cockpit", "https://cockpit.invalid/hook", "sec", enabled, "admin", Now,
            enabled ? null : Now, role);

    private static ClaimsPrincipal AppPrincipal(params string[] roles)
    {
        var claims = new List<Claim> { new("idtyp", "app"), new("azp", "client-cockpit"), new("tenant_id", TenantA) };
        foreach (var role in roles)
        {
            claims.Add(new Claim("roles", role));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
    }

    private static ClaimsPrincipal PersonPrincipal(params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.PrimarySid, "S-1-5-21-1-2-3-1001"), new(ClaimTypes.Name, "person") };
        foreach (var role in roles)
        {
            claims.Add(new Claim("roles", role));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
    }

    private static ConsentRequest Request(string tenant = TenantA, Guid? externalSystemId = null) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = new TenantId(tenant),
        RequesterSid = new Sid("S-1-5-21-1-2-3-2002"),
        Status = "PENDING",
        WorkflowMode = externalSystemId.HasValue ? ConsentWorkflowMode.External : ConsentWorkflowMode.Internal,
        ExternalSystemId = externalSystemId,
        Version = 1
    };

    // ---- role mapping (SEC-1161-01) ----

    [Theory]
    [InlineData("WORKFLOW", ExternalWorkflowSystemRole.Workflow)]
    [InlineData("OBSERVER", ExternalWorkflowSystemRole.Observer)]
    [InlineData("observer", ExternalWorkflowSystemRole.Unknown)]
    [InlineData("ADMIN", ExternalWorkflowSystemRole.Unknown)]
    [InlineData("", ExternalWorkflowSystemRole.Unknown)]
    [InlineData(null, ExternalWorkflowSystemRole.Unknown)]
    [Trait("Category", "Security")]
    public void Stored_role_parses_fail_closed(string? stored, ExternalWorkflowSystemRole expected) =>
        ExternalWorkflowSystemRoleMapping.FromSqlString(stored).ShouldBe(expected);

    [Theory]
    [InlineData("Workflow", true, ExternalWorkflowSystemRole.Workflow)]
    [InlineData("observer", true, ExternalWorkflowSystemRole.Observer)]
    [InlineData(" OBSERVER ", true, ExternalWorkflowSystemRole.Observer)]
    [InlineData("Unknown", false, ExternalWorkflowSystemRole.Unknown)]
    [InlineData("admin", false, ExternalWorkflowSystemRole.Unknown)]
    [InlineData(null, false, ExternalWorkflowSystemRole.Unknown)]
    public void Api_role_parses_only_the_two_known_values(string? value, bool ok, ExternalWorkflowSystemRole expected)
    {
        ExternalWorkflowSystemRoleMapping.TryParseApi(value, out var role).ShouldBe(ok);
        role.ShouldBe(expected);
    }

    [Fact]
    public void Unknown_role_is_never_stored()
    {
        ExternalWorkflowSystemRole.Workflow.ToSqlString().ShouldBe("WORKFLOW");
        ExternalWorkflowSystemRole.Observer.ToSqlString().ShouldBe("OBSERVER");
        Should.Throw<ArgumentOutOfRangeException>(() => ExternalWorkflowSystemRole.Unknown.ToSqlString());
    }

    [Fact]
    public void Existing_systems_default_to_workflow() =>
        new ExternalWorkflowSystem(Guid.NewGuid(), TenantA, "n", "c", "https://x.invalid", "s", true, "admin", Now, null)
            .Role.ShouldBe(ExternalWorkflowSystemRole.Workflow);

    [Theory]
    [InlineData(ExternalWorkflowSystemRole.Workflow, true, true, false)]
    [InlineData(ExternalWorkflowSystemRole.Observer, true, false, true)]
    [InlineData(ExternalWorkflowSystemRole.Observer, false, false, false)]
    [InlineData(ExternalWorkflowSystemRole.Workflow, false, false, false)]
    [InlineData(ExternalWorkflowSystemRole.Unknown, true, false, false)]
    [Trait("Category", "Security")]
    public void Activity_flags_require_enabled_and_known_role(ExternalWorkflowSystemRole role, bool enabled, bool workflow, bool observer)
    {
        var system = System(role, enabled);
        system.IsActiveWorkflow.ShouldBe(workflow);
        system.IsActiveObserver.ShouldBe(observer);
    }

    // ---- observer authorization (SEC-1161-03) ----

    [Fact]
    [Trait("Category", "Security")]
    public void Observer_with_read_and_observe_app_roles_is_authorized() =>
        ConsentApiEndpoints.IsAuthorizedObserver(
            System(ExternalWorkflowSystemRole.Observer),
            AppPrincipal(ExternalApiPermissions.ConsentRead, ExternalApiPermissions.ConsentObserve)).ShouldBeTrue();

    [Theory]
    [InlineData("Autheris.Consent.Read")]
    [InlineData("Autheris.Consent.Observe")]
    [InlineData("")]
    [Trait("Category", "Security")]
    public void Registration_alone_is_not_enough(string roles) =>
        ConsentApiEndpoints.IsAuthorizedObserver(
            System(ExternalWorkflowSystemRole.Observer),
            AppPrincipal(roles.Split(',', StringSplitOptions.RemoveEmptyEntries))).ShouldBeFalse();

    [Theory]
    [InlineData(ExternalWorkflowSystemRole.Workflow, true)]
    [InlineData(ExternalWorkflowSystemRole.Unknown, true)]
    [InlineData(ExternalWorkflowSystemRole.Observer, false)]
    [Trait("Category", "Security")]
    public void Only_an_enabled_observer_system_is_authorized(ExternalWorkflowSystemRole role, bool enabled) =>
        ConsentApiEndpoints.IsAuthorizedObserver(
            System(role, enabled),
            AppPrincipal(ExternalApiPermissions.ConsentRead, ExternalApiPermissions.ConsentObserve)).ShouldBeFalse();

    [Fact]
    [Trait("Category", "Security")]
    public void A_person_token_is_never_an_observer() =>
        ConsentApiEndpoints.IsAuthorizedObserver(
            System(ExternalWorkflowSystemRole.Observer),
            PersonPrincipal(ExternalApiPermissions.ConsentRead, ExternalApiPermissions.ConsentObserve)).ShouldBeFalse();

    [Fact]
    [Trait("Category", "Security")]
    public void No_system_is_never_an_observer() =>
        ConsentApiEndpoints.IsAuthorizedObserver(
            null,
            AppPrincipal(ExternalApiPermissions.ConsentRead, ExternalApiPermissions.ConsentObserve)).ShouldBeFalse();

    // ---- request read rule for service principals ----

    [Fact]
    [Trait("Category", "Security")]
    public void Authorized_observer_reads_any_request_of_its_tenant()
    {
        var observer = System(ExternalWorkflowSystemRole.Observer);
        ConsentApiEndpoints.CanReadRequest(Request(), new TenantId(TenantA), null, observer, true, false, false, isAuthorizedObserver: true)
            .ShouldBeTrue();
        ConsentApiEndpoints.CanReadRequest(Request(externalSystemId: Guid.NewGuid()), new TenantId(TenantA), null, observer, true, false, false,
            isAuthorizedObserver: true).ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Observer_never_reads_another_tenants_request()
    {
        var observer = System(ExternalWorkflowSystemRole.Observer);
        ConsentApiEndpoints.CanReadRequest(Request(TenantB), new TenantId(TenantA), null, observer, true, false, false, isAuthorizedObserver: true)
            .ShouldBeFalse();

        // Even a caller tenant that matches the request is refused when the observer system belongs to another tenant.
        var foreignObserver = System(ExternalWorkflowSystemRole.Observer, tenant: TenantB);
        ConsentApiEndpoints.CanReadRequest(Request(), new TenantId(TenantA), null, foreignObserver, true, false, false, isAuthorizedObserver: true)
            .ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Observer_without_authorization_reads_nothing()
    {
        var observer = System(ExternalWorkflowSystemRole.Observer);
        ConsentApiEndpoints.CanReadRequest(Request(), new TenantId(TenantA), null, observer, true, false, false).ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Workflow_system_still_reads_only_its_own_requests()
    {
        var workflow = System(ExternalWorkflowSystemRole.Workflow);
        ConsentApiEndpoints.CanReadRequest(Request(externalSystemId: workflow.Id), new TenantId(TenantA), null, workflow, true, false, false)
            .ShouldBeTrue();
        ConsentApiEndpoints.CanReadRequest(Request(), new TenantId(TenantA), null, workflow, true, false, false).ShouldBeFalse();

        // The observer flag never widens a workflow system's reads.
        ConsentApiEndpoints.CanReadRequest(Request(), new TenantId(TenantA), null, workflow, true, false, false, isAuthorizedObserver: true)
            .ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void System_with_unknown_role_reads_nothing()
    {
        var unknown = System(ExternalWorkflowSystemRole.Unknown);
        ConsentApiEndpoints.CanReadRequest(Request(externalSystemId: unknown.Id), new TenantId(TenantA), null, unknown, true, false, false)
            .ShouldBeFalse();
        ConsentApiEndpoints.CanReadRequest(Request(), new TenantId(TenantA), null, unknown, true, false, false, isAuthorizedObserver: true)
            .ShouldBeFalse();
    }

    // ---- role of a saved system (SEC-1161-02) ----

    [Fact]
    public void Requested_role_wins() =>
        ConsentWorkflowAdminEndpoints.ResolveRole(ExternalWorkflowSystemRole.Observer, System(ExternalWorkflowSystemRole.Workflow))
            .ShouldBe(ExternalWorkflowSystemRole.Observer);

    [Fact]
    public void New_system_defaults_to_workflow() =>
        ConsentWorkflowAdminEndpoints.ResolveRole(null, null).ShouldBe(ExternalWorkflowSystemRole.Workflow);

    [Fact]
    [Trait("Category", "Security")]
    public void Update_without_role_keeps_an_observer_an_observer() =>
        ConsentWorkflowAdminEndpoints.ResolveRole(null, System(ExternalWorkflowSystemRole.Observer))
            .ShouldBe(ExternalWorkflowSystemRole.Observer);

    [Fact]
    [Trait("Category", "Security")]
    public void Unrecognised_stored_role_is_never_carried_over_silently()
    {
        var ex = Should.Throw<ConsentDecisionException>(() =>
            ConsentWorkflowAdminEndpoints.ResolveRole(null, System(ExternalWorkflowSystemRole.Unknown)));
        ex.ErrorCode.ShouldBe(ConsentDecisionErrorCodes.ExternalSystemRoleConflict);
        ex.StatusCode.ShouldBe(409);
    }
}
