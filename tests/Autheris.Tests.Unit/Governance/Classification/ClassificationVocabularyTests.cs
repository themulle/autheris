using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance.Classification;

[Trait("Category", "Security")]
public sealed class ClassificationVocabularyTests
{
    private static readonly DateTimeOffset FixedTimestamp = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Star_scope_not_a_valid_tenant()
    {
        Should.Throw<ArgumentException>(() => new TenantId("*"));
        ClassificationScopes.IsValidTenantScope("*").ShouldBeFalse();
        ClassificationScopes.IsValidTenantScope("_platform").ShouldBeFalse();
        ClassificationScopes.IsValidTenantScope("tenant-a").ShouldBeTrue();

        Should.Throw<ArgumentException>(() => ClassificationScopes.ValidateScope("*", allowGlobal: false));
        ClassificationScopes.ValidateScope("*", allowGlobal: true).ShouldBe("*");

        var builtins = CreateBuiltins();
        Should.Throw<ArgumentException>(() => ClassificationHierarchyValidator.ResolveEffective(builtins, "*"));
    }

    [Fact]
    public void Child_rank_below_parent_rejected()
    {
        var effective = CreateBuiltins().ToDictionary(c => c.Code, StringComparer.Ordinal);
        var invalidChild = new DataClassification
        {
            Scope = "tenant-a",
            Code = "INTERNAL_CONTACT",
            DisplayName = "Internal Contact",
            ParentCode = "PII",
            RestrictionRank = 35,
            UpdatedBy = "admin",
            UpdatedAt = FixedTimestamp
        };

        Should.Throw<ArgumentException>(() =>
            ClassificationHierarchyValidator.ValidateDefinition(invalidChild, effective));
    }

    [Fact]
    public void Tenant_row_overrides_builtin_approver()
    {
        var rows = CreateBuiltins().ToList();
        rows.Add(new DataClassification
        {
            Scope = "acme",
            Code = "PII",
            DisplayName = "Personally Identifiable Information",
            RestrictionRank = 40,
            ClearGrantable = true,
            SeparateApproval = true,
            ApproverMode = ApproverMode.Steward,
            StewardGroupSid = "S-1-5-21-acme-stewards",
            MaxClearDays = 14,
            UpdatedBy = "acme-admin",
            UpdatedAt = FixedTimestamp
        });

        var acmeEffective = ClassificationHierarchyValidator.ResolveEffective(rows, "acme");
        acmeEffective["PII"].ApproverMode.ShouldBe(ApproverMode.Steward);
        acmeEffective["PII"].StewardGroupSid.ShouldBe("S-1-5-21-acme-stewards");
        acmeEffective["PII"].MaxClearDays.ShouldBe(14);
        acmeEffective["PII"].Scope.ShouldBe("acme");

        var otherEffective = ClassificationHierarchyValidator.ResolveEffective(rows, "other-tenant");
        otherEffective["PII"].ApproverMode.ShouldBe(ApproverMode.DataOwner);
        otherEffective["PII"].StewardGroupSid.ShouldBeNull();
        otherEffective["PII"].Scope.ShouldBe("*");
    }

    [Fact]
    public void Cycle_in_hierarchy_rejected()
    {
        var baseMap = CreateBuiltins().ToDictionary(c => c.Code, StringComparer.Ordinal);
        var classA = new DataClassification
        {
            Scope = "tenant-a",
            Code = "CLASS_A",
            DisplayName = "Class A",
            RestrictionRank = 50,
            UpdatedBy = "admin",
            UpdatedAt = FixedTimestamp
        };
        var classB = new DataClassification
        {
            Scope = "tenant-a",
            Code = "CLASS_B",
            DisplayName = "Class B",
            ParentCode = "CLASS_A",
            RestrictionRank = 60,
            UpdatedBy = "admin",
            UpdatedAt = FixedTimestamp
        };
        baseMap[classA.Code] = classA;
        baseMap[classB.Code] = classB;

        var selfCycle = classA with { ParentCode = "CLASS_A" };
        Should.Throw<ArgumentException>(() =>
            ClassificationHierarchyValidator.ValidateDefinition(selfCycle, baseMap));

        var twoNodeCycle = classA with { ParentCode = "CLASS_B" };
        Should.Throw<ArgumentException>(() =>
            ClassificationHierarchyValidator.ValidateDefinition(twoNodeCycle, baseMap));
    }

    [Fact]
    public void Credentials_cannot_be_clear_grantable()
    {
        var effective = CreateBuiltins().ToDictionary(c => c.Code, StringComparer.Ordinal);
        var clearCredentials = effective["CREDENTIALS"] with { ClearGrantable = true };

        Should.Throw<ArgumentException>(() =>
            ClassificationHierarchyValidator.ValidateDefinition(clearCredentials, effective));

        var validCredentials = effective["CREDENTIALS"] with { Scope = "tenant-a", UpdatedAt = FixedTimestamp };
        Should.NotThrow(() =>
            ClassificationHierarchyValidator.ValidateDefinition(validCredentials, effective));
    }

    [Theory]
    [InlineData("pii", false)]
    [InlineData("A", false)]
    [InlineData("PII-SPECIAL", false)]
    [InlineData("_DEFAULT", false)]
    [InlineData("PII_SPECIAL", true)]
    public void Code_format_enforced(string code, bool expectedValid)
    {
        ClassificationHierarchyValidator.IsValidClassificationCode(code).ShouldBe(expectedValid);
    }

