namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Shouldly;
using Xunit;

public sealed class MetricViewMeasureConsentTests
{
    private static readonly TenantId Tenant = new("tenant-a");
    private static readonly Sid UserSid = new("S-1-5-21-USER");
    private static readonly TableIdentifier MvId = new("dbx", "analytics", "sales_mv");

    private static TableMetadata MakeMetricView() => new()
    {
        Identifier = MvId,
        Table = new Table
        {
            Id = Guid.NewGuid(),
            SourceType = "Databricks",
            SourceName = "dbx",
            SchemaName = "analytics",
            TableName = "sales_mv",
            ObjectKind = DiscoveryObjectKind.MetricView,
            MetricViewCountMeasure = "row_count",
            IsActive = true
        },
        Columns =
        [
            new TableColumn { ColumnName = "region", DataType = "string", Role = MetricViewColumnRole.Dimension },
            new TableColumn { ColumnName = "revenue", DataType = "decimal(18,2)", Role = MetricViewColumnRole.Measure },
            new TableColumn { ColumnName = "row_count", DataType = "bigint", Role = MetricViewColumnRole.Measure },
            new TableColumn { ColumnName = "undeclared", DataType = "string", DiscoveryHidden = true, Role = null }
        ]
    };

    private static Consent MakeConsent(
        bool isPatternGrant = false,
        ColumnSetMode columnSetMode = ColumnSetMode.AllNonHidden,
        params (string Col, ColumnAccessLevel Level)[] rules) =>
        new()
        {
            TableIdentifier = isPatternGrant ? default : MvId,
            ObjectPattern = isPatternGrant ? "dbx.analytics.*" : null,
            DataSource = isPatternGrant ? "dbx" : null,
            ColumnSetMode = columnSetMode,
            TenantId = Tenant,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = UserSid,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(1),
            ColumnRules = Array.ConvertAll(rules, r => new ConsentColumnRule { ColumnName = r.Col, AccessLevel = r.Level })
        };

    [Fact]
    [Trait("Category", "Security")]
    public void TableWideAndPatternConsents_AllowDimensions_DenyMeasuresAndNullRole()
    {
        var resolver = new ConsentResolutionService();
        var mv = MakeMetricView();

        // 1. Concrete table-wide grant (ColumnRules empty)
        var tableWideDecision = resolver.ResolveAccess(
            UserSid, new HashSet<Sid>(), new HashSet<string>(), MvId, [MakeConsent(isPatternGrant: false)], DatabaseDialect.Databricks, null, null, mv);
        tableWideDecision.IsAllowed.ShouldBeTrue();
        tableWideDecision.GetEffectiveColumnAccess("region", mv).ShouldBe(ColumnAccessLevel.Clear);
        tableWideDecision.GetEffectiveColumnAccess("revenue", mv).ShouldBe(ColumnAccessLevel.Deny);
        tableWideDecision.GetEffectiveColumnAccess("row_count", mv).ShouldBe(ColumnAccessLevel.Deny);
        tableWideDecision.GetEffectiveColumnAccess("undeclared", mv).ShouldBe(ColumnAccessLevel.Deny);
        tableWideDecision.GetEffectiveColumnAccess("unknown_col", mv).ShouldBe(ColumnAccessLevel.Deny);

        // 2. Pattern grant (ColumnSetMode = AllNonHidden, empty ColumnRules)
        var patternAllDecision = resolver.ResolveAccess(
            UserSid, new HashSet<Sid>(), new HashSet<string>(), MvId, [MakeConsent(isPatternGrant: true, ColumnSetMode.AllNonHidden)], DatabaseDialect.Databricks, null, null, mv);
        patternAllDecision.IsAllowed.ShouldBeTrue();
        patternAllDecision.GetEffectiveColumnAccess("region", mv).ShouldBe(ColumnAccessLevel.Clear);
        patternAllDecision.GetEffectiveColumnAccess("revenue", mv).ShouldBe(ColumnAccessLevel.Deny);
        patternAllDecision.GetEffectiveColumnAccess("undeclared", mv).ShouldBe(ColumnAccessLevel.Deny);

        // 3. Pattern grant mentioning only dimension
        var patternDimOnlyDecision = resolver.ResolveAccess(
            UserSid, new HashSet<Sid>(), new HashSet<string>(), MvId,
            [MakeConsent(isPatternGrant: true, ColumnSetMode.AllNonHidden, ("region", ColumnAccessLevel.Clear))],
            DatabaseDialect.Databricks, null, null, mv);
        patternDimOnlyDecision.GetEffectiveColumnAccess("region", mv).ShouldBe(ColumnAccessLevel.Clear);
        patternDimOnlyDecision.GetEffectiveColumnAccess("revenue", mv).ShouldBe(ColumnAccessLevel.Deny);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ExplicitMeasureRule_GrantsMeasureAccess_OnConcreteAndPatternConsents()
    {
        var resolver = new ConsentResolutionService();
        var mv = MakeMetricView();

        var explicitConcrete = resolver.ResolveAccess(
            UserSid, new HashSet<Sid>(), new HashSet<string>(), MvId,
            [MakeConsent(isPatternGrant: false, ColumnSetMode.AllNonHidden, ("region", ColumnAccessLevel.Clear), ("revenue", ColumnAccessLevel.Clear))],
            DatabaseDialect.Databricks, null, null, mv);
        explicitConcrete.GetEffectiveColumnAccess("region", mv).ShouldBe(ColumnAccessLevel.Clear);
        explicitConcrete.GetEffectiveColumnAccess("revenue", mv).ShouldBe(ColumnAccessLevel.Clear);
        explicitConcrete.GetEffectiveColumnAccess("row_count", mv).ShouldBe(ColumnAccessLevel.Deny);

        var explicitPattern = resolver.ResolveAccess(
            UserSid, new HashSet<Sid>(), new HashSet<string>(), MvId,
            [MakeConsent(true, ColumnSetMode.Explicit, ("revenue", ColumnAccessLevel.Clear))],
            DatabaseDialect.Databricks, null, null, mv);
        explicitPattern.IsAllowed.ShouldBeTrue();
        explicitPattern.GetEffectiveColumnAccess("revenue", mv).ShouldBe(ColumnAccessLevel.Clear);
        explicitPattern.GetEffectiveColumnAccess("row_count", mv).ShouldBe(ColumnAccessLevel.Deny);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void CatalogCallerColumnLevels_MetricView_DeniesUnconsentedMeasuresAndNullRole()
    {
        var resolver = new ConsentResolutionService();
        var mv = MakeMetricView();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.PrimarySid, UserSid.Value), new Claim("tenant_id", Tenant.Value)], "Test"));

        var granted = CatalogCallerColumnLevels.Resolve(
            resolver, principal, mv, [MakeConsent(isPatternGrant: false)], isAdmin: false, isGranted: true, bypass: false, logger: null);
        granted["region"].ShouldBe(ColumnAccessLevel.Clear);
        granted["revenue"].ShouldBe(ColumnAccessLevel.Deny);
        granted["undeclared"].ShouldBe(ColumnAccessLevel.Deny);

        var baseline = CatalogCallerColumnLevels.Resolve(
            resolver, principal, mv, [], isAdmin: false, isGranted: false, bypass: false, logger: null);
        baseline["region"].ShouldBe(ColumnAccessLevel.Clear);
        baseline["revenue"].ShouldBe(ColumnAccessLevel.Deny);
        baseline["undeclared"].ShouldBe(ColumnAccessLevel.Deny);
    }
}
