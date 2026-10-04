#pragma warning disable CA2012

namespace Autheris.Tests.Unit;

using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance;
using Autheris.Application.Governance.Services;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
using Autheris.Application.Streaming.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// Security review 2026-10-02, work package C (policy core): H-05, H-09, H-11, H-12, M-18, M-19, M-21 and low findings.
/// </summary>
public sealed class SecurityReview20261002PolicyCoreTests
{
    private static readonly TableIdentifier HrTable = new("hr", "dbo", "employees");
    private static readonly Sid UserSid = new("S-1-5-21-POLICY-CORE-USER");
    private static readonly TenantId TenantA = new("tenant-a");

    // =====================================================================================
    // H-11: Consent union
    // =====================================================================================

    private static Consent AllowConsent(string? region, params (string Column, ColumnAccessLevel Level)[] columns)
    {
        var columnRules = new List<ConsentColumnRule>();
        foreach (var (column, level) in columns)
        {
            columnRules.Add(new ConsentColumnRule { ColumnName = column, AccessLevel = level });
        }

        var rowFilters = new List<ConsentRowFilter>();
        if (region != null)
        {
            rowFilters.Add(new ConsentRowFilter { FilterGroup = 1, ColumnName = "region", Operator = "EQ", ValueJson = $"\"{region}\"" });
        }

        return new Consent
        {
            TableIdentifier = HrTable,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = UserSid,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(30),
            ColumnRules = columnRules,
            RowFilters = rowFilters
        };
    }

    private static TableAccessDecision Resolve(params Consent[] consents) =>
        new ConsentResolutionService().ResolveAccess(UserSid, new HashSet<Sid>(), new HashSet<string>(), HrTable, consents);

    [Fact]
    public void H11_ConsentUnion_ColumnWithoutRule_IsNotClearForRowsOfOtherConsent()
    {
        // Consent A (EU, only 'name' Clear) + Consent B (US, no column rules): 'ssn' must not become Clear for EU rows.
        var decision = Resolve(
            AllowConsent("EU", ("name", ColumnAccessLevel.Clear)),
            AllowConsent("US"));

        decision.IsAllowed.ShouldBeTrue();
        decision.HasUnconstrainedColumnAllow.ShouldBeFalse();
        decision.GetColumnAccess("ssn").ShouldBe(ColumnAccessLevel.Deny);
        decision.GetColumnAccess("name").ShouldBe(ColumnAccessLevel.Clear);
    }

    [Fact]
    public void H11_ColumnReleasedByOnlyOneRowConstrainedConsent_IsDeniedForTheUnion()
    {
        // Consent A (EU, name Clear) + Consent B (US, salary Clear): neither column is Clear for all rows.
        var decision = Resolve(
            AllowConsent("EU", ("name", ColumnAccessLevel.Clear)),
            AllowConsent("US", ("salary", ColumnAccessLevel.Clear)));

        decision.IsAllowed.ShouldBeTrue();
        decision.GetColumnAccess("salary").ShouldBe(ColumnAccessLevel.Deny);
        decision.GetColumnAccess("name").ShouldBe(ColumnAccessLevel.Deny);
    }

    [Fact]
    public void H11_SameRowFilter_UsesMaximumWithinGroup()
    {
        var decision = Resolve(
            AllowConsent("EU", ("salary", ColumnAccessLevel.Mask)),
            AllowConsent("EU", ("salary", ColumnAccessLevel.Clear)));

        decision.GetColumnAccess("salary").ShouldBe(ColumnAccessLevel.Clear);
    }

    [Fact]
    public void H11_SingleRowConstrainedConsentWithoutColumnRules_StillReleasesAllColumns()
    {
        var decision = Resolve(AllowConsent("EU"));

        decision.IsAllowed.ShouldBeTrue();
        decision.HasUnconstrainedColumnAllow.ShouldBeTrue();
        decision.GetColumnAccess("any_column").ShouldBe(ColumnAccessLevel.Clear);
        decision.CombinedRowFilterSql.ShouldNotBeNull();
    }

