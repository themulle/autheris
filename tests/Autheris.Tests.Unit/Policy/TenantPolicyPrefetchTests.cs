namespace Autheris.Tests.Unit.Policy;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Policy;
using Autheris.Application.Policy;
using Autheris.Application.Policy.Services;
using Autheris.Application.Services;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Tests.Unit.Governance;
using Shouldly;
using Xunit;

/// <summary>
/// The protection gate reads the tenant access policy once per batch of decisions (<see cref="TenantPolicyPrefetch"/>) instead of once
/// per table. A prefetch is used only for its own tenant and within <see cref="ConsentBatchPrefetch.MaxAge"/>; otherwise the gate reads
/// the provider as before.
/// </summary>
[Trait("Category", "Security")]
public sealed class TenantPolicyPrefetchTests
{
    private static readonly TenantId Tenant = new("t1");
    private static readonly TenantId OtherTenant = new("t2");
    private static readonly TableIdentifier TableId = new("sales", "dbo", "orders");
    private static readonly Sid CallerSid = new("S-1-5-21-CALLER-1");
    private static readonly Sid EmpGroupSid = new("S-1-5-21-EMP-1");

    // The caller is in the employee group: Internal only when the policy lists that group.
    private static readonly TenantAccessPolicy WithEmployees = TenantAccessPolicy.FromDeploymentDefaults(Tenant, new AccessPolicyOptions
    {
        Internal = new InternalAccessPolicySettings { EmployeeGroupSids = [EmpGroupSid.Value] }
    });

    private static readonly TenantAccessPolicy WithoutEmployees = TenantAccessPolicy.FromDeploymentDefaults(Tenant, new AccessPolicyOptions());

    [Fact]
    public async Task Fresh_prefetch_of_the_caller_tenant_is_used_instead_of_the_provider()
    {
        var provider = new CountingSettingsProvider(WithoutEmployees);
        var prefetch = await LoadAsync(WithEmployees, Tenant, DateTimeOffset.UtcNow);

        var decision = await CreatePolicy(provider).DecideAsync(Query(prefetch), CancellationToken.None);

        decision.IsAllowed.ShouldBeTrue();
        provider.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Stricter_prefetched_policy_is_enforced()
    {
        var provider = new CountingSettingsProvider(WithEmployees);
        var prefetch = await LoadAsync(WithoutEmployees, Tenant, DateTimeOffset.UtcNow);

        var decision = await CreatePolicy(provider).DecideAsync(Query(prefetch), CancellationToken.None);

        decision.IsAllowed.ShouldBeFalse();
        decision.Reasons.ShouldContain(r => r.Step == DecisionStep.Protection && r.Code == "PROTECTION_CLASS_GRANT_REQUIRED");
        provider.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Prefetch_of_another_tenant_is_ignored()
    {
        var provider = new CountingSettingsProvider(WithoutEmployees);
        var prefetch = await LoadAsync(WithEmployees, OtherTenant, DateTimeOffset.UtcNow);

        var decision = await CreatePolicy(provider).DecideAsync(Query(prefetch), CancellationToken.None);

        decision.IsAllowed.ShouldBeFalse();
        provider.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task Stale_prefetch_is_ignored()
    {
        var provider = new CountingSettingsProvider(WithoutEmployees);
        var prefetch = await LoadAsync(WithEmployees, Tenant, DateTimeOffset.UtcNow - ConsentBatchPrefetch.MaxAge - TimeSpan.FromSeconds(1));

        var decision = await CreatePolicy(provider).DecideAsync(Query(prefetch), CancellationToken.None);

        decision.IsAllowed.ShouldBeFalse();
        provider.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task TryGet_rejects_a_time_before_the_read()
    {
        var readAt = DateTimeOffset.UtcNow;
        var prefetch = await LoadAsync(WithEmployees, Tenant, readAt);

        prefetch.TryGet(Tenant, readAt.AddSeconds(-1), out _).ShouldBeFalse();
        prefetch.TryGet(Tenant, readAt, out var policy).ShouldBeTrue();
        policy.ShouldBeSameAs(WithEmployees);
    }

    private static Task<TenantPolicyPrefetch> LoadAsync(TenantAccessPolicy policy, TenantId tenant, DateTimeOffset readAt) =>
        TenantPolicyPrefetch.LoadAsync(new CountingSettingsProvider(policy), tenant, readAt, CancellationToken.None);

    private static TableAccessPolicy CreatePolicy(IAccessPolicySettingsProvider settings) =>
        new(
            new FakeConsentRepository(),
            new ConsentResolutionService(),
            NullConsentCacheService.Instance,
            new GatewayOptions(),
            NullMandatoryRowFilterResolver.Instance,
            protectionProvider: new InternalTableProtection(),
            accessPolicySettings: settings);

    private static TableAccessQuery Query(TenantPolicyPrefetch prefetch) =>
        new(
            CallerSid,
            Tenant,
            new HashSet<Sid> { EmpGroupSid },
            new HashSet<string>(),
            new TableMetadata
            {
                Identifier = TableId,
                Table = new Table { Id = Guid.NewGuid(), SourceName = "sales", SchemaName = "dbo", TableName = "orders", IsActive = true },
                Columns = [new TableColumn { ColumnName = "id", DataType = "int" }]
            })
        {
            TenantPolicyPrefetch = prefetch
        };

    private sealed class InternalTableProtection : IProtectionSnapshotProvider
    {
        public ValueTask<EffectiveProtection> GetAsync(TenantId tenant, TableIdentifier table, CancellationToken ct = default) =>
            ValueTask.FromResult(new EffectiveProtection(
                Container: new ContainerRef(ContainerKind.Domain, Guid.NewGuid()),
                OwnerGroupSid: null,
                TableClass: ProtectionClass.Internal,
                DeclaredTableClass: ProtectionClass.Internal,
                TableMaskCategory: null,
                Columns: new Dictionary<string, ColumnProtection>(StringComparer.OrdinalIgnoreCase),
                Visibility: CatalogVisibilityMode.Open));
    }

    private sealed class CountingSettingsProvider(TenantAccessPolicy policy) : IAccessPolicySettingsProvider
    {
        public int Calls { get; private set; }

        public Task<TenantAccessPolicy> GetEffectiveAsync(TenantId tenant, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(policy);
        }

        public Task<AccessPolicyUpdateResult> UpdateAsync(TenantId tenant, TenantAccessPolicyUpdate update, int expectedVersion, Sid actor, CancellationToken ct = default) =>
            throw new NotImplementedException();
    }
}
