namespace Autheris.Tests.Unit;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

/// <summary>
/// Issue #116: every component that derives column levels from consent column rules must treat scope-specific rules
/// consistently with <see cref="ConsentResolutionService"/> (scope-only rules never grant access to other scopes).
/// </summary>
public sealed class Issue116ScopeOnlyColumnRuleConsistencyTests
{
    private static readonly TableIdentifier Orders = new("sales", "public", "orders");
    private static readonly TenantId Tenant = new("tenant-a");

    private static IReadOnlySet<string> Scopes(params string[] s) => new HashSet<string>(s, StringComparer.OrdinalIgnoreCase);

    private static ConsentColumnRule Rule(string column, ColumnAccessLevel level, params string[] scopes) =>
        new() { ColumnName = column, AccessLevel = level, ApplicableScopes = scopes };

    private static Consent AllowWith(params ConsentColumnRule[] rules) => new()
    {
        TableIdentifier = Orders,
        TenantId = Tenant,
        Effect = ConsentEffect.Allow,
        GranteeType = GranteeType.User,
        GranteeSid = new Sid("S-1-USER"),
        ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
        ValidTo = DateTimeOffset.UtcNow.AddDays(1),
        ColumnRules = rules
    };

    [Fact]
    public void SharedHelper_ScopeOnlyMaskForScopeA_CallerWithScopeB_IsDenied()
    {
        var consent = AllowWith(Rule("salary", ColumnAccessLevel.Mask, "scope-a"));

        ConsentResolutionService.ResolveConsentColumnLevel(consent, "salary", Scopes("scope-b")).ShouldBe(ColumnAccessLevel.Deny);
        ConsentResolutionService.ResolveConsentColumnLevel(consent, "salary", null).ShouldBe(ColumnAccessLevel.Deny);
        ConsentResolutionService.ResolveConsentColumnLevel(consent, "salary", Scopes("scope-a")).ShouldBe(ColumnAccessLevel.Mask);
    }

    [Fact]
    public void SharedHelper_BaseClearPlusScopeMaskA_BIsClear_AIsMasked()
    {
        var consent = AllowWith(
            Rule("salary", ColumnAccessLevel.Clear),
            Rule("salary", ColumnAccessLevel.Mask, "scope-a"));

        ConsentResolutionService.ResolveConsentColumnLevel(consent, "salary", Scopes("scope-b")).ShouldBe(ColumnAccessLevel.Clear);
        ConsentResolutionService.ResolveConsentColumnLevel(consent, "salary", Scopes("scope-a")).ShouldBe(ColumnAccessLevel.Mask);
    }

    [Fact]
    public void SharedHelper_RuleOrderDoesNotChangeStrictestApplicableLevel()
    {
        var clearA = Rule("salary", ColumnAccessLevel.Clear, "scope-a");
        var maskA = Rule("salary", ColumnAccessLevel.Mask, "scope-a");
        var denyB = Rule("salary", ColumnAccessLevel.Deny, "scope-b");

        var forward = AllowWith(clearA, maskA, denyB);
        var reversed = AllowWith(denyB, maskA, clearA);

        foreach (var consent in new[] { forward, reversed })
        {
            ConsentResolutionService.ResolveConsentColumnLevel(consent, "salary", Scopes("scope-a")).ShouldBe(ColumnAccessLevel.Mask);
            ConsentResolutionService.ResolveConsentColumnLevel(consent, "salary", Scopes("scope-a", "scope-b")).ShouldBe(ColumnAccessLevel.Deny);
            ConsentResolutionService.ResolveConsentColumnLevel(consent, "salary", Scopes("scope-c")).ShouldBe(ColumnAccessLevel.Deny);
        }
    }

    [Fact]
    public void IsScopeSpecific_DistinguishesBaseAndScopeRules()
    {
        ConsentResolutionService.IsScopeSpecific(Rule("c", ColumnAccessLevel.Clear)).ShouldBeFalse();
        ConsentResolutionService.IsScopeSpecific(Rule("c", ColumnAccessLevel.Clear, "scope-a")).ShouldBeTrue();
    }

    [Fact]
    public void CatalogVisibility_ScopeOnlyColumnRule_DoesNotExposeColumnToEveryone()
    {
        var table = new TableMetadata
        {
            Identifier = Orders,
            Table = new Table
            {
                SourceName = "sales", SchemaName = "public", TableName = "orders",
                DisplayName = "sales.orders", Sensitivity = "NORMAL",
                Visibility = DatasetVisibility.Hidden
            },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "salary", DataType = "int" }
            ],
            PrimaryKeyColumns = ["id"]
        };

        var consent = AllowWith(
            Rule("id", ColumnAccessLevel.Clear),
            Rule("salary", ColumnAccessLevel.Clear, "scope-a"));

        var visible = CatalogVisibility.FilterForSubject([table], [consent], Tenant);

        var columns = visible.Single().Columns.Select(c => c.ColumnName).ToList();
        columns.ShouldContain("id");
        columns.ShouldNotContain("salary");
    }
}