    [Fact]
    public void H11_UnconstrainedConsentWithoutRules_PlusRowConsent_KeepsUnconstrainedColumns()
    {
        var decision = Resolve(
            AllowConsent(null),
            AllowConsent("EU", ("name", ColumnAccessLevel.Mask)));

        decision.HasUnconstrainedColumnAllow.ShouldBeTrue();
        decision.GetColumnAccess("ssn").ShouldBe(ColumnAccessLevel.Clear);
        decision.CombinedRowFilterSql.ShouldBeNull();
    }

    [Fact]
    public void H11_UnconstrainedConsentWithRules_RowConsentCannotElevateOtherColumns()
    {
        // Unconstrained consent releases only 'name'; row-constrained consent without rules must not release 'ssn' globally.
        var decision = Resolve(
            AllowConsent(null, ("name", ColumnAccessLevel.Clear)),
            AllowConsent("EU"));

        decision.HasUnconstrainedColumnAllow.ShouldBeFalse();
        decision.GetColumnAccess("ssn").ShouldBe(ColumnAccessLevel.Deny);
        decision.GetColumnAccess("name").ShouldBe(ColumnAccessLevel.Clear);
    }

    // =====================================================================================
    // H-12 / M-18 / M-19: Casbin
    // =====================================================================================

    private static SecurityEvaluationContext CasbinContext(TableIdentifier table, Dictionary<string, object?>? attributes = null, IReadOnlyCollection<Sid>? groups = null) =>
        new(
            UserSid: UserSid,
            GroupSids: groups ?? Array.Empty<Sid>(),
            Tenant: TenantA,
            TargetTable: table,
            RequestedColumns: ["id"],
            ClientIp: IPAddress.Parse("10.1.2.3"),
            Timestamp: DateTimeOffset.UtcNow,
            PurposeId: "AUDIT",
            Attributes: attributes);

    [Fact]
    public async Task H12_KeyMatch2SegmentParameter_AllowRule_ContributesItsRlsFilter()
    {
        // Previously: Casbin allowed via keyMatch2 ('finance.dbo.:table'), but the gateway matcher did not match -> no filter (all regions).
        using var casbin = new CasbinEnforcementService();
        casbin.AddPolicy(TenantA, UserSid.Value, "finance.dbo.:table", "read", "true", "allow", rlsFilter: "region = 'EU'");

        var decision = await casbin.EvaluatePolicyAsync(CasbinContext(new TableIdentifier("finance", "dbo", "invoices")));

        decision.IsAllowed.ShouldBeTrue();
        decision.CombinedRowFilterSql.ShouldBe("region = 'EU'");
    }

    [Fact]
    public async Task H12_M18_CasbinOnlyMatchAcrossSegmentBoundary_IsDeniedFailClosed()
    {
        // keyMatch2 treats '.' as regex wildcard: 'finance.dbo.*' matches 'finance.dbo_hr.salaries' in Casbin.
        // The gateway matcher does not match, so no filter could be collected -> must be denied.
        using var casbin = new CasbinEnforcementService();
        casbin.AddPolicy(TenantA, UserSid.Value, "finance.dbo.*", "read", "true", "allow", rlsFilter: "region = 'EU'");

        var decision = await casbin.EvaluatePolicyAsync(CasbinContext(new TableIdentifier("finance", "dbo_hr", "salaries")));

        decision.IsAllowed.ShouldBeFalse();
        decision.CombinedRowFilterSql.ShouldBeNull();
    }

    [Fact]
    public async Task M18_SegmentWildcard_StillMatchesTablesInsideSchema()
    {
        using var casbin = new CasbinEnforcementService();
        casbin.AddPolicy(TenantA, UserSid.Value, "finance.dbo.*", "read", "true", "allow", rlsFilter: "region = 'EU'");

        var decision = await casbin.EvaluatePolicyAsync(CasbinContext(new TableIdentifier("finance", "dbo", "invoices")));

        decision.IsAllowed.ShouldBeTrue();
        decision.CombinedRowFilterSql.ShouldBe("region = 'EU'");
    }

