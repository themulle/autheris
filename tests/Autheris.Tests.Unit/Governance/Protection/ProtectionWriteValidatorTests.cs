using Autheris.Application.Governance.Protection;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Shouldly;

namespace Autheris.Tests.Unit.Governance.Protection;

[Trait("Category", "Security")]
public sealed class ProtectionWriteValidatorTests
{
    [Theory]
    [InlineData(ProtectionClass.Confidential, ProtectionClass.Internal)]
    [InlineData(ProtectionClass.Confidential, ProtectionClass.Public)]
    [InlineData(ProtectionClass.Internal, ProtectionClass.Public)]
    [InlineData(ProtectionClass.Secret, ProtectionClass.Confidential)]
    [InlineData(ProtectionClass.Secret, (ProtectionClass)99)]
    public void Looser_column_rejected(ProtectionClass declaredTableClass, ProtectionClass requestedColumnClass)
    {
        var result = ProtectionWriteValidator.ValidateColumn(declaredTableClass, requestedColumnClass);

        result.IsValid.ShouldBeFalse();
        result.ErrorCode.ShouldBe(ProtectionWriteValidator.ColumnLooserErrorCode);
        result.ErrorMessage.ShouldNotBeNullOrWhiteSpace();
        result.NormalizedProtection.ShouldBeNull();
    }

    [Theory]
    [InlineData("PUBLIC")]
    [InlineData("public")]
    [InlineData("  Public  ")]
    [InlineData("pUbLiC")]
    public void Public_mask_category_rejected(string maskCategory)
    {
        var maskResult = ProtectionWriteValidator.ValidateMaskCategory(maskCategory);
        maskResult.IsValid.ShouldBeFalse();
        maskResult.ErrorCode.ShouldBe(ProtectionWriteValidator.InvalidMaskCategoryErrorCode);

        var colResult = ProtectionWriteValidator.ValidateColumn(
            ProtectionClass.Confidential,
            ProtectionClass.Confidential,
            maskCategory);
        colResult.IsValid.ShouldBeFalse();
        colResult.ErrorCode.ShouldBe(ProtectionWriteValidator.InvalidMaskCategoryErrorCode);

        var tableChange = ProtectionWriteValidator.ApplyTableChange(
            ProtectionClass.Confidential,
            ProtectionClass.Confidential,
            maskCategory,
            existingColumnOverrides: null);
        tableChange.IsValid.ShouldBeFalse();
        tableChange.ErrorCode.ShouldBe(ProtectionWriteValidator.InvalidMaskCategoryErrorCode);
        tableChange.ClearedRedundantColumns.ShouldBeEmpty();
    }

