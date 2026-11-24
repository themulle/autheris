namespace Autheris.Tests.Unit.Governance.Classification;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Policy.Classification;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.Services;
using Autheris.Application.Sql.Masking;
using Autheris.Application.Sql.Policy;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Tests.Unit.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Governance;
using Xunit;

[Trait("Category", "Security")]
public sealed class ClassificationAccessResolverTests
{
    private static readonly TenantId Tenant = new("tenant-alpha");
    private static readonly Sid UserSid = new("S-1-5-21-USER-1");
    private static readonly Sid ApproverSid = new("S-1-5-21-OWNER-1");
    private static readonly TableIdentifier CustomersTable = new("crm", "public", "customers");
    private static readonly DateTimeOffset FixedNow = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    private static ClassificationAccessResolver CreateResolver(
        IClassificationRepository? repo = null,
        TimeProvider? timeProvider = null,
        IKeyVaultSecretProvider? secretProvider = null)
    {
        var maskProvider = new SqlDataMaskingProvider(Options.Create(new GatewayOptions()), secretProvider, timeProvider: timeProvider);
        return new ClassificationAccessResolver(maskProvider, repo, timeProvider);
    }

    private static Consent BaseGrant(Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        TenantId = Tenant,
        TableIdentifier = CustomersTable,
        GranteeType = GranteeType.User,
        GranteeSid = UserSid,
        Effect = ConsentEffect.Allow,
        ValidFrom = FixedNow.AddDays(-1),
        ValidTo = FixedNow.AddDays(30)
    };

    private static ColumnClassification ColClass(string code, string scope = ClassificationScopes.Global, string? colId = null) => new()
    {
        TableColumnId = colId ?? Guid.NewGuid().ToString("D"),
        Scope = scope,
        Code = code,
        Source = "manual",
        AssignedBy = ApproverSid.Value,
        AssignedAt = FixedNow.AddDays(-2)
    };

    [Fact]
    public void UnknownOrInactiveOrMalformedClassificationCode_FailsClosedWithDeny()
    {
        var time = new AdjustableClassificationTimeProvider(FixedNow);
        var resolver = CreateResolver(timeProvider: time);
        var builtIn = ClassificationAccessResolver.BuiltInCatalog(Tenant.Value);

        var definitions = new Dictionary<string, DataClassification>(builtIn.Definitions, StringComparer.Ordinal)
        {
            ["CUSTOM_INACTIVE"] = new DataClassification
            {
                Scope = Tenant.Value,
                Code = "CUSTOM_INACTIVE",
                DisplayName = "Inactive Custom",
                RestrictionRank = 50,
                IsActive = false,
                UpdatedBy = ApproverSid.Value,
                UpdatedAt = FixedNow
            }
        };
        var catalog = builtIn with { Definitions = definitions };
        var grant = BaseGrant();

        var unknown = resolver.ResolveColumnForGrant(
            grant, "secret_col", "TEXT", ColumnAccessLevel.Clear,
            [ColClass("NOT_IN_CATALOG")], [], catalog, Tenant, TargetSqlDialect.PostgreSql);
        unknown.AccessLevel.ShouldBe(ColumnAccessLevel.Deny);
        unknown.MaskSpec.ShouldBeNull();
        unknown.CatalogText.ShouldBe(CatalogTextVisibility.Hidden);
        unknown.ReasonCode.ShouldBe("CLASSIFICATION_UNKNOWN_OR_INACTIVE");

        var inactive = resolver.ResolveColumnForGrant(
            grant, "secret_col", "TEXT", ColumnAccessLevel.Clear,
            [ColClass("CUSTOM_INACTIVE")], [], catalog, Tenant, TargetSqlDialect.PostgreSql);
        inactive.AccessLevel.ShouldBe(ColumnAccessLevel.Deny);
        inactive.ReasonCode.ShouldBe("CLASSIFICATION_UNKNOWN_OR_INACTIVE");

        var invalidFormat = resolver.ResolveColumnForGrant(
            grant, "secret_col", "TEXT", ColumnAccessLevel.Clear,
            [ColClass("lowercase_bad!")], [], catalog, Tenant, TargetSqlDialect.PostgreSql);
        invalidFormat.AccessLevel.ShouldBe(ColumnAccessLevel.Deny);
    }