    [Fact]
    public async Task H12_DenyRuleWithUnevaluableSubRule_FailsClosed()
    {
        using var casbin = new CasbinEnforcementService();
        var table = new TableIdentifier("finance", "dbo", "invoices");
        casbin.AddPolicy(TenantA, UserSid.Value, table.ToString(), "read", "true", "allow");
        casbin.AddPolicy(TenantA, UserSid.Value, table.ToString(), "read", "r.ctx.NoSuchProperty == 'x'", "deny");

        var decision = await casbin.EvaluatePolicyAsync(CasbinContext(table));

        decision.IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public async Task H12_GroupMatchedAllowRule_ContributesFilter()
    {
        using var casbin = new CasbinEnforcementService();
        var table = new TableIdentifier("finance", "dbo", "invoices");
        var group = new Sid("S-1-5-21-FINANCE-GROUP");
        casbin.AddPolicy(TenantA, "role:finance", table.ToString(), "read", "true", "allow", rlsFilter: "region = 'DE'");
        casbin.AddRoleForUser(TenantA, group.Value, "role:finance");

        var decision = await casbin.EvaluatePolicyAsync(CasbinContext(table, groups: [group]));

        decision.IsAllowed.ShouldBeTrue();
        decision.CombinedRowFilterSql.ShouldBe("region = 'DE'");
    }

    [Fact]
    public async Task M19_UnquotedTemplate_ClaimWithSqlKeywords_IsEmittedAsQuotedLiteral()
    {
        using var casbin = new CasbinEnforcementService();
        var table = new TableIdentifier("finance", "dbo", "costs");
        casbin.AddPolicy(TenantA, UserSid.Value, table.ToString(), "read", "true", "allow", rlsFilter: "cost_center = ${attr.cost_center}");

        var decision = await casbin.EvaluatePolicyAsync(CasbinContext(table, new Dictionary<string, object?>
        {
            ["cost_center"] = "0 OR tenant_id IS NOT NULL"
        }));

        decision.IsAllowed.ShouldBeTrue();
        decision.CombinedRowFilterSql.ShouldBe("cost_center = '0 OR tenant_id IS NOT NULL'");
    }

    [Fact]
    public async Task M19_QuotedAndPartiallyQuotedTemplates_AreNotDoubleQuoted()
    {
        using var casbin = new CasbinEnforcementService();
        var table = new TableIdentifier("finance", "dbo", "costs");
        casbin.AddPolicy(TenantA, UserSid.Value, table.ToString(), "read", "true", "allow", rlsFilter: "dept = '${department}' AND code LIKE '${department}%'");

        var decision = await casbin.EvaluatePolicyAsync(CasbinContext(table, new Dictionary<string, object?>
        {
            ["department"] = "HR"
        }));

        decision.IsAllowed.ShouldBeTrue();
        decision.CombinedRowFilterSql.ShouldBe("dept = 'HR' AND code LIKE 'HR%'");
    }

    [Fact]
    public async Task M19_UnresolvedAttributePlaceholder_FailsClosed()
    {
        using var casbin = new CasbinEnforcementService();
        var table = new TableIdentifier("finance", "dbo", "costs");
        casbin.AddPolicy(TenantA, UserSid.Value, table.ToString(), "read", "true", "allow", rlsFilter: "cost_center = ${attr.cost_center}");

        await Should.ThrowAsync<System.Security.SecurityException>(async () =>
            await casbin.EvaluatePolicyAsync(CasbinContext(table, new Dictionary<string, object?>())));
    }

    // =====================================================================================
    // H-09: Streaming uses consent -> Casbin -> catalog masking
    // =====================================================================================

    private sealed class StreamFixture
    {
        public IPolicyEnforcementService Policy { get; } = Substitute.For<IPolicyEnforcementService>();
        public ITableMetadataRepository Metadata { get; } = Substitute.For<ITableMetadataRepository>();
        public IColumnMaskingProvider Masking { get; } = Substitute.For<IColumnMaskingProvider>();
        public IConsentRepository Consents { get; } = Substitute.For<IConsentRepository>();
        public IConsentCacheService Cache { get; } = Substitute.For<IConsentCacheService>();

        public StreamFixture(IReadOnlyList<Consent> consents)
        {
            Consents.GetActiveConsentsForSubjectsAsync(
                    Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(consents));

            Metadata.GetTableMetadataAsync(HrTable, Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<TableMetadata?>(new TableMetadata
                {
                    Identifier = HrTable,
                    Table = new Table { SourceName = "hr", SchemaName = "dbo", TableName = "employees" },
                    Columns =
                    [
                        new TableColumn { ColumnName = "id" },
                        new TableColumn { ColumnName = "name" },
                        new TableColumn { ColumnName = "salary", IsSensitive = true },
                        new TableColumn { ColumnName = "iban" },
                        new TableColumn { ColumnName = "ssn" }
                    ],
                    ColumnMaskingRules = new Dictionary<string, MaskingRule>
                    {
                        ["iban"] = new MaskingRule { RuleType = "REDACT" }
                    }
                }));

            Masking.MaskValue(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<MaskingRule>()).Returns("***");
        }

        public StreamRlsPolicyEnforcer Create() => new(
            Policy,
            Metadata,
            Masking,
            Substitute.For<IEpochValidationService>(),
            NullLogger<StreamRlsPolicyEnforcer>.Instance,
            Consents,
            new ConsentResolutionService(),
            Cache);
    }

    private static CdcEvent HrEvent() => new(
        EventId: "evt-hr-1",
        Table: HrTable,
        Operation: CdcOperation.Insert,
        TenantId: TenantA.Value,
        Before: null,
        After: new Dictionary<string, object?>
        {
            ["id"] = 1,
            ["name"] = "Alice",
            ["salary"] = 120000,
            ["iban"] = "DE89370400440532013000",
            ["ssn"] = "123-45-6789",
            ["unknown_col"] = "leak"
        },
        Timestamp: DateTimeOffset.UtcNow);

    private static ClaimsPrincipal Subscriber(params Claim[] extra)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", TenantA.Value),
            new(ClaimTypes.PrimarySid, UserSid.Value)
        };
        claims.AddRange(extra);
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private static Consent TenantConsent(ConsentEffect effect, params (string Column, ColumnAccessLevel Level)[] columns)
    {
        var rules = new List<ConsentColumnRule>();
        foreach (var (column, level) in columns)
        {
            rules.Add(new ConsentColumnRule { ColumnName = column, AccessLevel = level });
        }

        return new Consent
        {
            TableIdentifier = HrTable,
            Effect = effect,
            TenantId = TenantA,
            GranteeType = GranteeType.User,
            GranteeSid = UserSid,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(30),
            ColumnRules = rules
        };
    }