    [Fact]
    public void MaskFunction_restrictiveness_order_and_codes_roundtrip()
    {
        ClassificationHierarchyValidator.IsValidMaskRuleCode("_DEFAULT").ShouldBeTrue();

        foreach (var fn in Enum.GetValues<MaskFunction>())
        {
            var code = ClassificationMaskFunctions.ToCode(fn);
            ClassificationMaskFunctions.TryParse(code, out var parsed).ShouldBeTrue();
            parsed.ShouldBe(fn);
        }

        ClassificationMaskFunctions.GetRestrictivenessRank(MaskFunction.Nullify)
            .ShouldBeGreaterThan(ClassificationMaskFunctions.GetRestrictivenessRank(MaskFunction.Redact));
        ClassificationMaskFunctions.GetRestrictivenessRank(MaskFunction.Redact)
            .ShouldBe(ClassificationMaskFunctions.GetRestrictivenessRank(MaskFunction.TypeSafeRedact));
        ClassificationMaskFunctions.GetRestrictivenessRank(MaskFunction.TypeSafeRedact)
            .ShouldBeGreaterThan(ClassificationMaskFunctions.GetRestrictivenessRank(MaskFunction.Hash));
        ClassificationMaskFunctions.GetRestrictivenessRank(MaskFunction.Hash)
            .ShouldBeGreaterThan(ClassificationMaskFunctions.GetRestrictivenessRank(MaskFunction.Tokenize));
        ClassificationMaskFunctions.GetRestrictivenessRank(MaskFunction.Tokenize)
            .ShouldBeGreaterThan(ClassificationMaskFunctions.GetRestrictivenessRank(MaskFunction.BucketNumber));
        ClassificationMaskFunctions.GetRestrictivenessRank(MaskFunction.BucketNumber)
            .ShouldBeGreaterThan(ClassificationMaskFunctions.GetRestrictivenessRank(MaskFunction.RoundNumber));
        ClassificationMaskFunctions.GetRestrictivenessRank(MaskFunction.RoundNumber)
            .ShouldBeGreaterThan(ClassificationMaskFunctions.GetRestrictivenessRank(MaskFunction.TruncateDate));
        ClassificationMaskFunctions.GetRestrictivenessRank(MaskFunction.TruncateDate)
            .ShouldBeGreaterThan(ClassificationMaskFunctions.GetRestrictivenessRank(MaskFunction.EmailDomain));
        ClassificationMaskFunctions.GetRestrictivenessRank(MaskFunction.EmailDomain)
            .ShouldBeGreaterThan(ClassificationMaskFunctions.GetRestrictivenessRank(MaskFunction.GeoJitter));
        ClassificationMaskFunctions.GetRestrictivenessRank(MaskFunction.GeoJitter)
            .ShouldBeGreaterThan(ClassificationMaskFunctions.GetRestrictivenessRank(MaskFunction.Partial));
    }

    private static IReadOnlyList<DataClassification> CreateBuiltins() =>
    [
        new DataClassification
        {
            Scope = "*",
            Code = "PUBLIC",
            DisplayName = "Public",
            RestrictionRank = 0,
            ClearGrantable = true,
            SeparateApproval = false,
            ApproverMode = ApproverMode.DataOwner,
            UpdatedBy = "MIGRATION",
            UpdatedAt = FixedTimestamp
        },
        new DataClassification
        {
            Scope = "*",
            Code = "CONFIDENTIAL",
            DisplayName = "Confidential",
            RestrictionRank = 20,
            ClearGrantable = true,
            SeparateApproval = true,
            ApproverMode = ApproverMode.DataOwner,
            UpdatedBy = "MIGRATION",
            UpdatedAt = FixedTimestamp
        },
        new DataClassification
        {
            Scope = "*",
            Code = "FINANCIAL",
            DisplayName = "Financial",
            RestrictionRank = 30,
            ClearGrantable = true,
            SeparateApproval = true,
            ApproverMode = ApproverMode.DataOwner,
            UpdatedBy = "MIGRATION",
            UpdatedAt = FixedTimestamp
        },
        new DataClassification
        {
            Scope = "*",
            Code = "PII",
            DisplayName = "Personally Identifiable Information",
            RestrictionRank = 40,
            ClearGrantable = true,
            SeparateApproval = true,
            ApproverMode = ApproverMode.DataOwner,
            UpdatedBy = "MIGRATION",
            UpdatedAt = FixedTimestamp
        },
        new DataClassification
        {
            Scope = "*",
            Code = "PII_SPECIAL",
            DisplayName = "Special Category Personal Data",
            ParentCode = "PII",
            RestrictionRank = 60,
            ClearGrantable = true,
            SeparateApproval = true,
            ApproverMode = ApproverMode.DataOwnerAndSteward,
            StewardGroupSid = "STEWARD_UNCONFIGURED",
            UpdatedBy = "MIGRATION",
            UpdatedAt = FixedTimestamp
        },
        new DataClassification
        {
            Scope = "*",
            Code = "CREDENTIALS",
            DisplayName = "Credentials",
            RestrictionRank = 90,
            ClearGrantable = false,
            SeparateApproval = true,
            ApproverMode = ApproverMode.DataOwner,
            UpdatedBy = "MIGRATION",
            UpdatedAt = FixedTimestamp
        }
    ];
}