    [Fact]
    public void MissingClassificationMaskRule_FallsBackToDefaultRule_OrTypeSafeRedact()
    {
        var resolver = CreateResolver(timeProvider: new AdjustableClassificationTimeProvider(FixedNow));
        var defs = new Dictionary<string, DataClassification>(StringComparer.Ordinal)
        {
            ["CUSTOM_GOV"] = new DataClassification
            {
                Scope = Tenant.Value,
                Code = "CUSTOM_GOV",
                DisplayName = "Custom Gov",
                RestrictionRank = 45,
                IsActive = true,
                UpdatedBy = ApproverSid.Value,
                UpdatedAt = FixedNow
            }
        };
        var withDefaultCatalog = new ClassificationPolicyCatalog(
            Tenant.Value,
            defs,
            new Dictionary<string, ClassificationMaskRule>(StringComparer.Ordinal)
            {
                [ClassificationScopes.DefaultMaskRuleCode] = new ClassificationMaskRule
                {
                    Scope = ClassificationScopes.Global,
                    Code = ClassificationScopes.DefaultMaskRuleCode,
                    MaskFunction = MaskFunction.Nullify,
                    CatalogText = CatalogTextVisibility.Visible,
                    UpdatedBy = ApproverSid.Value,
                    UpdatedAt = FixedNow
                }
            });

        var grant = BaseGrant();
        var withDefault = resolver.ResolveColumnForGrant(
            grant, "gov_id", "TEXT", ColumnAccessLevel.Clear,
            [ColClass("CUSTOM_GOV", Tenant.Value)], [], withDefaultCatalog, Tenant, TargetSqlDialect.PostgreSql);
        withDefault.AccessLevel.ShouldBe(ColumnAccessLevel.Mask);
        withDefault.WinningMaskFunction.ShouldBe(MaskFunction.Nullify);
        withDefault.MaskSpec.ShouldNotBeNull();
        withDefault.MaskSpec!.Kind.ShouldBe(MaskKind.Nullify);

        var withoutDefaultCatalog = new ClassificationPolicyCatalog(
            Tenant.Value,
            defs,
            new Dictionary<string, ClassificationMaskRule>(StringComparer.Ordinal));
        var noDefault = resolver.ResolveColumnForGrant(
            grant, "gov_id", "INTEGER", ColumnAccessLevel.Clear,
            [ColClass("CUSTOM_GOV", Tenant.Value)], [], withoutDefaultCatalog, Tenant, TargetSqlDialect.PostgreSql);
        noDefault.AccessLevel.ShouldBe(ColumnAccessLevel.Mask);
        noDefault.WinningMaskFunction.ShouldBe(MaskFunction.TypeSafeRedact);
        noDefault.MaskSpec!.Kind.ShouldBe(MaskKind.Redact);
    }

    [Fact]
    public void CredentialsClassification_IsNeverClear_EvenWithUnmaskGrant()
    {
        var resolver = CreateResolver(timeProvider: new AdjustableClassificationTimeProvider(FixedNow));
        var catalog = ClassificationAccessResolver.BuiltInCatalog(Tenant.Value);
        var grant = new Consent
        {
            Id = Guid.NewGuid(),
            TenantId = Tenant,
            TableIdentifier = CustomersTable,
            GranteeType = GranteeType.User,
            GranteeSid = UserSid,
            Effect = ConsentEffect.Allow,
            ValidFrom = FixedNow.AddDays(-1),
            ValidTo = FixedNow.AddDays(30),
            ColumnRules = [new ConsentColumnRule { ColumnName = "api_token", AccessLevel = ColumnAccessLevel.Clear }]
        };

        var outcome = resolver.ResolveColumnForGrant(
            grant, "api_token", "TEXT", ColumnAccessLevel.Clear,
            [ColClass(ClassificationScopes.CredentialsCode)], [], catalog, Tenant, TargetSqlDialect.PostgreSql, unmasked: true);

        outcome.AccessLevel.ShouldBe(ColumnAccessLevel.Mask);
        outcome.MaskSpec.ShouldNotBeNull();
    }