    [Fact]
    public async Task H09_Stream_SensitiveAndCatalogMaskedColumns_AreMaskedWithoutExplicitClear()
    {
        var fixture = new StreamFixture([TenantConsent(ConsentEffect.Allow)]);

        var result = await fixture.Create().EvaluateAndMaskAsync(HrEvent(), Subscriber());

        result.IsAllowed.ShouldBeTrue();
        result.MaskedPayload.ShouldNotBeNull();
        result.MaskedPayload["name"].ShouldBe("Alice");
        result.MaskedPayload["salary"].ShouldBe("***");
        result.MaskedPayload["iban"].ShouldBe("***");
        result.MaskedPayload.ContainsKey("unknown_col").ShouldBeFalse();
    }

    [Fact]
    public async Task H09_Stream_ConsentColumnDeny_StripsColumn()
    {
        var fixture = new StreamFixture(
        [
            TenantConsent(ConsentEffect.Allow),
            TenantConsent(ConsentEffect.Deny, ("ssn", ColumnAccessLevel.Deny))
        ]);

        var result = await fixture.Create().EvaluateAndMaskAsync(HrEvent(), Subscriber());

        result.IsAllowed.ShouldBeTrue();
        result.MaskedPayload.ShouldNotBeNull();
        result.MaskedPayload.ContainsKey("ssn").ShouldBeFalse();
    }

    [Fact]
    public async Task H09_Stream_WithoutConsent_IsDeniedEvenIfCasbinWouldAllow()
    {
        var fixture = new StreamFixture(Array.Empty<Consent>());
        fixture.Policy.HasPolicies(Arg.Any<TenantId>()).Returns(true);
        fixture.Policy.EvaluatePolicyAsync(Arg.Any<SecurityEvaluationContext>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<TableAccessDecision>(TableAccessDecision.Allowed(HrTable, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true)));

        var result = await fixture.Create().EvaluateAndMaskAsync(HrEvent(), Subscriber());

        result.IsAllowed.ShouldBeFalse();
        result.MaskedPayload.ShouldBeNull();
    }

