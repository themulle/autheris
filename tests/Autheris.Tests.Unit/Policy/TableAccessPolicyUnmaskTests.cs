namespace Autheris.Tests.Unit.Policy;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Policy;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.Policy.Services;
using Autheris.Application.Policy;
using Autheris.Application.Policy.Protection;
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

[Trait("Category", "Security")]
public sealed class TableAccessPolicyUnmaskTests
{
    private static readonly TenantId Tenant = new("t1");
    private static readonly TableIdentifier TableId = new("sales", "dbo", "orders");
    private static readonly Sid CallerSid = new("S-1-5-21-CALLER-1");
    private static readonly Sid GroupSid = new("S-1-5-21-GROUP-1");

    private static TableMetadata MakeMetadata() => new()
    {
        Identifier = TableId,
        Table = new Table { Id = Guid.NewGuid(), SourceName = "sales", SchemaName = "dbo", TableName = "orders", IsActive = true },
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "int" },
            new TableColumn { ColumnName = "amount", DataType = "decimal" },
            new TableColumn { ColumnName = "password_hash", DataType = "varchar" },
            new TableColumn { ColumnName = "email", DataType = "varchar" }
        ]
    };

    private static EffectiveProtection Protection(
        IReadOnlyDictionary<string, ColumnProtection>? columns = null,
        string? tableMask = null,
        ProtectionClass tableClass = ProtectionClass.Public) =>
        new(
            Container: new ContainerRef(ContainerKind.Domain, Guid.NewGuid()),
            OwnerGroupSid: null,
            TableClass: tableClass,
            DeclaredTableClass: tableClass,
            TableMaskCategory: tableMask,
            Columns: columns ?? new Dictionary<string, ColumnProtection>(StringComparer.OrdinalIgnoreCase),
            Visibility: CatalogVisibilityMode.Open);

    private static IReadOnlyDictionary<string, ColumnProtection> Columns(params (string Name, string? Category)[] items) =>
        items.ToDictionary(i => i.Name, i => new ColumnProtection(ProtectionClass.Public, i.Category), StringComparer.OrdinalIgnoreCase);

    private static TableAccessPolicy CreatePolicy(EffectiveProtection protection, IUnmaskGrantResolver? unmask = null) =>
        new(
            new FakeConsentRepository(),
            new ConsentResolutionService(),
            NullConsentCacheService.Instance,
            new GatewayOptions(),
            NullMandatoryRowFilterResolver.Instance,
            protectionProvider: new StubProtectionProvider(protection),
            accessPolicySettings: new StubSettings(TenantAccessPolicy.FromDeploymentDefaults(Tenant, new AccessPolicyOptions())),
            unmaskResolver: unmask);

    private static TableAccessQuery Query(params Sid[] groups) =>
        new(CallerSid, Tenant, new HashSet<Sid>(groups), new HashSet<string>(), MakeMetadata());

    [Fact]
    public async Task Table_mask_category_masks_all_columns()
    {
        var decision = await CreatePolicy(Protection(tableMask: "PII")).DecideAsync(Query(), CancellationToken.None);

        decision.IsAllowed.ShouldBeTrue();
        foreach (var column in new[] { "id", "amount", "password_hash", "email" })
        {
            decision.GetColumnAccess(column).ShouldBe(ColumnAccessLevel.Mask);
        }
    }

    [Fact]
    public async Task Financial_column_masked_without_unmask_grant()
    {
        var decision = await CreatePolicy(Protection(Columns(("amount", "FINANCIAL")))).DecideAsync(Query(), CancellationToken.None);

        decision.GetColumnAccess("amount").ShouldBe(ColumnAccessLevel.Mask);
        decision.GetColumnAccess("id").ShouldBe(ColumnAccessLevel.Clear);
    }

    [Fact]
    public async Task Credentials_column_masked_without_unmask_grant()
    {
        var decision = await CreatePolicy(Protection(Columns(("password_hash", "CREDENTIALS")))).DecideAsync(Query(), CancellationToken.None);

        decision.GetColumnAccess("password_hash").ShouldBe(ColumnAccessLevel.Mask);
    }

    [Fact]
    public async Task Unknown_category_masks_with_type_default()
    {
        var decision = await CreatePolicy(Protection(Columns(("email", "NO_SUCH_CATEGORY")))).DecideAsync(Query(), CancellationToken.None);

        decision.GetColumnAccess("email").ShouldBe(ColumnAccessLevel.Mask);
        decision.GetColumnAccess("email").ShouldNotBe(ColumnAccessLevel.Clear);
    }

    [Fact]
    public async Task Missing_mask_rule_falls_back_not_clear()
    {
        var decision = await CreatePolicy(Protection(Columns(("email", "PII_SPECIAL")))).DecideAsync(Query(), CancellationToken.None);

        decision.GetColumnAccess("email").ShouldBe(ColumnAccessLevel.Mask);
    }

    [Fact]
    public async Task Column_category_overrides_only_that_column()
    {
        var decision = await CreatePolicy(Protection(Columns(("email", "PII")))).DecideAsync(Query(), CancellationToken.None);

        decision.GetColumnAccess("email").ShouldBe(ColumnAccessLevel.Mask);
        decision.GetColumnAccess("amount").ShouldBe(ColumnAccessLevel.Clear);
    }

    [Fact]
    public async Task Public_table_with_masked_column_stays_masked()
    {
        var decision = await CreatePolicy(Protection(Columns(("email", "PII")), tableClass: ProtectionClass.Public))
            .DecideAsync(Query(), CancellationToken.None);

        decision.IsAllowed.ShouldBeTrue();
        decision.GetColumnAccess("email").ShouldBe(ColumnAccessLevel.Mask);
    }

    [Fact]
    public async Task Unmask_grant_makes_masked_columns_clear()
    {
        var resolver = new FakeUnmaskResolver((_, _) => new UnmaskCoverage(wholeTable: true, new HashSet<string>()));
        var decision = await CreatePolicy(Protection(tableMask: "PII"), resolver).DecideAsync(Query(), CancellationToken.None);

        decision.IsAllowed.ShouldBeTrue();
        decision.GetColumnAccess("email").ShouldBe(ColumnAccessLevel.Clear);
        decision.GetColumnAccess("amount").ShouldBe(ColumnAccessLevel.Clear);
    }

    [Fact]
    public async Task Column_scoped_unmask_clears_only_that_column()
    {
        var resolver = new FakeUnmaskResolver((_, _) => new UnmaskCoverage(false, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "email" }));
        var decision = await CreatePolicy(Protection(tableMask: "PII"), resolver).DecideAsync(Query(), CancellationToken.None);

        decision.GetColumnAccess("email").ShouldBe(ColumnAccessLevel.Clear);
        decision.GetColumnAccess("amount").ShouldBe(ColumnAccessLevel.Mask);
    }

    [Fact]
    public async Task Unmask_without_data_access_yields_nothing()
    {
        var resolver = new FakeUnmaskResolver((_, _) => new UnmaskCoverage(wholeTable: true, new HashSet<string>()));
        var decision = await CreatePolicy(Protection(tableMask: "PII", tableClass: ProtectionClass.Confidential), resolver)
            .DecideAsync(Query(), CancellationToken.None);

        decision.IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Group_unmask_ends_with_group_exit()
    {
        var resolver = new FakeUnmaskResolver((subjects, _) => subjects.Contains(GroupSid)
            ? new UnmaskCoverage(wholeTable: true, new HashSet<string>())
            : UnmaskCoverage.None);
        var policy = CreatePolicy(Protection(tableMask: "PII"), resolver);

        var inGroup = await policy.DecideAsync(Query(GroupSid), CancellationToken.None);
        var afterExit = await policy.DecideAsync(Query(), CancellationToken.None);

        inGroup.GetColumnAccess("email").ShouldBe(ColumnAccessLevel.Clear);
        afterExit.GetColumnAccess("email").ShouldBe(ColumnAccessLevel.Mask);
    }

    [Fact]
    public async Task Resolver_failure_keeps_columns_masked()
    {
        var resolver = new FakeUnmaskResolver((_, _) => throw new InvalidOperationException("store down"));
        var decision = await CreatePolicy(Protection(tableMask: "PII"), resolver).DecideAsync(Query(), CancellationToken.None);

        decision.IsAllowed.ShouldBeTrue();
        decision.GetColumnAccess("email").ShouldBe(ColumnAccessLevel.Mask);
    }

    private sealed class FakeUnmaskResolver(Func<IReadOnlyCollection<Sid>, bool, UnmaskCoverage> factory) : IUnmaskGrantResolver
    {
        public ValueTask<UnmaskCoverage> ResolveAsync(
            TenantId tenant, IReadOnlyCollection<Sid> subjects, bool groupsComplete, TableIdentifier table, CancellationToken ct = default) =>
            ValueTask.FromResult(factory(subjects, groupsComplete));
    }

    private sealed class StubProtectionProvider(EffectiveProtection protection) : IProtectionSnapshotProvider
    {
        public ValueTask<EffectiveProtection> GetAsync(TenantId tenant, TableIdentifier table, CancellationToken ct = default) =>
            ValueTask.FromResult(protection);
    }

    private sealed class StubSettings(TenantAccessPolicy policy) : IAccessPolicySettingsProvider
    {
        public Task<TenantAccessPolicy> GetEffectiveAsync(TenantId tenant, CancellationToken ct = default) => Task.FromResult(policy);
        public Task<AccessPolicyUpdateResult> UpdateAsync(TenantId tenant, TenantAccessPolicyUpdate update, int expectedVersion, Sid actor, CancellationToken ct = default) =>
            throw new NotImplementedException();
    }
}
