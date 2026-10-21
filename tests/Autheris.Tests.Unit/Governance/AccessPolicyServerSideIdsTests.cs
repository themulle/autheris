namespace Autheris.Tests.Unit.Governance;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Governance;
using Shouldly;
using Xunit;

/// <summary>
/// Security review (access policy endpoints): tenant and policy ids of grantees and selectors come from the route / the saved policy, never
/// from the request body, and approval / creation fields are not accepted from input.
/// </summary>
public sealed class AccessPolicyServerSideIdsTests
{
    private sealed class StubEpochValidationService : IEpochValidationService
    {
        public Task<bool> IsEpochValidAsync(TableIdentifier table, long cachedEpoch, CancellationToken ct = default) => Task.FromResult(true);
        public Task InvalidateEpochAsync(TableIdentifier table, CancellationToken ct = default) => Task.CompletedTask;
        public Task<long> GetCurrentEpochAsync(TableIdentifier table, CancellationToken ct = default) => Task.FromResult(1L);
        public Task<IReadOnlyDictionary<TableIdentifier, long>> GetCurrentEpochsAsync(IEnumerable<TableIdentifier> tables, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<TableIdentifier, long>>(new Dictionary<TableIdentifier, long>());
    }

    private static AccessPolicy HostileBody() => new()
    {
        TenantId = "victim-tenant",
        PolicyId = "body-policy",
        Name = "Engineers read",
        Description = "desc",
        IsActive = true,
        Priority = 200,
        Decision = AccessPolicyDecision.Permit,
        CreatedBy = "S-1-5-21-FORGED-CREATOR",
        CreatedAt = DateTimeOffset.UnixEpoch,
        ApprovedBy = "S-1-5-21-FORGED-APPROVER",
        ApprovedAt = DateTimeOffset.UnixEpoch,
        Grantees = [new AccessPolicyGrantee("victim-tenant", "victim-policy", "workforce", AccessPolicyGranteeType.Group, "engineers")],
        Selectors = [new AccessPolicySelector("victim-tenant", "victim-policy", AccessPolicySelectorType.TablePattern, "finance.*")]
    };

    [Fact]
    public void BuildServerSidePolicy_RebuildsGranteeAndSelectorIds_AndIgnoresApprovalAndCreationFields()
    {
        var createdAt = DateTimeOffset.UtcNow;
        var policy = AccessPolicyEndpoints.BuildServerSidePolicy(HostileBody(), "tenant-a", "route-policy", "S-1-5-21-CALLER", createdAt);

        policy.TenantId.ShouldBe("tenant-a");
        policy.PolicyId.ShouldBe("route-policy");
        policy.CreatedBy.ShouldBe("S-1-5-21-CALLER");
        policy.CreatedAt.ShouldBe(createdAt);
        policy.ApprovedBy.ShouldBeNull();
        policy.ApprovedAt.ShouldBeNull();

        policy.Name.ShouldBe("Engineers read");
        policy.Priority.ShouldBe(200);
        policy.Decision.ShouldBe(AccessPolicyDecision.Permit);

        var grantee = policy.Grantees.ShouldHaveSingleItem();
        grantee.TenantId.ShouldBe("tenant-a");
        grantee.PolicyId.ShouldBe("route-policy");
        grantee.RealmId.ShouldBe("workforce");
        grantee.GranteeId.ShouldBe("engineers");

        var selector = policy.Selectors.ShouldHaveSingleItem();
        selector.TenantId.ShouldBe("tenant-a");
        selector.PolicyId.ShouldBe("route-policy");
        selector.SelectorValue.ShouldBe("finance.*");
    }

    [Fact]
    public void BuildServerSidePolicy_ToleratesMissingGranteeAndSelectorLists()
    {
        var body = HostileBody();
        body.Grantees = null!;
        body.Selectors = null!;

        var policy = AccessPolicyEndpoints.BuildServerSidePolicy(body, "tenant-a", "route-policy", "S-1-5-21-CALLER", DateTimeOffset.UtcNow);

        policy.Grantees.ShouldBeEmpty();
        policy.Selectors.ShouldBeEmpty();
    }

    [Fact]
    public async Task Store_PersistsGranteesAndSelectorsUnderThePolicyBeingSaved_NotTheIdsCarriedByTheRows()
    {
        using var repo = GovernanceStoreFactory.CreateSqlite(new StubEpochValidationService());
        var policy = HostileBody();
        policy.TenantId = "tenant-a";
        policy.PolicyId = "policy-a-" + Guid.NewGuid().ToString("N")[..8];

        await repo.CreatePolicyAsync(policy);

        var loaded = await repo.GetPolicyByIdAsync(new TenantId("tenant-a"), policy.PolicyId);
        loaded.ShouldNotBeNull();
        loaded!.Grantees.ShouldHaveSingleItem().TenantId.ShouldBe("tenant-a");
        loaded.Selectors.ShouldHaveSingleItem().PolicyId.ShouldBe(policy.PolicyId);

        using var cmd = repo.RawConnection().CreateCommand();
        cmd.CommandText = "SELECT (SELECT COUNT(*) FROM ACCESS_POLICY_GRANTEES WHERE tenant_id = 'victim-tenant') + (SELECT COUNT(*) FROM ACCESS_POLICY_SELECTORS WHERE tenant_id = 'victim-tenant')";
        Convert.ToInt32(await cmd.ExecuteScalarAsync()).ShouldBe(0);
    }
}
