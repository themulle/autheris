using Autheris.Domain.Model;
using Autheris.Domain.Security;

namespace Autheris.Application.Governance.Protection;

public sealed record ProtectionWriteResult(bool IsValid, string? ErrorCode = null, string? ErrorMessage = null, ObjectProtection? NormalizedProtection = null);

public sealed record ProtectionTableChange(
    bool IsValid,
    string? ErrorCode,
    string? ErrorMessage,
    ObjectProtection? NormalizedTableProtection,
    IReadOnlyList<string> ClearedRedundantColumns,
    bool IsLooserThanContainer);

/// <summary>Pure write-time validator for table and column protection classifications and mask categories.</summary>
public static class ProtectionWriteValidator
{
    public const string ColumnLooserErrorCode = "PROTECTION_CLASS_COLUMN_LOOSER";
    public const string InvalidMaskCategoryErrorCode = "MASK_CATEGORY_INVALID";
    public const int MaxMaskCategoryLength = 64;

    public static ProtectionWriteResult ValidateMaskCategory(string? maskCategory)
    {
        if (string.IsNullOrWhiteSpace(maskCategory))
        {
            return new ProtectionWriteResult(true, NormalizedProtection: new ObjectProtection(null, null));
        }

        if (maskCategory.Any(char.IsControl))
        {
            return FailMask("Mask category must not contain control characters.");
        }

        var trimmed = maskCategory.Trim();
        if (trimmed.Length > MaxMaskCategoryLength)
        {
            return FailMask($"Mask category must not exceed {MaxMaskCategoryLength} characters.");
        }

        if (string.Equals(trimmed, "PUBLIC", StringComparison.OrdinalIgnoreCase))
        {
            return FailMask("Mask category must not be 'PUBLIC'.");
        }

        return new ProtectionWriteResult(true, NormalizedProtection: new ObjectProtection(null, trimmed));
    }

    public static ProtectionWriteResult ValidateColumn(ProtectionClass declaredTableClass, ProtectionClass? requestedClass, string? requestedMaskCategory = null)
    {
        var maskResult = ValidateMaskCategory(requestedMaskCategory);
        if (!maskResult.IsValid)
        {
            return maskResult;
        }

        var normalizedTable = declaredTableClass.NormalizeOrConfidential();
        var normalizedRequested = requestedClass?.NormalizeOrConfidential();
        if (normalizedRequested is { } c && c < normalizedTable)
        {
            return new ProtectionWriteResult(
                false,
                ColumnLooserErrorCode,
                $"Column protection class '{c.ToCanonicalName()}' cannot be looser than declared table protection class '{normalizedTable.ToCanonicalName()}'.");
        }

        return new ProtectionWriteResult(true, NormalizedProtection: new ObjectProtection(normalizedRequested, maskResult.NormalizedProtection?.MaskCategory));
    }

    public static ProtectionTableChange ApplyTableChange(
        ProtectionClass? containerDefault,
        ProtectionClass? requestedTableClass,
        string? requestedMaskCategory,
        IReadOnlyDictionary<string, ObjectProtection>? existingColumnOverrides)
    {
        var maskResult = ValidateMaskCategory(requestedMaskCategory);
        if (!maskResult.IsValid)
        {
            return new ProtectionTableChange(false, maskResult.ErrorCode, maskResult.ErrorMessage, null, [], false);
        }

        var effectiveContainerDefault = containerDefault?.NormalizeOrConfidential() ?? ProtectionClass.Confidential;
        var normalizedRequestedTable = requestedTableClass?.NormalizeOrConfidential();
        var newDeclaredTable = normalizedRequestedTable ?? effectiveContainerDefault;
        var isLooser = newDeclaredTable < effectiveContainerDefault;
        var cleared = CollectRedundantColumns(existingColumnOverrides, newDeclaredTable);

        return new ProtectionTableChange(true, null, null, new ObjectProtection(normalizedRequestedTable, maskResult.NormalizedProtection?.MaskCategory), cleared, isLooser);
    }

    private static ProtectionWriteResult FailMask(string message) =>
        new(false, InvalidMaskCategoryErrorCode, message);

    private static IReadOnlyList<string> CollectRedundantColumns(IReadOnlyDictionary<string, ObjectProtection>? existingColumnOverrides, ProtectionClass newDeclaredTable)
    {
        var normalizedOverrides = EffectiveProtectionResolver.BuildCaseInsensitiveOverrides(existingColumnOverrides);
        if (normalizedOverrides is null)
        {
            return [];
        }

        var cleared = new List<string>();
        foreach (var (colName, colOverride) in normalizedOverrides)
        {
            if (colOverride.Class is { } colClass && colClass.NormalizeOrConfidential() <= newDeclaredTable)
            {
                cleared.Add(colName);
            }
        }

        cleared.Sort(StringComparer.Ordinal);
        return cleared.AsReadOnly();
    }
}