    [Fact]
    public void HierarchyInheritance_InheritsMaskFromParent_AndOnlyAnUnmaskGrantClears()
    {
        var resolver = CreateResolver(timeProvider: new AdjustableClassificationTimeProvider(FixedNow), secretProvider: new FakeClassificationSecretProvider());
        var builtIn = ClassificationAccessResolver.BuiltInCatalog(Tenant.Value);
        var rules = new Dictionary<string, ClassificationMaskRule>(builtIn.MaskRules, StringComparer.Ordinal);
        rules.Remove(ClassificationScopes.PiiSpecialCode);
        rules[ClassificationScopes.PiiCode] = new ClassificationMaskRule
        {
            Scope = ClassificationScopes.Global,
            Code = ClassificationScopes.PiiCode,
            MaskFunction = MaskFunction.Hash,
            KeyRef = "test-hmac-key",
            CatalogText = CatalogTextVisibility.Visible,
            UpdatedBy = ApproverSid.Value,
            UpdatedAt = FixedNow
        };
        var catalog = builtIn with { MaskRules = rules };

        var grant = BaseGrant();
        var outcome = resolver.ResolveColumnForGrant(
            grant, "medical_note", "TEXT", ColumnAccessLevel.Clear,
            [ColClass(ClassificationScopes.PiiSpecialCode)], [], catalog, Tenant, TargetSqlDialect.PostgreSql);

        outcome.AccessLevel.ShouldBe(ColumnAccessLevel.Mask);
        outcome.WinningMaskFunction.ShouldBe(MaskFunction.Hash);
        outcome.WinningClassificationCode.ShouldBe(ClassificationScopes.PiiSpecialCode);
        outcome.MaskSpec!.Kind.ShouldBe(MaskKind.Hmac);

        var cleared = resolver.ResolveColumnForGrant(
            grant, "medical_note", "TEXT", ColumnAccessLevel.Clear,
            [ColClass(ClassificationScopes.PiiSpecialCode)], [], catalog, Tenant, TargetSqlDialect.PostgreSql, unmasked: true);
        cleared.AccessLevel.ShouldBe(ColumnAccessLevel.Clear);
    }

    [Fact]
    public void AllElevenMaskFunctionsHaveExpectedRestrictivenessOrder_AndTieBreakUsesRankDescThenCodeAsc()
    {
        var expectedOrder = new[]
        {
            MaskFunction.Partial,
            MaskFunction.GeoJitter,
            MaskFunction.EmailDomain,
            MaskFunction.TruncateDate,
            MaskFunction.RoundNumber,
            MaskFunction.BucketNumber,
            MaskFunction.Tokenize,
            MaskFunction.Hash,
            MaskFunction.TypeSafeRedact,
            MaskFunction.Nullify
        };
        for (int i = 1; i < expectedOrder.Length; i++)
        {
            ClassificationMaskFunctions.GetRestrictivenessRank(expectedOrder[i])
                .ShouldBeGreaterThan(ClassificationMaskFunctions.GetRestrictivenessRank(expectedOrder[i - 1]));
        }

        ClassificationMaskFunctions.GetRestrictivenessRank(MaskFunction.Redact)
            .ShouldBe(ClassificationMaskFunctions.GetRestrictivenessRank(MaskFunction.TypeSafeRedact));

        var resolver = CreateResolver(timeProvider: new AdjustableClassificationTimeProvider(FixedNow));
        DataClassification MakeDef(string code, short rank) => new()
        {
            Scope = Tenant.Value,
            Code = code,
            DisplayName = code,
            RestrictionRank = rank,
            IsActive = true,
            UpdatedBy = ApproverSid.Value,
            UpdatedAt = FixedNow
        };
        ClassificationMaskRule MakeRule(string code, MaskFunction fn, string? paramsJson = null) => new()
        {
            Scope = Tenant.Value,
            Code = code,
            MaskFunction = fn,
            ParamsJson = paramsJson,
            CatalogText = CatalogTextVisibility.Visible,
            UpdatedBy = ApproverSid.Value,
            UpdatedAt = FixedNow
        };

        var catalog = ClassificationAccessResolver.BuildCatalog(
            Tenant.Value,
            [MakeDef("CLASS_A", 30), MakeDef("CLASS_B", 50), MakeDef("CLASS_C", 50)],
            [
                MakeRule("CLASS_A", MaskFunction.Redact, "{\"constant\":\"***\"}"),
                MakeRule("CLASS_B", MaskFunction.TypeSafeRedact, "{\"constant\":\"XXX\"}"),
                MakeRule("CLASS_C", MaskFunction.TypeSafeRedact, "{\"constant\":\"ZZZ\"}")
            ]);

        var grant = BaseGrant();
        var higherRankWins = resolver.ResolveColumnForGrant(
            grant, "val", "TEXT", ColumnAccessLevel.Clear,
            [ColClass("CLASS_A", Tenant.Value), ColClass("CLASS_B", Tenant.Value)],
            [], catalog, Tenant, TargetSqlDialect.PostgreSql);
        higherRankWins.WinningClassificationCode.ShouldBe("CLASS_B");

        var equalRankTieBreak = resolver.ResolveColumnForGrant(
            grant, "val", "TEXT", ColumnAccessLevel.Clear,
            [ColClass("CLASS_C", Tenant.Value), ColClass("CLASS_B", Tenant.Value)],
            [], catalog, Tenant, TargetSqlDialect.PostgreSql);
        equalRankTieBreak.WinningClassificationCode.ShouldBe("CLASS_B");
    }