    [Fact]
    public async Task H09_Stream_PassesClaimAttributesAndNoLoopbackIpToCasbin()
    {
        var fixture = new StreamFixture([TenantConsent(ConsentEffect.Allow)]);
        SecurityEvaluationContext? captured = null;
        fixture.Policy.HasPolicies(Arg.Any<TenantId>()).Returns(true);
        fixture.Policy.EvaluatePolicyAsync(Arg.Do<SecurityEvaluationContext>(c => captured = c), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<TableAccessDecision>(TableAccessDecision.Allowed(HrTable, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true)));

        var result = await fixture.Create().EvaluateAndMaskAsync(HrEvent(), Subscriber(new Claim("department", "Contractor"), new Claim("purpose", "PAYROLL")));

        result.IsAllowed.ShouldBeTrue();
        captured.ShouldNotBeNull();
        captured.Department.ShouldBe("Contractor");
        captured.PurposeId.ShouldBe("PAYROLL");
        IPAddress.IsLoopback(captured.ClientIp).ShouldBeFalse();
    }

    [Fact]
    public async Task H09_Stream_AttributeBasedCasbinDenyRule_IsEnforced()
    {
        var fixture = new StreamFixture([TenantConsent(ConsentEffect.Allow)]);
        using var casbin = new CasbinEnforcementService();
        casbin.AddPolicy(TenantA, UserSid.Value, HrTable.ToString(), "read", "true", "allow");
        casbin.AddPolicy(TenantA, UserSid.Value, HrTable.ToString(), "read", "r.ctx.Department == 'Contractor'", "deny");

        var enforcer = new StreamRlsPolicyEnforcer(
            casbin,
            fixture.Metadata,
            fixture.Masking,
            Substitute.For<IEpochValidationService>(),
            NullLogger<StreamRlsPolicyEnforcer>.Instance,
            fixture.Consents,
            new ConsentResolutionService(),
            fixture.Cache);

        var contractor = await enforcer.EvaluateAndMaskAsync(HrEvent(), Subscriber(new Claim("department", "Contractor")));
        contractor.IsAllowed.ShouldBeFalse();

        var employee = await enforcer.EvaluateAndMaskAsync(HrEvent(), Subscriber(new Claim("department", "HR")));
        employee.IsAllowed.ShouldBeTrue();
    }

    // =====================================================================================
    // M-21: Streaming row filter - three-valued logic, fail-closed compilation
    // =====================================================================================

    [Fact]
    public void M21_NotEqual_WithNullValue_IsNotMatched()
    {
        var payload = new Dictionary<string, object?> { ["id"] = 1, ["country"] = null };
        StreamingRowFilterAstEvaluator.Matches(payload, "country <> 'CN'").ShouldBeFalse();
    }

    [Fact]
    public void M21_NegatedComparison_WithMissingColumn_IsNotMatched()
    {
        var payload = new Dictionary<string, object?> { ["id"] = 1 };
        StreamingRowFilterAstEvaluator.Matches(payload, "NOT (dept = 'x')").ShouldBeFalse();
    }

    [Fact]
    public void M21_NotInList_ContainingNull_IsUnknownAndNotMatched()
    {
        var payload = new Dictionary<string, object?> { ["region"] = "EU" };
        StreamingRowFilterAstEvaluator.Matches(payload, "region NOT IN ('US', NULL)").ShouldBeFalse();
    }

    [Theory]
    [InlineData("UPPER(region) = 'EU'")]
    [InlineData("region = @region")]
    [InlineData("region = ?")]
    [InlineData("region = (SELECT 'EU')")]
    [InlineData("EXISTS (SELECT 1 FROM t)")]
    public void M21_UnsupportedExpressions_AreRejectedFailClosed(string filter)
    {
        var payload = new Dictionary<string, object?> { ["region"] = "EU", ["UPPER(region)"] = "EU", ["__param_region"] = "EU" };
        StreamingRowFilterAstEvaluator.Matches(payload, filter).ShouldBeFalse();
    }