    [Fact]
    public void ValidateColumn_accepts_equal_stricter_or_cleared_class()
    {
        var equal = ProtectionWriteValidator.ValidateColumn(ProtectionClass.Confidential, ProtectionClass.Confidential, "  PII_EMAIL  ");
        equal.IsValid.ShouldBeTrue();
        equal.NormalizedProtection.ShouldBe(new ObjectProtection(ProtectionClass.Confidential, "PII_EMAIL"));

        var stricter = ProtectionWriteValidator.ValidateColumn(ProtectionClass.Internal, ProtectionClass.Secret);
        stricter.IsValid.ShouldBeTrue();
        stricter.NormalizedProtection.ShouldBe(new ObjectProtection(ProtectionClass.Secret, null));

        var cleared = ProtectionWriteValidator.ValidateColumn(ProtectionClass.Secret, null, "PII_SSN");
        cleared.IsValid.ShouldBeTrue();
        cleared.NormalizedProtection.ShouldBe(new ObjectProtection(null, "PII_SSN"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void ValidateMaskCategory_allows_null_or_whitespace_as_cleared(string? input)
    {
        var result = ProtectionWriteValidator.ValidateMaskCategory(input);

        result.IsValid.ShouldBeTrue();
        result.ErrorCode.ShouldBeNull();
        result.NormalizedProtection.ShouldBe(new ObjectProtection(null, null));
    }

    [Fact]
    public void ValidateMaskCategory_enforces_length_bounds_and_rejects_control_characters()
    {
        var exactMax = new string('A', ProtectionWriteValidator.MaxMaskCategoryLength);
        var exactWithPadding = "  " + exactMax + "  ";
        var tooLong = new string('A', ProtectionWriteValidator.MaxMaskCategoryLength + 1);

        var valid = ProtectionWriteValidator.ValidateMaskCategory(exactWithPadding);
        valid.IsValid.ShouldBeTrue();
        valid.NormalizedProtection.ShouldNotBeNull();
        valid.NormalizedProtection.MaskCategory.ShouldBe(exactMax);

        var tooLongResult = ProtectionWriteValidator.ValidateMaskCategory(tooLong);
        tooLongResult.IsValid.ShouldBeFalse();
        tooLongResult.ErrorCode.ShouldBe(ProtectionWriteValidator.InvalidMaskCategoryErrorCode);

        foreach (var withControl in new[] { "PII\0", "PII\n", "PII\r", "P\tII", "PII\u0007" })
        {
            var ctrlResult = ProtectionWriteValidator.ValidateMaskCategory(withControl);
            ctrlResult.IsValid.ShouldBeFalse();
            ctrlResult.ErrorCode.ShouldBe(ProtectionWriteValidator.InvalidMaskCategoryErrorCode);
        }
    }

    [Fact]
    public void ApplyTableChange_reports_IsLooserThanContainer_while_valid()
    {
        var looser = ProtectionWriteValidator.ApplyTableChange(
            ProtectionClass.Confidential,
            ProtectionClass.Internal,
            "  PII_PARTIAL  ",
            existingColumnOverrides: null);

        looser.IsValid.ShouldBeTrue();
        looser.ErrorCode.ShouldBeNull();
        looser.IsLooserThanContainer.ShouldBeTrue();
        looser.NormalizedTableProtection.ShouldBe(new ObjectProtection(ProtectionClass.Internal, "PII_PARTIAL"));
        looser.ClearedRedundantColumns.ShouldBeEmpty();

        var notLooser = ProtectionWriteValidator.ApplyTableChange(
            ProtectionClass.Internal,
            ProtectionClass.Confidential,
            null,
            existingColumnOverrides: null);

        notLooser.IsValid.ShouldBeTrue();
        notLooser.IsLooserThanContainer.ShouldBeFalse();
        notLooser.NormalizedTableProtection.ShouldBe(new ObjectProtection(ProtectionClass.Confidential, null));
    }

    [Fact]
    public void ApplyTableChange_clears_redundant_column_overrides_and_preserves_stricter_ones()
    {
        var existing = new Dictionary<string, ObjectProtection>(StringComparer.OrdinalIgnoreCase)
        {
            ["col_public"] = new(ProtectionClass.Public),
            ["col_internal"] = new(ProtectionClass.Internal),
            ["col_conf"] = new(ProtectionClass.Confidential, "PII_EMAIL"),
            ["col_secret"] = new(ProtectionClass.Secret, "PII_SSN"),
            ["col_mask_only"] = new(null, "PII_PHONE")
        };

        var result = ProtectionWriteValidator.ApplyTableChange(
            ProtectionClass.Internal,
            ProtectionClass.Confidential,
            null,
            existing);

        result.IsValid.ShouldBeTrue();
        result.IsLooserThanContainer.ShouldBeFalse();
        result.ClearedRedundantColumns.ShouldBe(["col_conf", "col_internal", "col_public"]);
    }

    [Fact]
    public void ApplyTableChange_case_colliding_column_overrides_preserve_strictest_and_deduplicate()
    {
        var caseSensitiveExisting = new Dictionary<string, ObjectProtection>(StringComparer.Ordinal)
        {
            ["ssn"] = new(ProtectionClass.Internal),
            ["SSN"] = new(ProtectionClass.Secret),
            ["email"] = new(ProtectionClass.Public),
            ["EMAIL"] = new(ProtectionClass.Internal)
        };

        var result = ProtectionWriteValidator.ApplyTableChange(
            ProtectionClass.Internal,
            ProtectionClass.Confidential,
            null,
            caseSensitiveExisting);

        result.IsValid.ShouldBeTrue();
        result.ClearedRedundantColumns.ShouldBe(["email"]);
    }
}
