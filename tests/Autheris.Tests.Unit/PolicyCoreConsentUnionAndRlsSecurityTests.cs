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
using Autheris.Application.Common.Interfaces;
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
public sealed class PolicyCoreConsentUnionAndRlsSecurityTests
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
            TenantId = Autheris.Tests.Common.TestTenants.Default,
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
    // H-09: Streaming uses consent -> decision point -> catalog masking
    // =====================================================================================

    private sealed class StreamFixture
    {
        public Autheris.Application.Policy.Pdp.IAccessDecisionPoint Policy { get; } = Autheris.Tests.Unit.TestSupport.PdpTestDoubles.AllowAll();
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
    public async Task H09_Stream_WithoutConsent_IsDeniedEvenIfDecisionPointWouldAllow()
    {
        var fixture = new StreamFixture(Array.Empty<Consent>());
        fixture.Policy.DecideAsync(Arg.Any<Autheris.Application.Policy.Pdp.AccessRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<TableAccessDecision>(TableAccessDecision.Allowed(HrTable, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true)));

        var result = await fixture.Create().EvaluateAndMaskAsync(HrEvent(), Subscriber());

        result.IsAllowed.ShouldBeFalse();
        result.MaskedPayload.ShouldBeNull();
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
    // Low: consent cache TTL bounded by ValidTo
    // =====================================================================================

    [Fact]
    public void Low_ConsentCacheTtl_IsBoundedByEarliestValidTo()
    {
        var now = DateTimeOffset.UtcNow;
        var consent = AllowConsent(null);
        var soonExpiring = new Consent
        {
            TenantId = Autheris.Tests.Common.TestTenants.Default,
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