    [Fact]
    public void PublicClassification_StaysClear_AndRespectsTheGrantColumnLevel()
    {
        var resolver = CreateResolver(timeProvider: new AdjustableClassificationTimeProvider(FixedNow));
        var catalog = ClassificationAccessResolver.BuiltInCatalog(Tenant.Value);
        var grant = BaseGrant();

        var defaultClear = resolver.ResolveColumnForGrant(
            grant, "display_name", "TEXT", ColumnAccessLevel.Clear,
            [ColClass(ClassificationScopes.PublicCode)], [], catalog, Tenant, TargetSqlDialect.PostgreSql);
        defaultClear.AccessLevel.ShouldBe(ColumnAccessLevel.Clear);

        var masked = resolver.ResolveColumnForGrant(
            grant, "display_name", "TEXT", ColumnAccessLevel.Mask,
            [ColClass(ClassificationScopes.PublicCode)], [], catalog, Tenant, TargetSqlDialect.PostgreSql, unmasked: true);
        masked.AccessLevel.ShouldBe(ColumnAccessLevel.Mask);

        var denied = resolver.ResolveColumnForGrant(
            grant, "display_name", "TEXT", ColumnAccessLevel.Deny,
            [ColClass(ClassificationScopes.PublicCode)], [], catalog, Tenant, TargetSqlDialect.PostgreSql, unmasked: true);
        denied.AccessLevel.ShouldBe(ColumnAccessLevel.Deny);
    }

    [Fact]
    public void ClassifiedColumn_IsMaskedWithoutUnmaskGrant_AndClearWithIt()
    {
        var resolver = CreateResolver(timeProvider: new AdjustableClassificationTimeProvider(FixedNow));
        var catalog = ClassificationAccessResolver.BuiltInCatalog(Tenant.Value);
        var grant = BaseGrant();

        var masked = resolver.ResolveColumnForGrant(
            grant, "ssn", "TEXT", ColumnAccessLevel.Clear,
            [ColClass(ClassificationScopes.PiiCode)], [], catalog, Tenant, TargetSqlDialect.PostgreSql);
        masked.AccessLevel.ShouldBe(ColumnAccessLevel.Mask);
        masked.WinningClassificationCode.ShouldBe(ClassificationScopes.PiiCode);

        var unmasked = resolver.ResolveColumnForGrant(
            grant, "ssn", "TEXT", ColumnAccessLevel.Clear,
            [ColClass(ClassificationScopes.PiiCode)], [], catalog, Tenant, TargetSqlDialect.PostgreSql, unmasked: true);
        unmasked.AccessLevel.ShouldBe(ColumnAccessLevel.Clear);
    }

    [Fact]
    public void UnmaskGrant_NeverWidensAMaskedGrantColumn()
    {
        var resolver = CreateResolver(timeProvider: new AdjustableClassificationTimeProvider(FixedNow));
        var catalog = ClassificationAccessResolver.BuiltInCatalog(Tenant.Value);

        var outcome = resolver.ResolveColumnForGrant(
            BaseGrant(), "ssn", "TEXT", ColumnAccessLevel.Mask,
            [ColClass(ClassificationScopes.PiiCode)], [], catalog, Tenant, TargetSqlDialect.PostgreSql, unmasked: true);

        outcome.AccessLevel.ShouldBe(ColumnAccessLevel.Mask);
    }

