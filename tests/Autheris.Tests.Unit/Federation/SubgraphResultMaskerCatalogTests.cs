namespace Autheris.Tests.Unit.Federation;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Federation.Interfaces;
using Autheris.Application.Federation.Services;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.GraphQL.Federation;
using HotChocolate.Language;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class SubgraphResultMaskerCatalogTests
{
    private static readonly ClaimsPrincipal Reader = ReaderFor("tenant-a");

    private static ClaimsPrincipal ReaderFor(string tenant) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.Role, "Reader"), new Claim("tenant_id", tenant)], "TestAuth"));

    private static IFederatedMaskingCatalog CatalogWith(string tenant, params string[] sensitiveNormalizedNames)
    {
        var catalog = Substitute.For<IFederatedMaskingCatalog>();
        catalog.GetClassificationsAsync(Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var map = new Dictionary<string, MaskingRule>();
                if (ci.Arg<TenantId>().Value == tenant)
                {
                    foreach (var n in sensitiveNormalizedNames)
                    {
                        map[n] = new MaskingRule { RuleType = "REDACT" };
                    }
                }
                return new ValueTask<IReadOnlyDictionary<string, MaskingRule>>(map);
            });
        return catalog;
    }

    private static SubgraphResultMasker CreateMasker(
        IFederatedMaskingCatalog? catalog,
        FederationOptions? federation = null,
        IGatewayRoleEvaluator? roleEvaluator = null)
    {
        var provider = Substitute.For<IColumnMaskingProvider>();
        provider.MaskValue(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<MaskingRule>()).Returns("[MASKED]");
        var options = Options.Create(new GatewayOptions
        {
            Federation = federation ?? new FederationOptions { Enabled = true, EnableResultMasking = true }
        });
        return new SubgraphResultMasker(provider, options, NullLogger<SubgraphResultMasker>.Instance, roleEvaluator, catalog);
    }

    private static Dictionary<string, object?> Row(params (string Key, object? Value)[] fields)
    {
        var d = new Dictionary<string, object?>();
        foreach (var (k, v) in fields) d[k] = v;
        return d;
    }

    [Fact]
    public async Task MaskResultData_FieldClassifiedSensitiveInCatalog_IsMasked_EvenWithoutNameMatch()
    {
        var masker = CreateMasker(CatalogWith("tenant-a", "loyaltycode"));
        var data = Row(("loyaltyCode", "LC-1"), ("name", "Acme"));

        var result = await masker.MaskResultDataAsync(data, Reader, null) as IReadOnlyDictionary<string, object?>;

        result.ShouldNotBeNull();
        result["loyaltyCode"].ShouldBe("[MASKED]");
        result["name"].ShouldBe("Acme");
    }

    [Fact]
    public async Task MaskResultData_UnderscoreCatalogName_MatchesCamelCaseField()
    {
        var masker = CreateMasker(CatalogWith("tenant-a", FederatedFieldName.Normalize("loyalty_code")));

        var result = await masker.MaskResultDataAsync(Row(("loyaltyCode", "LC-1")), Reader, null) as IReadOnlyDictionary<string, object?>;

        result!["loyaltyCode"].ShouldBe("[MASKED]");
    }

    [Fact]
    public async Task MaskResultData_HeuristicFieldsRemainMasked_WithCatalogPresent()
    {
        var masker = CreateMasker(CatalogWith("tenant-a"));

        var result = await masker.MaskResultDataAsync(Row(("userEmail", "a@b.c"), ("taxNumber", "12345"), ("passportNo", "X1")), Reader, null) as IReadOnlyDictionary<string, object?>;

        result!["userEmail"].ShouldBe("[MASKED]");
        result["taxNumber"].ShouldBe("[MASKED]");
        result["passportNo"].ShouldBe("[MASKED]");
    }

    [Fact]
    public async Task ConfigMap_OverridesHeuristic()
    {
        var federation = new FederationOptions
        {
            Enabled = true,
            EnableResultMasking = true,
            FieldClassifications = new Dictionary<string, string>
            {
                ["crm.Customer.contactEmail"] = "NONE",
                ["crm.Customer.nickname"] = "REDACT"
            }
        };
        var masker = CreateMasker(CatalogWith("tenant-a"), federation);

        var result = await masker.MaskResultDataAsync(Row(("contactEmail", "a@b.c"), ("nickname", "bob")), Reader, null) as IReadOnlyDictionary<string, object?>;

        result!["contactEmail"].ShouldBe("a@b.c");
        result["nickname"].ShouldBe("[MASKED]");
    }

    [Fact]
    public async Task CatalogLookupFails_FallsBackToHeuristic()
    {
        var catalog = Substitute.For<IFederatedMaskingCatalog>();
        catalog.GetClassificationsAsync(Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns<ValueTask<IReadOnlyDictionary<string, MaskingRule>>>(_ => throw new InvalidOperationException("catalog down"));
        var masker = CreateMasker(catalog);

        var result = await masker.MaskResultDataAsync(Row(("userEmail", "a@b.c"), ("name", "Acme")), Reader, null) as IReadOnlyDictionary<string, object?>;

        result!["userEmail"].ShouldBe("[MASKED]");
        result["name"].ShouldBe("Acme");
    }

    [Fact]
    public async Task CatalogLookupFails_WithDefaultMaskUnknownScalars_MasksNonIdentifierScalars()
    {
        var catalog = Substitute.For<IFederatedMaskingCatalog>();
        catalog.GetClassificationsAsync(Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns<ValueTask<IReadOnlyDictionary<string, MaskingRule>>>(_ => throw new InvalidOperationException("catalog down"));
        var federation = new FederationOptions { Enabled = true, EnableResultMasking = true, DefaultMaskUnknownSensitiveScalars = true };
        var masker = CreateMasker(catalog, federation);

        var data = Row(("id", "1"), ("customerId", "2"), ("loyaltyCode", "LC-1"), ("nested", Row(("note", "x"))));
        var result = await masker.MaskResultDataAsync(data, Reader, null) as IReadOnlyDictionary<string, object?>;

        result!["id"].ShouldBe("1");
        result["customerId"].ShouldBe("2");
        result["loyaltyCode"].ShouldBe("[MASKED]");
        ((IReadOnlyDictionary<string, object?>)result["nested"]!)["note"].ShouldBe("[MASKED]");
    }

    [Fact]
    public async Task TenantIsolation_OtherTenantClassificationNotApplied()
    {
        var masker = CreateMasker(CatalogWith("tenant-a", "loyaltycode"));

        var result = await masker.MaskResultDataAsync(Row(("loyaltyCode", "LC-1")), ReaderFor("tenant-b"), null) as IReadOnlyDictionary<string, object?>;

        result!["loyaltyCode"].ShouldBe("LC-1");
    }

    [Fact]
    public async Task Admin_Unmasked()
    {
        var masker = CreateMasker(CatalogWith("tenant-a", "loyaltycode"));
        var admin = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, "GovernanceAdmin"), new Claim("tenant_id", "tenant-a")], "TestAuth"));

        var result = await masker.MaskResultDataAsync(Row(("loyaltyCode", "LC-1")), admin, null) as IReadOnlyDictionary<string, object?>;

        result!["loyaltyCode"].ShouldBe("LC-1");
    }

    [Fact]
    public async Task AliasPath_UsesRealFieldForCatalogLookup()
    {
        var masker = CreateMasker(CatalogWith("tenant-a", "loyaltycode"));
        var map = SubgraphResultMaskingMiddleware.ExtractAliasToFieldMap(Utf8GraphQLParser.Parse("{ customer { harmless: loyaltyCode } }"));
        var data = Row(("customer", Row(("harmless", "LC-1"))));

        var result = await masker.MaskResultDataAsync(data, Reader, map) as IReadOnlyDictionary<string, object?>;

        ((IReadOnlyDictionary<string, object?>)result!["customer"]!)["harmless"].ShouldBe("[MASKED]");
    }

    [Fact]
    public async Task AliasCollision_NonHeuristicTargets_KeepsBothSoCatalogApplies()
    {
        var masker = CreateMasker(CatalogWith("tenant-a", "loyaltycode"));
        var map = SubgraphResultMaskingMiddleware.ExtractAliasToFieldMap(
            Utf8GraphQLParser.Parse("{ node { ... on A { x: loyaltyCode } ... on B { x: name } } }"));
        map.ShouldNotBeNull();
        map["node.x"].ShouldBe("loyaltyCode|name");

        var data = Row(("node", Row(("x", "LC-1"))));
        var result = await masker.MaskResultDataAsync(data, Reader, map) as IReadOnlyDictionary<string, object?>;

        ((IReadOnlyDictionary<string, object?>)result!["node"]!)["x"].ShouldBe("[MASKED]");
    }

    [Fact]
    public async Task CatalogFederatedMaskingCatalog_MapsSensitiveColumns_AndCachesPerTenant()
    {
        var repo = Substitute.For<ITableMetadataRepository>();
        var table = new TableMetadata
        {
            Columns =
            [
                new TableColumn { ColumnName = "loyalty_code", IsSensitive = true },
                new TableColumn { ColumnName = "note", IsSensitive = false },
                new TableColumn { ColumnName = "pin", IsSensitive = false }
            ],
            ColumnMaskingRules = new Dictionary<string, MaskingRule> { ["pin"] = new MaskingRule { RuleType = "NULLIFY" } }
        };
        repo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<TableMetadata>>([table]));
        var options = Options.Create(new GatewayOptions { Federation = new FederationOptions { CatalogMaskingCacheSeconds = 60 } });
        var catalog = new CatalogFederatedMaskingCatalog(repo, options);

        var map = await catalog.GetClassificationsAsync(new TenantId("tenant-a"));
        await catalog.GetClassificationsAsync(new TenantId("tenant-a"));

        map.ShouldContainKey("loyaltycode");
        map["pin"].RuleType.ShouldBe("NULLIFY");
        map.ShouldNotContainKey("note");
        await repo.Received(1).GetAllTablesAsync(Arg.Any<CancellationToken>());

        await catalog.GetClassificationsAsync(new TenantId("tenant-b"));
        await repo.Received(2).GetAllTablesAsync(Arg.Any<CancellationToken>());
    }
}
