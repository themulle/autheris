namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

/// <summary>
/// Consent union (SEC H-11): allow consents are grouped by a structural fingerprint of their typed row filter. The legacy text key merged
/// consents whose USER_ATTRIBUTE leaves all render as <c>1 = 0</c> and compared literals case-insensitively, so the maximum column level of
/// one consent applied to the rows granted only by another.
/// </summary>
public sealed class ConsentRowFilterGroupingTests
{
    private static readonly TableIdentifier HrTable = new("hr", "dbo", "employees");
    private static readonly Sid UserSid = new("S-1-5-21-GROUPING-USER");

    private static ConsentRowFilter AttributeFilter(string column, string attribute) => new()
    {
        FilterGroup = 1,
        ColumnName = column,
        Operator = "EQ",
        ValueSource = "USER_ATTRIBUTE",
        UserAttribute = attribute,
        ValueJson = "[]"
    };

    private static ConsentRowFilter LiteralFilter(string column, string value) => new()
    {
        FilterGroup = 1,
        ColumnName = column,
        Operator = "EQ",
        ValueJson = $"\"{value}\""
    };

    private static Consent Allow(ConsentRowFilter filter, params ConsentColumnRule[] rules) => new()
    {
        TenantId = Autheris.Domain.Common.TenantId.LegacySingleTenant,
        TableIdentifier = HrTable,
        Effect = ConsentEffect.Allow,
        GranteeType = GranteeType.User,
        GranteeSid = UserSid,
        ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
        ValidTo = DateTimeOffset.UtcNow.AddDays(30),
        ColumnRules = rules,
        RowFilters = [filter]
    };

    private static ConsentColumnRule Rule(string column, ColumnAccessLevel level, params string[] scopes) =>
        new() { ColumnName = column, AccessLevel = level, ApplicableScopes = scopes };

    private static TableAccessDecision Resolve(IReadOnlySet<string>? scopes, params Consent[] consents) =>
        new ConsentResolutionService().ResolveAccess(UserSid, new HashSet<Sid>(), new HashSet<string>(), HrTable, consents, callerScopes: scopes);

    [Fact]
    public void DifferentAttributeFilters_AreNotMerged_ColumnOfOneConsentStaysDeniedForTheOther()
    {
        // Consent A: rows of the caller's department, salary Clear. Consent B: rows of the caller's region, only 'name'.
        // Both legacy texts are '1 = 0'; merged, salary would be Clear for the region rows granted only by B.
        var decision = Resolve(
            null,
            Allow(AttributeFilter("department", "department"), Rule("salary", ColumnAccessLevel.Clear), Rule("name", ColumnAccessLevel.Clear)),
            Allow(AttributeFilter("region", "region"), Rule("name", ColumnAccessLevel.Clear)));

        decision.IsAllowed.ShouldBeTrue();
        decision.GetColumnAccess("salary").ShouldBe(ColumnAccessLevel.Deny);
        decision.GetColumnAccess("name").ShouldBe(ColumnAccessLevel.Clear);
    }

    [Fact]
    public void AttributeFilterAndLiteralFilter_AreNotMerged()
    {
        var decision = Resolve(
            null,
            Allow(AttributeFilter("region", "region"), Rule("salary", ColumnAccessLevel.Clear)),
            Allow(LiteralFilter("region", "EU"), Rule("salary", ColumnAccessLevel.Mask)));

        decision.GetColumnAccess("salary").ShouldBe(ColumnAccessLevel.Mask);
    }

    [Fact]
    public void LiteralsDifferingOnlyInCase_AreDifferentGroups()
    {
        // 'EU' and 'eu' are different rows under a case-sensitive collation: the maximum must not cross them.
        var decision = Resolve(
            null,
            Allow(LiteralFilter("region", "EU"), Rule("salary", ColumnAccessLevel.Clear)),
            Allow(LiteralFilter("region", "eu"), Rule("name", ColumnAccessLevel.Clear)));

        decision.GetColumnAccess("salary").ShouldBe(ColumnAccessLevel.Deny);
        decision.GetColumnAccess("name").ShouldBe(ColumnAccessLevel.Deny);
    }

    [Fact]
    public void StructurallyIdenticalLiteralFilters_StillShareAGroup()
    {
        var decision = Resolve(
            null,
            Allow(LiteralFilter("region", "EU"), Rule("salary", ColumnAccessLevel.Mask)),
            Allow(LiteralFilter("region", "EU"), Rule("salary", ColumnAccessLevel.Clear)));

        decision.GetColumnAccess("salary").ShouldBe(ColumnAccessLevel.Clear);
    }

    [Fact]
    public void ScopeClearance_DifferentAttributeFilters_ClearanceOfOneConsentDoesNotCoverTheOther()
    {
        // Both consents release salary, but only consent A supplies the scope-specific Clear for Agent.Read. Merged into one group, A's
        // clearance would also cover the rows granted only by B.
        var scopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Agent.Read" };
        var decision = Resolve(
            scopes,
            Allow(AttributeFilter("department", "department"), Rule("salary", ColumnAccessLevel.Clear), Rule("salary", ColumnAccessLevel.Clear, "Agent.Read")),
            Allow(AttributeFilter("region", "region"), Rule("salary", ColumnAccessLevel.Clear)));

        decision.GetColumnAccess("salary").ShouldBe(ColumnAccessLevel.Clear);
        decision.ScopeClearance.ShouldNotBeNull();
        decision.ScopeClearance!.Permits("salary").ShouldBeFalse();
    }

    [Fact]
    public void ScopeClearance_SameLiteralFilter_ClearanceIsShared()
    {
        var scopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Agent.Read" };
        var decision = Resolve(
            scopes,
            Allow(LiteralFilter("region", "EU"), Rule("salary", ColumnAccessLevel.Clear), Rule("salary", ColumnAccessLevel.Clear, "Agent.Read")),
            Allow(LiteralFilter("region", "EU"), Rule("salary", ColumnAccessLevel.Clear)));

        decision.ScopeClearance.ShouldNotBeNull();
        decision.ScopeClearance!.Permits("salary").ShouldBeTrue();
    }

    [Fact]
    public void Fingerprint_IsNullForAttributeAndAdvancedFilters_AndOrdinalForLiterals()
    {
        ConsentResolutionService.RowFilterFingerprint([AttributeFilter("region", "region")]).ShouldBeNull();
        ConsentResolutionService.RowFilterFingerprint([new ConsentRowFilter { ColumnName = "id", FilterType = RowFilterType.SubqueryCorrelated }]).ShouldBeNull();
        ConsentResolutionService.RowFilterFingerprint([new ConsentRowFilter { ColumnName = "id", FilterType = RowFilterType.CrossSourceSetFilter }]).ShouldBeNull();
        ConsentResolutionService.RowFilterFingerprint([]).ShouldBeNull();

        var upper = ConsentResolutionService.RowFilterFingerprint([LiteralFilter("region", "EU")]);
        upper.ShouldNotBeNull();
        upper.ShouldBe(ConsentResolutionService.RowFilterFingerprint([LiteralFilter("region", "EU")]));
        upper.ShouldNotBe(ConsentResolutionService.RowFilterFingerprint([LiteralFilter("region", "eu")]));
        upper.ShouldNotBe(ConsentResolutionService.RowFilterFingerprint([LiteralFilter("country", "EU")]));
    }
}