    [Fact]
    public void Od40_NewMaskingClassification_TightensExistingGrantToMask()
    {
        var resolver = CreateResolver(timeProvider: new AdjustableClassificationTimeProvider(FixedNow));
        var catalog = ClassificationAccessResolver.BuiltInCatalog(Tenant.Value);
        var grant = BaseGrant();

        var initial = resolver.ResolveColumnForGrant(
            grant, "iban", "TEXT", ColumnAccessLevel.Clear,
            [ColClass(ClassificationScopes.PublicCode)], [], catalog, Tenant, TargetSqlDialect.PostgreSql);
        initial.AccessLevel.ShouldBe(ColumnAccessLevel.Clear);

        var tightened = resolver.ResolveColumnForGrant(
            grant, "iban", "TEXT", ColumnAccessLevel.Clear,
            [ColClass(ClassificationScopes.PublicCode), ColClass(ClassificationScopes.FinancialCode)],
            [], catalog, Tenant, TargetSqlDialect.PostgreSql);
        tightened.AccessLevel.ShouldBe(ColumnAccessLevel.Mask);
        tightened.WinningClassificationCode.ShouldBe(ClassificationScopes.FinancialCode);
    }

    [Fact]
    public void ColumnMaskOverrides_HonorPrecedence_AndIsolateKeyedVsUnkeyedScopes()
    {
        var resolver = CreateResolver(timeProvider: new AdjustableClassificationTimeProvider(FixedNow), secretProvider: new FakeClassificationSecretProvider());
        var builtIn = ClassificationAccessResolver.BuiltInCatalog(Tenant.Value);
        var rules = new Dictionary<string, ClassificationMaskRule>(builtIn.MaskRules, StringComparer.Ordinal)
        {
            [ClassificationScopes.PiiCode] = new ClassificationMaskRule
            {
                Scope = ClassificationScopes.Global,
                Code = ClassificationScopes.PiiCode,
                MaskFunction = MaskFunction.Partial,
                ParamsJson = "{\"prefix_chars\":1,\"suffix_chars\":1,\"mask_char\":\"*\"}",
                UpdatedBy = ApproverSid.Value,
                UpdatedAt = FixedNow
            },
            [ClassificationScopes.FinancialCode] = new ClassificationMaskRule
            {
                Scope = ClassificationScopes.Global,
                Code = ClassificationScopes.FinancialCode,
                MaskFunction = MaskFunction.TypeSafeRedact,
                UpdatedBy = ApproverSid.Value,
                UpdatedAt = FixedNow
            }
        };
        var catalog = builtIn with { MaskRules = rules };

        var colId = Guid.NewGuid().ToString("D");
        ColumnMaskOverride Ov(string scope, string? code, MaskFunction fn, string? keyRef = null) => new()
        {
            TableColumnId = colId,
            Scope = scope,
            Code = code,
            MaskFunction = fn,
            KeyRef = keyRef,
            CatalogText = CatalogTextVisibility.Visible,
            UpdatedBy = ApproverSid.Value,
            UpdatedAt = FixedNow
        };

        ColumnMaskOverride[] overrides =
        [
            Ov(ClassificationScopes.Global, null, MaskFunction.RoundNumber),
            Ov(Tenant.Value, null, MaskFunction.Hash, "test-hmac-key"),
            Ov(ClassificationScopes.Global, ClassificationScopes.PiiCode, MaskFunction.Tokenize, "test-hmac-key"),
            Ov(Tenant.Value, ClassificationScopes.PiiCode, MaskFunction.Nullify)
        ];
        var grant = BaseGrant();
        var keyedTenantWins = resolver.ResolveColumnForGrant(
            grant, "card", "TEXT", ColumnAccessLevel.Clear,
            [ColClass(ClassificationScopes.PiiCode, colId: colId)], overrides, catalog, Tenant, TargetSqlDialect.PostgreSql);
        keyedTenantWins.WinningMaskFunction.ShouldBe(MaskFunction.Nullify);

        ColumnMaskOverride[] piiOnlyKeyed = [Ov(Tenant.Value, ClassificationScopes.PiiCode, MaskFunction.Partial)];
        var finOutcome = resolver.ResolveColumnForGrant(
            grant, "balance", "TEXT", ColumnAccessLevel.Clear,
            [ColClass(ClassificationScopes.FinancialCode, colId: colId)], piiOnlyKeyed, catalog, Tenant, TargetSqlDialect.PostgreSql);
        finOutcome.WinningMaskFunction.ShouldBe(MaskFunction.TypeSafeRedact);

        ColumnMaskOverride[] unkeyedOnly =
        [
            Ov(ClassificationScopes.Global, null, MaskFunction.Partial),
            Ov(Tenant.Value, null, MaskFunction.Hash, "test-hmac-key")
        ];
        var unkeyedTenantWins = resolver.ResolveColumnForGrant(
            grant, "email", "TEXT", ColumnAccessLevel.Clear,
            [ColClass(ClassificationScopes.PiiCode, colId: colId)], unkeyedOnly, catalog, Tenant, TargetSqlDialect.PostgreSql);
        unkeyedTenantWins.WinningMaskFunction.ShouldBe(MaskFunction.Hash);
    }

