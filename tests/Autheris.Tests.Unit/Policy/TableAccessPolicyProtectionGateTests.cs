namespace Autheris.Tests.Unit.Policy;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Policy;
using Autheris.Application.Policy;
using Autheris.Application.Policy.Pdp;
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

[Trait("Category", "Security")]
public sealed class TableAccessPolicyProtectionGateTests
{
    private static readonly TenantId Tenant = new("t1");
    private static readonly TableIdentifier TableId = new("sales", "dbo", "orders");
    private static readonly Sid CallerSid = new("S-1-5-21-CALLER-1");
    private static readonly Sid EmpGroupSid = new("S-1-5-21-EMP-1");

    private static TableMetadata MakeMetadata() => new()
    {
        Identifier = TableId,
        Table = new Table
        {
            Id = Guid.NewGuid(),
            SourceName = "sales",
            SchemaName = "dbo",
            TableName = "orders",
            IsActive = true
        },
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "int" },
            new TableColumn { ColumnName = "amount", DataType = "decimal" },
            new TableColumn { ColumnName = "email", DataType = "varchar" }
        ]
    };

    private static EffectiveProtection Protection(
        ProtectionClass declaredTableClass,
        IReadOnlyDictionary<string, ColumnProtection>? columns = null,
        string? tableMask = null) =>
        new(
            Container: new ContainerRef(ContainerKind.Domain, Guid.NewGuid()),
            OwnerGroupSid: null,
            TableClass: declaredTableClass,
            DeclaredTableClass: declaredTableClass,
            TableMaskCategory: tableMask,
            Columns: columns ?? new Dictionary<string, ColumnProtection>(StringComparer.OrdinalIgnoreCase),
            Visibility: CatalogVisibilityMode.Open);

    private static TableAccessPolicy CreatePolicy(
        IProtectionSnapshotProvider protectionProvider,
        FakeConsentRepository? consents = null,
        IMandatoryRowFilterResolver? mandatoryFilters = null,
        TenantAccessPolicy? tenantPolicy = null,
        bool bypassConsent = false)
    {
        var opts = new GatewayOptions
        {
            Insecure = new InsecureGettingStartedOptions { danger_bypass_consent_checks = bypassConsent }
        };
        var policy = tenantPolicy ?? TenantAccessPolicy.FromDeploymentDefaults(Tenant, new AccessPolicyOptions
        {
            Internal = new InternalAccessPolicySettings { EmployeeGroupSids = [EmpGroupSid.Value] }
        });
        return new TableAccessPolicy(
            consents ?? new FakeConsentRepository(),
            new ConsentResolutionService(),
            NullConsentCacheService.Instance,
            opts,
            mandatoryFilters ?? NullMandatoryRowFilterResolver.Instance,
            protectionProvider: protectionProvider,
            accessPolicySettings: new StubAccessPolicySettingsProvider(policy));
    }

    private static TableAccessQuery QueryFor(bool isInternal, IReadOnlySet<Sid>? groups = null) =>
        new(
            CallerSid,
            Tenant,
            isInternal ? (groups ?? new HashSet<Sid> { EmpGroupSid }) : (groups ?? new HashSet<Sid>()),
            new HashSet<string>(),
            MakeMetadata());

    private static Consent MakeConsent(ConsentEffect effect, params ConsentRowFilter[] rowFilters)
    {
        var now = DateTimeOffset.UtcNow;
        return new Consent
        {
            Id = Guid.NewGuid(),
            TenantId = Tenant,
            GranteeType = GranteeType.User,
            GranteeSid = CallerSid,
            TableIdentifier = TableId,
            Effect = effect,
            ColumnSetMode = ColumnSetMode.Explicit,
            ValidFrom = now.AddDays(-1),
            ValidTo = now.AddDays(30),
            IsRevoked = false,
            RowFilters = rowFilters
        };
    }

    [Fact]
    public async Task Snapshot_failure_denies()
    {
        var failingProvider = new StubProtectionSnapshotProvider((_, _) => throw new InvalidOperationException("DB down"));
        var policy = CreatePolicy(failingProvider);

        var decision = await policy.DecideAsync(QueryFor(isInternal: true), CancellationToken.None);

        decision.IsAllowed.ShouldBeFalse();
        decision.Reasons.ShouldContain(r => r.Step == DecisionStep.Protection && r.Code == "PROTECTION_UNAVAILABLE");
    }

    [Fact]
    public async Task Explicit_deny_beats_public()
    {
        var consents = new FakeConsentRepository();
        await consents.CreateConsentAsync(MakeConsent(ConsentEffect.Deny));
        var policy = CreatePolicy(new StubProtectionSnapshotProvider((_, _) => Protection(ProtectionClass.Public)), consents);

        var decision = await policy.DecideAsync(QueryFor(isInternal: false), CancellationToken.None);

        decision.IsAllowed.ShouldBeFalse();
        decision.DenialKind.ShouldBe(TableAccessDenialKind.ConsentDeny);
    }

    [Fact]
    public async Task Consent_row_filter_not_applied_to_baseline()
    {
        var consents = new FakeConsentRepository();
        await consents.CreateConsentAsync(MakeConsent(
            ConsentEffect.Allow,
            new ConsentRowFilter
            {
                ColumnName = "id",
                Operator = "EQ",
                ValueType = "int",
                ValueJson = "1"
            }));
        var policy = CreatePolicy(new StubProtectionSnapshotProvider((_, _) => Protection(ProtectionClass.Public)), consents);

        var decision = await policy.DecideAsync(QueryFor(isInternal: false), CancellationToken.None);

        decision.IsAllowed.ShouldBeTrue();
        decision.CombinedRowFilterSql.ShouldBeNull();
        decision.GetColumnAccess("id").ShouldBe(ColumnAccessLevel.Clear);
        decision.GetColumnAccess("amount").ShouldBe(ColumnAccessLevel.Clear);
        decision.GetColumnAccess("email").ShouldBe(ColumnAccessLevel.Clear);
    }

    [Fact]
    public async Task Virtual_filter_applies_to_baseline()
    {
        var policy = CreatePolicy(
            new StubProtectionSnapshotProvider((_, _) => Protection(ProtectionClass.Internal)),
            mandatoryFilters: new FixedMandatoryFilterResolver("tenant_col = 't1'"));

        var decision = await policy.DecideAsync(QueryFor(isInternal: true), CancellationToken.None);

        decision.IsAllowed.ShouldBeTrue();
        decision.MandatoryRowPredicateSql.ShouldBe("tenant_col = 't1'");
    }

    [Fact]
    public async Task Internal_caller_denied_all_columns_when_one_column_secret()
    {
        var columns = new Dictionary<string, ColumnProtection>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = new(ProtectionClass.Internal, null),
            ["email"] = new(ProtectionClass.Secret, "email")
        };
        var policy = CreatePolicy(new StubProtectionSnapshotProvider((_, _) => Protection(ProtectionClass.Internal, columns)));

        var decision = await policy.DecideAsync(QueryFor(isInternal: true), CancellationToken.None);

        decision.IsAllowed.ShouldBeFalse();
        decision.Reasons.ShouldContain(r => r.Step == DecisionStep.Protection && r.Code == "PROTECTION_CLASS_GRANT_REQUIRED");
    }

    [Fact]
    public async Task Table_grant_covers_all_columns()
    {
        var consents = new FakeConsentRepository();
        await consents.CreateConsentAsync(MakeConsent(ConsentEffect.Allow));
        var columns = new Dictionary<string, ColumnProtection>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = new(ProtectionClass.Secret, null),
            ["email"] = new(ProtectionClass.Secret, "email")
        };
        var policy = CreatePolicy(new StubProtectionSnapshotProvider((_, _) => Protection(ProtectionClass.Secret, columns)), consents);

        var decision = await policy.DecideAsync(QueryFor(isInternal: false), CancellationToken.None);

        decision.IsAllowed.ShouldBeTrue();
        decision.GetColumnAccess("id").ShouldBe(ColumnAccessLevel.Clear);
        decision.GetColumnAccess("email").ShouldBe(ColumnAccessLevel.Mask);
    }

    [Theory]
    [InlineData(ProtectionClass.Public, false)]
    [InlineData(ProtectionClass.Internal, true)]
    public async Task Baseline_with_explicit_column_deny_and_no_allow_enforces_column_deny(ProtectionClass tableClass, bool isInternal)
    {
        var consents = new FakeConsentRepository();
        var deny = MakeConsent(ConsentEffect.Deny);
        deny.ColumnRules = [new ConsentColumnRule { ColumnName = "email", AccessLevel = ColumnAccessLevel.Deny }];
        await consents.CreateConsentAsync(deny);
        var policy = CreatePolicy(new StubProtectionSnapshotProvider((_, _) => Protection(tableClass)), consents);

        var decision = await policy.DecideAsync(QueryFor(isInternal), CancellationToken.None);

        decision.IsAllowed.ShouldBeTrue();
        decision.GetColumnAccess("id").ShouldBe(ColumnAccessLevel.Clear);
        decision.GetColumnAccess("email").ShouldBe(ColumnAccessLevel.Deny);
    }

    [Theory]
    [InlineData(ProtectionClass.Public, false)]
    [InlineData(ProtectionClass.Internal, true)]
    public async Task Baseline_with_explicit_row_deny_and_no_allow_enforces_row_deny(ProtectionClass tableClass, bool isInternal)
    {
        var consents = new FakeConsentRepository();
        await consents.CreateConsentAsync(MakeConsent(
            ConsentEffect.Deny,
            new ConsentRowFilter
            {
                Id = Guid.NewGuid(),
                ColumnName = "amount",
                Operator = "GT",
                ValueType = "int",
                ValueJson = "100"
            }));
        var policy = CreatePolicy(new StubProtectionSnapshotProvider((_, _) => Protection(tableClass)), consents);

        var decision = await policy.DecideAsync(QueryFor(isInternal), CancellationToken.None);

        decision.IsAllowed.ShouldBeTrue();
        decision.CombinedRowFilterSql.ShouldNotBeNullOrWhiteSpace();
        decision.CombinedRowFilterSql.ShouldContain("NOT");
        decision.CombinedRowFilterSql.ShouldContain("amount");
    }

    [Theory]
    [InlineData(ProtectionClass.Public, false)]
    [InlineData(ProtectionClass.Internal, true)]
    public async Task Baseline_with_explicit_deny_mask_and_no_allow_enforces_mask(ProtectionClass tableClass, bool isInternal)
    {
        var consents = new FakeConsentRepository();
        var denyMask = MakeConsent(ConsentEffect.Deny);
        denyMask.ColumnRules = [new ConsentColumnRule { ColumnName = "email", AccessLevel = ColumnAccessLevel.Mask }];
        await consents.CreateConsentAsync(denyMask);
        var policy = CreatePolicy(new StubProtectionSnapshotProvider((_, _) => Protection(tableClass)), consents);

        var decision = await policy.DecideAsync(QueryFor(isInternal), CancellationToken.None);

        decision.IsAllowed.ShouldBeTrue();
        decision.GetColumnAccess("id").ShouldBe(ColumnAccessLevel.Clear);
        decision.GetColumnAccess("email").ShouldBe(ColumnAccessLevel.Mask);
    }

    [Theory]
    [InlineData(ProtectionClass.Public, false)]
    [InlineData(ProtectionClass.Internal, true)]
    public async Task Baseline_with_allow_and_explicit_row_deny_applies_row_deny_without_allow_filter(ProtectionClass tableClass, bool isInternal)
    {
        var consents = new FakeConsentRepository();
        await consents.CreateConsentAsync(MakeConsent(
            ConsentEffect.Allow,
            new ConsentRowFilter
            {
                Id = Guid.NewGuid(),
                ColumnName = "id",
                Operator = "EQ",
                ValueType = "int",
                ValueJson = "1"
            }));
        var denyConsent = MakeConsent(
            ConsentEffect.Deny,
            new ConsentRowFilter
            {
                Id = Guid.NewGuid(),
                ColumnName = "amount",
                Operator = "GT",
                ValueType = "int",
                ValueJson = "500"
            });
        denyConsent.ColumnRules = [new ConsentColumnRule { ColumnName = "email", AccessLevel = ColumnAccessLevel.Mask }];
        await consents.CreateConsentAsync(denyConsent);
        var policy = CreatePolicy(new StubProtectionSnapshotProvider((_, _) => Protection(tableClass)), consents);

        var decision = await policy.DecideAsync(QueryFor(isInternal), CancellationToken.None);

        decision.IsAllowed.ShouldBeTrue();
        decision.CombinedRowFilterSql.ShouldNotBeNullOrWhiteSpace();
        decision.CombinedRowFilterSql.ShouldContain("NOT");
        decision.CombinedRowFilterSql.ShouldContain("amount");
        decision.CombinedRowFilterSql.ShouldNotContain("[id]");
        decision.GetColumnAccess("email").ShouldBe(ColumnAccessLevel.Mask);
        decision.GetColumnAccess("id").ShouldBe(ColumnAccessLevel.Clear);
    }

    private sealed class StubProtectionSnapshotProvider(Func<TenantId, TableIdentifier, EffectiveProtection> factory) : IProtectionSnapshotProvider
    {
        public ValueTask<EffectiveProtection> GetAsync(TenantId tenant, TableIdentifier table, CancellationToken ct = default)
            => ValueTask.FromResult(factory(tenant, table));
    }

    private sealed class StubAccessPolicySettingsProvider(TenantAccessPolicy policy) : IAccessPolicySettingsProvider
    {
        public Task<TenantAccessPolicy> GetEffectiveAsync(TenantId tenant, CancellationToken ct = default) => Task.FromResult(policy);
        public Task<AccessPolicyUpdateResult> UpdateAsync(TenantId tenant, TenantAccessPolicyUpdate update, int expectedVersion, Sid actor, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class FixedMandatoryFilterResolver(string predicateSql) : IMandatoryRowFilterResolver
    {
        public ValueTask<MandatoryFilterOutcome> ResolveAsync(MandatoryFilterQuery query, CancellationToken ct = default)
            => ValueTask.FromResult(new MandatoryFilterOutcome(IsDenied: false, DenyReason: null, PredicateSql: predicateSql, AppliedFilters: ["v_tenant_scope"]));

        public ValueTask<bool> HasActiveProfilesAsync(TenantId tenant, CancellationToken ct = default) => ValueTask.FromResult(true);
    }
}
