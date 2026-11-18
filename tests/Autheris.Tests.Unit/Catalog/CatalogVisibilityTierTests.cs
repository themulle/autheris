namespace Autheris.Tests.Unit.Catalog;

using Autheris.Application.Catalog.Discovery;
using Autheris.Tests.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
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

    private static TableMetadata Table(TableIdentifier id, IReadOnlyDictionary<string, MaskingRule>? masking = null) => new()
    {
        Identifier = id,
        Table = new Table { SourceName = id.Domain, SchemaName = id.Schema, TableName = id.TableName, DisplayName = id.TableName },
        Columns = [new TableColumn { ColumnName = "id", DataType = "int" }, new TableColumn { ColumnName = "secret_col", DataType = "varchar" }],
        ColumnMaskingRules = masking ?? new Dictionary<string, MaskingRule>()
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
        IEnumerable<TableMetadata> tables, IEnumerable<Consent>? consents = null,
        string[]? shared = null, TenancyShape mode = TenancyShape.Multi, string tenant = TenantA) =>
        CatalogVisibility.FilterForSubject(tables, consents ?? [], new TenantId(tenant), null);

    // ---- Discovery column handling ----

    [Fact]
    public void Discovery_RemovesTableLevelDenyMaskedColumns()
    {
        var masking = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
        {
            ["secret_col"] = new MaskingRule { RuleType = "DENY" },
            ["id"] = new MaskingRule { RuleType = "REDACT" }
        };

        var visible = Filter([Table(Shared, masking: masking)], [Allow(Shared)]);

        visible.ShouldHaveSingleItem().Columns.Select(c => c.ColumnName).ShouldBe(["id"]);
    }

    // ---- Tenant gate ----

    [Fact]
    [Trait("Category", "Security")]
    public void Multi_MalformedSharedEntries_AreSkipped_NeverWiden()
    {
        var shared = DiscoveryTenantScope.ParseSharedTables(["", "  ", "not-a-table", "*", "a.b", "shared.public.catalog_t"]);
        var empty = new HashSet<TableIdentifier>();

        shared.ShouldContain(Shared);
        shared.ShouldNotContain(Other);
        shared.Count.ShouldBe(2); // "a.b" is the valid two-part form (default.a.b); every other malformed entry is dropped.
        DiscoveryTenantScope.IsInScope(Table(Shared), new TenantId(TenantA), catalogOwner: null, shared, empty).ShouldBeTrue();
        DiscoveryTenantScope.IsInScope(Table(Other), new TenantId(TenantA), catalogOwner: null, shared, empty).ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Multi_LegacyTenantId_IsANormalTenant_SharedTablesStillDecideScope()
    {
        var legacy = Autheris.Tests.Common.TestTenants.Default;
        var shared = DiscoveryTenantScope.ParseSharedTables(SharedList);
        var empty = new HashSet<TableIdentifier>();

        DiscoveryTenantScope.IsInScope(Table(Shared), legacy, catalogOwner: null, shared, empty).ShouldBeTrue();
        DiscoveryTenantScope.IsInScope(Table(Other), legacy, catalogOwner: null, shared, empty).ShouldBeFalse();
        DiscoveryTenantScope.IsInScope(Table(Shared), legacy, catalogOwner: null, DiscoveryTenantScope.ParseSharedTables(null), empty).ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task VisibleTables_Multi_ClaimlessPrincipal_SeesNothing_EvenWithConsent()
    {
        var claimless = User(tenant: null);

        var visible = await CatalogVisibility.VisibleTablesAsync(
            [Table(Shared)], claimless, Autheris.Tests.Common.TestTenants.Default, ConsentRepo([Allow(Shared, Autheris.Tests.Common.TestTenants.Default.Value)]));

        visible.ShouldBeEmpty();
    }

    // ---- Policy neutrality of catalog tags (T-R2-1, T-R2-2, T-R3-1, O-6a) ----

    [Fact]
    [Trait("Category", "Security")]
    public void T_R2_1_CatalogTags_ChangeNoContractResult_AndNoGrantClassification()
    {
        var plain = Table(Shared);
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