    [Fact]
    public void M21_OrWithUnknownBranch_TrueBranchStillMatches()
    {
        var payload = new Dictionary<string, object?> { ["country"] = null, ["region"] = "EU" };
        StreamingRowFilterAstEvaluator.Matches(payload, "country <> 'CN' OR region = 'EU'").ShouldBeTrue();
    }

    [Fact]
    public void M21_BracketQuotedIdentifier_FromSqlServerRowFilterBuilder_StillMatches()
    {
        var payload = new Dictionary<string, object?> { ["region"] = "EU", ["amount"] = 150 };
        StreamingRowFilterAstEvaluator.Matches(payload, "([region] = 'EU') AND amount >= 100").ShouldBeTrue();
        StreamingRowFilterAstEvaluator.Matches(payload, "[region] = 'US'").ShouldBeFalse();
    }

    // =====================================================================================
    // H-05: Policy simulation tenant scope
    // =====================================================================================

    [Fact]
    public async Task H05_PolicySimulation_NullRequestTenant_QueriesOnlyEffectiveTenant()
    {
        var auditRepo = Substitute.For<IAuditLogRepository>();
        auditRepo.QueryAuditLogsAsync(Arg.Any<string?>(), Arg.Any<Sid?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<int>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AuditLogEntry>>(Array.Empty<AuditLogEntry>()));

        var service = new PolicySimulationService(auditRepo);
        var request = new PolicySimulationRequest(DraftPolicyCsv: "p, *, *, *, *, true, deny", Tenant: null, Limit: 10000);

        await service.SimulateAsync(request, TenantA);

        await auditRepo.Received(1).QueryAuditLogsAsync(
            Arg.Any<string?>(), Arg.Any<Sid?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<int>(),
            Arg.Is<TenantId?>(t => t.HasValue && t.Value == TenantA),
            Arg.Any<CancellationToken>());
        await auditRepo.DidNotReceive().QueryAuditLogsAsync(
            Arg.Any<string?>(), Arg.Any<Sid?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<int>(),
            Arg.Is<TenantId?>(t => !t.HasValue),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task H05_PolicySimulation_ForeignRequestTenant_IsIgnoredInFavourOfEffectiveTenant()
    {
        var auditRepo = Substitute.For<IAuditLogRepository>();
        auditRepo.QueryAuditLogsAsync(Arg.Any<string?>(), Arg.Any<Sid?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<int>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AuditLogEntry>>(Array.Empty<AuditLogEntry>()));

        var service = new PolicySimulationService(auditRepo);
        var request = new PolicySimulationRequest(DraftPolicyCsv: "p, *, *, *, *, true, deny", Tenant: new TenantId("tenant-victim"));

        await service.SimulateAsync(request, TenantA);

        await auditRepo.DidNotReceive().QueryAuditLogsAsync(
            Arg.Any<string?>(), Arg.Any<Sid?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<int>(),
            Arg.Is<TenantId?>(t => t.HasValue && t.Value.Value == "tenant-victim"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task H05_PolicySimulation_DefaultTenant_IsRejected()
    {
        var service = new PolicySimulationService(Substitute.For<IAuditLogRepository>());
        var request = new PolicySimulationRequest(DraftPolicyCsv: "p, *, *, *, *, true, deny");

        await Should.ThrowAsync<ArgumentException>(() => service.SimulateAsync(request, default));
    }

    // =====================================================================================
    // Low: consent cache TTL bounded by ValidTo
    // =====================================================================================

    [Fact]
    public void Low_ConsentCacheTtl_IsBoundedByEarliestValidTo()
    {
        var now = DateTimeOffset.UtcNow;
        var consent = AllowConsent(null);
        var soonExpiring = new Consent
        {
            TableIdentifier = HrTable,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = UserSid,
            ValidFrom = now.AddDays(-1),
            ValidTo = now.AddSeconds(15)
        };

        var ttl = ConsentResolutionService.ComputeDecisionCacheTtl(false, [consent, soonExpiring], now);

        ttl.ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(15));
        ttl.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(1));
        ConsentResolutionService.ComputeDecisionCacheTtl(false, [consent], now).ShouldBe(TimeSpan.FromMinutes(10));
    }
}