    [Fact]
    public void TokenizeWithoutKeyVaultKey_FailsClosedWithMaskKeyUnavailable()
    {
        var resolver = CreateResolver(timeProvider: new AdjustableClassificationTimeProvider(FixedNow), secretProvider: null);
        var builtIn = ClassificationAccessResolver.BuiltInCatalog(Tenant.Value);
        var rules = new Dictionary<string, ClassificationMaskRule>(builtIn.MaskRules, StringComparer.Ordinal)
        {
            [ClassificationScopes.PiiCode] = new ClassificationMaskRule
            {
                Scope = ClassificationScopes.Global,
                Code = ClassificationScopes.PiiCode,
                MaskFunction = MaskFunction.Tokenize,
                KeyRef = "missing-hmac-key",
                UpdatedBy = ApproverSid.Value,
                UpdatedAt = FixedNow
            }
        };
        var catalog = builtIn with { MaskRules = rules };

        var outcome = resolver.ResolveColumnForGrant(
            BaseGrant(), "ssn", "TEXT", ColumnAccessLevel.Clear,
            [ColClass(ClassificationScopes.PiiCode)], [], catalog, Tenant, TargetSqlDialect.PostgreSql);

        outcome.AccessLevel.ShouldBe(ColumnAccessLevel.Deny);
        outcome.ReasonCode.ShouldBe("MASK_KEY_UNAVAILABLE");
        outcome.MaskSpec.ShouldBeNull();
    }

