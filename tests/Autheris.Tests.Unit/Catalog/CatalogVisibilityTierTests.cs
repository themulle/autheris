namespace Autheris.Tests.Unit.Catalog;

using Autheris.Tests.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// Issue #111 WP-2 (T-2 items 8-12, M-2, T-R2-4, T-R2-5, F-C13/F-C14, A-1 G1/G2): persisted visibility tiers apply only on the
/// <see cref="CatalogSurface.Discovery"/> surface, only to shared tables in Multi mode, and every returned row carries an explicit view.
/// </summary>
public sealed class CatalogVisibilityTierTests
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";
    private static readonly TableIdentifier Shared = new("shared", "public", "catalog_t");
    private static readonly TableIdentifier Other = new("crm", "public", "other_t");
    private static readonly string[] SharedList = ["shared.public.catalog_t"];

    private static TableMetadata Table(TableIdentifier id, DatasetVisibility visibility, bool releaseProfile = false, IReadOnlyDictionary<string, MaskingRule>? masking = null) => new()
    {
        Identifier = id,
        Table = new Table { SourceName = id.Domain, SchemaName = id.Schema, TableName = id.TableName, DisplayName = id.TableName, Visibility = visibility },
        Columns = [new TableColumn { ColumnName = "id", DataType = "int" }, new TableColumn { ColumnName = "secret_col", DataType = "varchar" }],
        ColumnMaskingRules = masking ?? new Dictionary<string, MaskingRule>(),
        ReleaseProfileToCatalogViewers = releaseProfile
    };

    private static Consent Allow(TableIdentifier id, string tenant = TenantA, ConsentEffect effect = ConsentEffect.Allow) => new()
    {
        TableIdentifier = id,
        TenantId = new TenantId(tenant),
        Effect = effect,
        ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
        ValidTo = DateTimeOffset.UtcNow.AddDays(1)
    };

    private static Consent Deny(TableIdentifier id, string tenant = TenantA) => Allow(id, tenant, ConsentEffect.Deny);

    private static IReadOnlyList<TableMetadata> Filter(
        IEnumerable<TableMetadata> tables, IEnumerable<Consent>? consents = null, CatalogSurface surface = CatalogSurface.Discovery,
        string[]? shared = null, TenancyShape mode = TenancyShape.Multi, string tenant = TenantA) =>
        CatalogVisibility.FilterForSubject(tables, consents ?? [], new TenantId(tenant), null, surface, shared, mode.Owner());

    // ---- DataAccess (default): the tier switch is not executed at all (F-C13, G2) ----

    [Fact]
    [Trait("Category", "Security")]
    public void DataAccess_PersistedSummaryOrFull_WithoutConsent_Dropped()
    {
        var tables = new[] { Table(Shared, DatasetVisibility.Summary), Table(Other, DatasetVisibility.Full), Table(new("a", "b", "c"), DatasetVisibility.Hidden) };

        CatalogVisibility.FilterForSubject(tables, [], new TenantId(TenantA)).ShouldBeEmpty();
        Filter(tables, surface: CatalogSurface.DataAccess, shared: SharedList).ShouldBeEmpty();
        Filter(tables, surface: CatalogSurface.DataAccess, mode: TenancyShape.Single).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task VisibleTables_8ParamOverload_DefaultsToDataAccess()
    {
        var tables = new[] { Table(Shared, DatasetVisibility.Summary), Table(Other, DatasetVisibility.Full) };
        var consents = ConsentRepo([]);
        var user = User(TenantA);

        var viaOverload = await CatalogVisibility.VisibleTablesAsync(tables, user, new TenantId(TenantA), consents, contract: null, TenancyShape.Single.Owner(), sharedTables: SharedList);
        var viaShort = await CatalogVisibility.VisibleTablesAsync(tables, user, new TenantId(TenantA), consents);
        var viaMcp = await McpCatalogVisibility.VisibleTablesAsync(tables, user, consents, new GatewayOptions { Tenancy = new TenancyOptions { DefaultTenantId = "default-tenant" } }, CancellationToken.None);

        viaOverload.ShouldBeEmpty();
        viaShort.ShouldBeEmpty();
        viaMcp.ShouldBeEmpty();
    }

    // ---- Discovery ----

    [Fact]
    public void Discovery_Single_HiddenAbsent_SummaryWithoutColumns_FullWithColumns()
    {
        var tables = new[]
        {
            Table(new("a", "s", "hidden_t"), DatasetVisibility.Hidden),
            Table(new("a", "s", "summary_t"), DatasetVisibility.Summary),
            Table(new("a", "s", "full_t"), DatasetVisibility.Full)
        };

        var visible = Filter(tables, mode: TenancyShape.Single, tenant: Autheris.Tests.Common.TestTenants.Default.Value);

        visible.Select(t => t.Identifier.TableName).ShouldBe(["summary_t", "full_t"]);
        visible[0].Columns.ShouldBeEmpty();
        visible[0].CatalogView.ShouldBe(CatalogView.Summary);
        visible[1].Columns.Count.ShouldBe(2);
        visible[1].CatalogView.ShouldBe(CatalogView.Full);
    }

    [Fact]
    public void Discovery_Summary_IgnoresReleaseProfileToCatalogViewers()
    {
        var visible = Filter([Table(Shared, DatasetVisibility.Summary, releaseProfile: true)], shared: SharedList);

        visible.ShouldHaveSingleItem().Columns.ShouldBeEmpty();
    }

    [Fact]
    public void Discovery_Full_RemovesTableLevelDenyMaskedColumns()
    {
        var masking = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
        {
            ["secret_col"] = new MaskingRule { RuleType = "DENY" },
            ["id"] = new MaskingRule { RuleType = "REDACT" }
        };

        var visible = Filter([Table(Shared, DatasetVisibility.Full, masking: masking)], shared: SharedList);

        visible.ShouldHaveSingleItem().Columns.Select(c => c.ColumnName).ShouldBe(["id"]);
    }

    [Fact]
    public void Discovery_UnknownStoredVisibilityValue_IsDropped_NeverFallsThrough()
    {
        var visible = Filter([Table(Shared, (DatasetVisibility)9)], shared: SharedList);

        visible.ShouldBeEmpty();
    }

    [Fact]
    public void Discovery_ConsentPath_TakesPrecedence_AndIsMarkedGranted()
    {
        var visible = Filter([Table(Shared, DatasetVisibility.Summary)], [Allow(Shared)], shared: SharedList);

        var row = visible.ShouldHaveSingleItem();
        row.CatalogView.ShouldBe(CatalogView.Granted);
        row.Columns.Count.ShouldBe(2);
    }

    [Fact]
    public void Discovery_UnconditionalDenyOnHiddenTable_KeepsItHidden_AndOnSummaryListsSummary()
    {
        var hidden = Table(Other, DatasetVisibility.Hidden);
        var summary = Table(Shared, DatasetVisibility.Summary);

        var visible = Filter([hidden, summary], [Allow(Other), Deny(Other), Allow(Shared), Deny(Shared)], shared: ["crm.public.other_t", "shared.public.catalog_t"]);

        visible.ShouldHaveSingleItem().Identifier.ShouldBe(Shared);
        visible[0].CatalogView.ShouldBe(CatalogView.Summary);
        visible[0].Columns.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void T_R2_4_EveryReturnedRow_HasAnExplicitNonUnmarkedView()
    {
        var tables = new[]
        {
            Table(Shared, DatasetVisibility.Summary),
            Table(Other, DatasetVisibility.Hidden),
            Table(new("x", "y", "z"), DatasetVisibility.Full)
        };

        var visible = Filter(tables, [Allow(Other)], shared: ["shared.public.catalog_t", "x.y.z"]);

        visible.Count.ShouldBe(3);
        visible.ShouldAllBe(t => t.CatalogView != CatalogView.Unmarked);
    }

    // ---- Tenant gate (M-2, T-R2-5) ----

    [Fact]
    [Trait("Category", "Security")]
    public void M2_Multi_TiersApplyOnlyToSharedTables_OtherTenantConsentNeverMakesTableVisible()
    {
        var tables = new[] { Table(Shared, DatasetVisibility.Summary), Table(Other, DatasetVisibility.Summary) };

        // Tenant B holds consent records on the tenant-A table; consents never extend the shared set.
        var visible = Filter(tables, [Allow(Other, TenantB), Deny(Other, TenantB)], shared: SharedList, tenant: TenantB);

        visible.ShouldHaveSingleItem().Identifier.ShouldBe(Shared);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void M2_Multi_NullOrEmptySharedTables_TiersAreOff()
    {
        var tables = new[] { Table(Shared, DatasetVisibility.Full) };

        Filter(tables, shared: null).ShouldBeEmpty();
        Filter(tables, shared: []).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Multi_MalformedSharedEntries_AreSkipped_NeverWiden()
    {
        var tables = new[] { Table(Shared, DatasetVisibility.Summary), Table(Other, DatasetVisibility.Summary) };

        var visible = Filter(tables, shared: ["", "  ", "not-a-table", "*", "a.b", "shared.public.catalog_t"]);

        visible.ShouldHaveSingleItem().Identifier.ShouldBe(Shared);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Multi_LegacyTenantId_IsANormalTenant_TiersFollowSharedTables()
    {
        var tables = new[] { Table(Shared, DatasetVisibility.Full), Table(Other, DatasetVisibility.Full) };

        // DT-A-7: no tenant id is special any more; a principal without tenant and without default never reaches the filter (scope None).
        Filter(tables, shared: SharedList, tenant: Autheris.Tests.Common.TestTenants.Default.Value).ShouldHaveSingleItem().Identifier.ShouldBe(Shared);
        Filter(tables, tenant: Autheris.Tests.Common.TestTenants.Default.Value).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task VisibleTables_Multi_ClaimlessPrincipal_NeverGetsTiers_EvenForSharedTable()
    {
        var tables = new[] { Table(Shared, DatasetVisibility.Full) };
        var claimless = User(tenant: null);

        var visible = await CatalogVisibility.VisibleTablesAsync(
            tables, claimless, Autheris.Tests.Common.TestTenants.Default, ConsentRepo([]), contract: null, TenancyShape.Multi.Owner(), SharedList, CatalogSurface.Discovery);

        visible.ShouldBeEmpty();
    }

    [Fact]
    public async Task VisibleTables_Discovery_Subject_ShowsSharedTier_AndDataAccessDoesNot()
    {
        var tables = new[] { Table(Shared, DatasetVisibility.Summary), Table(Other, DatasetVisibility.Summary) };
        var user = User(TenantA);

        var discovery = await CatalogVisibility.VisibleTablesAsync(
            tables, user, new TenantId(TenantA), ConsentRepo([]), contract: null, TenancyShape.Multi.Owner(), SharedList, CatalogSurface.Discovery);
        var dataAccess = await CatalogVisibility.VisibleTablesAsync(
            tables, user, new TenantId(TenantA), ConsentRepo([]), contract: null, TenancyShape.Multi.Owner(), SharedList, CatalogSurface.DataAccess);

        discovery.ShouldHaveSingleItem().Identifier.ShouldBe(Shared);
        dataAccess.ShouldBeEmpty();
    }

    // ---- Policy neutrality of catalog tags (T-R2-1, T-R2-2, T-R3-1, O-6a) ----

    [Fact]
    [Trait("Category", "Security")]
    public void T_R2_1_CatalogTags_ChangeNoContractResult_AndNoGrantClassification()
    {
        var plain = Table(Shared, DatasetVisibility.Full);
        var tagged = plain with { CatalogTags = ["secret", "pii", "gdpr", "included", "excluded"] };

        var excluding = new Autheris.Application.Governance.Contracts.SchemaContractDefinition("c", excludedTags: ["secret", "excluded"]);
        var including = new Autheris.Application.Governance.Contracts.SchemaContractDefinition("c", includedTags: ["included", "secret"]);

        CatalogVisibility.FilterByContract([tagged], excluding).Count.ShouldBe(CatalogVisibility.FilterByContract([plain], excluding).Count);
        CatalogVisibility.FilterByContract([tagged], excluding).Count.ShouldBe(1);
        CatalogVisibility.FilterByContract([tagged], including).Count.ShouldBe(CatalogVisibility.FilterByContract([plain], including).Count);
        CatalogVisibility.FilterByContract([tagged], including).Count.ShouldBe(0);
        Autheris.Application.Governance.Grants.GrantClassifier.IsSensitive(tagged)
            .ShouldBe(Autheris.Application.Governance.Grants.GrantClassifier.IsSensitive(plain));
        Autheris.Application.Governance.Grants.GrantClassifier.IsSensitive(tagged).ShouldBeFalse();
    }

    // ---- Admin rows (G1) ----

    [Fact]
    [Trait("Category", "Security")]
    public async Task TenantAdmin_SharedTable_PersistedHidden_Behaviour_Pinned()
    {
        // Accepted behaviour: a tenant admin sees shared-table rows by FilterToTenant (tiers do not narrow admin views), and the row is not stamped.
        var tables = new[] { Table(Shared, DatasetVisibility.Hidden), Table(Other, DatasetVisibility.Hidden) };
        var admin = User(TenantA, "GovernanceAdmin");

        var visible = await CatalogVisibility.VisibleTablesAsync(
            tables, admin, new TenantId(TenantA), ConsentRepo([]), contract: null, TenancyShape.Multi.Owner(), SharedList, CatalogSurface.Discovery);

        visible.ShouldHaveSingleItem().Identifier.ShouldBe(Shared);
        visible[0].CatalogView.ShouldBe(CatalogView.Unmarked);
    }

    private static ClaimsPrincipal User(string? tenant, params string[] roles)
    {
        var claims = new List<Claim>
        {
            new("sub", "S-1-USER"),
            new("http://schemas.microsoft.com/ws/2008/06/identity/claims/primarysid", "S-1-USER")
        };
        if (tenant != null)
        {
            claims.Add(new("tenant_id", tenant));
        }

        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static IConsentRepository ConsentRepo(IReadOnlyList<Consent> consents)
    {
        var repo = Substitute.For<IConsentRepository>();
        repo.GetAllActiveConsentsForSubjectsAsync(
                Arg.Any<IEnumerable<Sid>>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>(consents));
        repo.GetTablesWithConsentsForTenantAsync(Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TableIdentifier>>([]));
        return repo;
    }
}