    [Fact]
    public async Task TableAccessPolicy_And_AccessDecisionPoint_EnforceClassificationMasksAndUnmaskEndToEnd()
    {
        var time = new AdjustableClassificationTimeProvider(FixedNow);
        var repo = new TestInMemoryClassificationStore(Tenant.Value);
        var secretProvider = new FakeClassificationSecretProvider();
        var resolver = CreateResolver(repo, time, secretProvider);

        var colId = Guid.NewGuid();
        var colEmail = Guid.NewGuid();
        var colKey = Guid.NewGuid();
        var meta = new TableMetadata
        {
            Identifier = CustomersTable,
            Table = new Table { SourceName = "crm", SchemaName = "public", TableName = "customers", SourceType = "PostgreSql", IsActive = true },
            Columns =
            [
                new TableColumn { Id = colId, ColumnName = "ssn", DataType = "TEXT" },
                new TableColumn { Id = colEmail, ColumnName = "email", DataType = "TEXT" },
                new TableColumn { Id = colKey, ColumnName = "api_key", DataType = "TEXT" }
            ],
            ColumnClassifications = new Dictionary<string, IReadOnlyList<ColumnClassification>>(StringComparer.OrdinalIgnoreCase)
            {
                ["ssn"] = [ColClass(ClassificationScopes.PiiCode, colId: colId.ToString("D"))],
                ["email"] = [ColClass(ClassificationScopes.PublicCode, colId: colEmail.ToString("D"))],
                ["api_key"] = [ColClass(ClassificationScopes.CredentialsCode, colId: colKey.ToString("D"))]
            }
        };

        var grant = BaseGrant();
        var consentRepo = new TestInMemoryConsentRepository([grant]);
        var cache = new TestMemoryConsentCache();
        var pdp = CreatePdp(resolver, consentRepo, cache, time);

        repo.MaskRules[ClassificationScopes.PiiCode] = new ClassificationMaskRule
        {
            Scope = Tenant.Value,
            Code = ClassificationScopes.PiiCode,
            MaskFunction = MaskFunction.Hash,
            KeyRef = "test-hmac-key",
            CatalogText = CatalogTextVisibility.Visible,
            UpdatedBy = ApproverSid.Value,
            UpdatedAt = FixedNow
        };

        var principal = PipelinePrincipals.MarkComplete(new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, UserSid.Value),
            new Claim("tenant_id", Tenant.Value)
        ], "Bearer")));

        var req = new AccessRequest(
            SecurityPrincipalContext.FromPrincipal(principal, Tenant, "Bearer"),
            meta, AccessOperation.Read, AccessTransport.Rest);

        var d1 = await pdp.DecideAsync(req);
        d1.IsAllowed.ShouldBeTrue(string.Join("; ", d1.DeniedReasons));
        d1.GetColumnAccess("email").ShouldBe(ColumnAccessLevel.Clear);
        d1.GetColumnAccess("ssn").ShouldBe(ColumnAccessLevel.Mask);
        d1.GetColumnAccess("api_key").ShouldBe(ColumnAccessLevel.Mask);
        ((ColumnMaskPolicy)d1.ColumnMasks!).Specs["ssn"].Kind.ShouldBe(MaskKind.Hmac);

        // #1009 A-3: clear values come from a per-object unmask grant, never from a classification grant; credentials stay masked.
        var unmaskPdp = CreatePdp(resolver, consentRepo, cache, time, new WholeTableUnmask());
        await cache.ClearL1CacheAsync();
        var d2 = await unmaskPdp.DecideAsync(req);
        d2.GetColumnAccess("ssn").ShouldBe(ColumnAccessLevel.Clear);
        d2.GetColumnAccess("email").ShouldBe(ColumnAccessLevel.Clear);
        d2.GetColumnAccess("api_key").ShouldBe(ColumnAccessLevel.Mask);
    }

    [Fact]
    public async Task LoadCatalogAsync_ReflectsUpdatedClassificationMaskRuleAfterVersionBump()
    {
        var time = new AdjustableClassificationTimeProvider(FixedNow);
        var repo = new TestInMemoryClassificationStore(Tenant.Value);
        var resolver = CreateResolver(repo, time);

        var v1 = await resolver.LoadCatalogAsync(Tenant.Value);
        v1.MaskRules[ClassificationScopes.PiiCode].MaskFunction.ShouldBe(MaskFunction.TypeSafeRedact);

        await repo.UpsertClassificationMaskRuleAsync(new ClassificationMaskRule
        {
            Scope = Tenant.Value,
            Code = ClassificationScopes.PiiCode,
            MaskFunction = MaskFunction.Nullify,
            UpdatedBy = ApproverSid.Value,
            UpdatedAt = FixedNow
        });

        var v2 = await resolver.LoadCatalogAsync(Tenant.Value);
        v2.MaskRules[ClassificationScopes.PiiCode].MaskFunction.ShouldBe(MaskFunction.Nullify);
    }

    private static AccessDecisionPoint CreatePdp(
        ClassificationAccessResolver resolver,
        IConsentRepository consentRepo,
        IConsentCacheService cacheService,
        TimeProvider timeProvider,
        Autheris.Application.Policy.Protection.IUnmaskGrantResolver? unmask = null)
    {
        var masks = new ColumnMaskPolicyFactory(new SqlDataMaskingProvider(Options.Create(new GatewayOptions()), timeProvider: timeProvider));
        var rows = new RowPolicyFactory(new PolicyPredicateRenderer());
        var typed = new TypedPolicySources(rows, masks, ClassificationResolver: resolver);
        var res = new ConsentResolutionService(logger: NullLogger<ConsentResolutionService>.Instance);
        return new AccessDecisionPoint(
            Options.Create(new GatewayOptions()),
            resolutionService: res,
            consentRepository: consentRepo,
            cacheService: cacheService,
            mandatoryFilters: new NoOpMandatoryRowFilterResolver(),
            typedPolicies: typed,
            timeProvider: timeProvider,
            unmaskResolver: unmask);
    }

    private sealed class WholeTableUnmask : Autheris.Application.Policy.Protection.IUnmaskGrantResolver
    {
        public ValueTask<Autheris.Application.Policy.Protection.UnmaskCoverage> ResolveAsync(
            TenantId tenant, IReadOnlyCollection<Sid> subjects, bool groupsComplete, TableIdentifier table, CancellationToken ct = default) =>
            ValueTask.FromResult(new Autheris.Application.Policy.Protection.UnmaskCoverage(wholeTable: true, new HashSet<string>()));
    }
}
