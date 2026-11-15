namespace Autheris.Domain.Model;

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Autheris.Domain.Common;

/// <summary>Supported typed mask functions persisted by <c>classification_mask_rules</c> and <c>column_mask_overrides</c>.</summary>
public enum MaskFunction
{
    Nullify,
    Redact,
    TypeSafeRedact,
    Partial,
    Hash,
    Tokenize,
    TruncateDate,
    BucketNumber,
    RoundNumber,
    EmailDomain,
    GeoJitter
}

/// <summary>Approval workflow mode required to grant clear access for a classification.</summary>
public enum ApproverMode
{
    DataOwner,
    Steward,
    DataOwnerAndSteward
}

/// <summary>Catalog metadata visibility when a column is masked under a rule or override.</summary>
public enum CatalogTextVisibility
{
    Visible,
    Hidden
}

/// <summary>Scope and built-in code constants for tenant-scoped classifications.</summary>
public static class ClassificationScopes
{
    public const string Global = "*";
    public const string CredentialsCode = "CREDENTIALS";
    public const string PiiCode = "PII";
    public const string PiiSpecialCode = "PII_SPECIAL";
    public const string FinancialCode = "FINANCIAL";
    public const string ConfidentialCode = "CONFIDENTIAL";
    public const string PublicCode = "PUBLIC";
    public const string DefaultMaskRuleCode = "_DEFAULT";

    public static bool IsGlobal(string? scope) =>
        string.Equals(scope, Global, StringComparison.Ordinal);

    public static bool IsValidTenantScope(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope) || IsGlobal(scope))
        {
            return false;
        }

        try
        {
            _ = new TenantId(scope);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static string ValidateScope(string scope, bool allowGlobal = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);

        if (IsGlobal(scope))
        {
            if (!allowGlobal)
            {
                throw new ArgumentException("Global scope '*' is not a valid tenant scope.", nameof(scope));
            }

            return Global;
        }

        return new TenantId(scope).Value;
    }
}

/// <summary>Canonical wire codes and restrictiveness ordering for <see cref="MaskFunction"/>.</summary>
public static class ClassificationMaskFunctions
{
    public static string ToCode(MaskFunction function) => function switch
    {
        MaskFunction.Nullify => "NULLIFY",
        MaskFunction.Redact => "REDACT",
        MaskFunction.TypeSafeRedact => "TYPE_SAFE_REDACT",
        MaskFunction.Partial => "PARTIAL",
        MaskFunction.Hash => "HASH",
        MaskFunction.Tokenize => "TOKENIZE",
        MaskFunction.TruncateDate => "TRUNCATE_DATE",
        MaskFunction.BucketNumber => "BUCKET_NUMBER",
        MaskFunction.RoundNumber => "ROUND_NUMBER",
        MaskFunction.EmailDomain => "EMAIL_DOMAIN",
        MaskFunction.GeoJitter => "GEO_JITTER",
        _ => throw new ArgumentOutOfRangeException(nameof(function), function, "Unknown mask function.")
    };

    public static bool TryParse(string? code, out MaskFunction function)
    {
        switch (code)
        {
            case "NULLIFY":
                function = MaskFunction.Nullify;
                return true;
            case "REDACT":
                function = MaskFunction.Redact;
                return true;
            case "TYPE_SAFE_REDACT":
                function = MaskFunction.TypeSafeRedact;
                return true;
            case "PARTIAL":
                function = MaskFunction.Partial;
                return true;
            case "HASH":
                function = MaskFunction.Hash;
                return true;
            case "TOKENIZE":
                function = MaskFunction.Tokenize;
                return true;
            case "TRUNCATE_DATE":
                function = MaskFunction.TruncateDate;
                return true;
            case "BUCKET_NUMBER":
                function = MaskFunction.BucketNumber;
                return true;
            case "ROUND_NUMBER":
                function = MaskFunction.RoundNumber;
                return true;
            case "EMAIL_DOMAIN":
                function = MaskFunction.EmailDomain;
                return true;
            case "GEO_JITTER":
                function = MaskFunction.GeoJitter;
                return true;
            default:
                function = default;
                return false;
        }
    }

    /// <summary>Higher numbers indicate a more restrictive mask function.</summary>
    public static int GetRestrictivenessRank(MaskFunction function) => function switch
    {
        MaskFunction.Nullify => 10,
        MaskFunction.Redact => 9,
        MaskFunction.TypeSafeRedact => 9,
        MaskFunction.Hash => 8,
        MaskFunction.Tokenize => 7,
        MaskFunction.BucketNumber => 6,
        MaskFunction.RoundNumber => 5,
        MaskFunction.TruncateDate => 4,
        MaskFunction.EmailDomain => 3,
        MaskFunction.GeoJitter => 2,
        MaskFunction.Partial => 1,
        _ => throw new ArgumentOutOfRangeException(nameof(function), function, "Unknown mask function.")
    };
}

/// <summary>Canonical database codes for <see cref="ApproverMode"/>.</summary>
public static class ClassificationApproverModes
{
    public static string ToCode(ApproverMode mode) => mode switch
    {
        ApproverMode.DataOwner => "DATA_OWNER",
        ApproverMode.Steward => "STEWARD",
        ApproverMode.DataOwnerAndSteward => "DATA_OWNER_AND_STEWARD",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown approver mode.")
    };

    public static bool TryParse(string? code, out ApproverMode mode)
    {
        switch (code)
        {
            case "DATA_OWNER":
                mode = ApproverMode.DataOwner;
                return true;
            case "STEWARD":
                mode = ApproverMode.Steward;
                return true;
            case "DATA_OWNER_AND_STEWARD":
                mode = ApproverMode.DataOwnerAndSteward;
                return true;
            default:
                mode = default;
                return false;
        }
    }
}

/// <summary>Canonical database codes for <see cref="CatalogTextVisibility"/>.</summary>
public static class ClassificationCatalogTextVisibilities
{
    public static string ToCode(CatalogTextVisibility visibility) => visibility switch
    {
        CatalogTextVisibility.Visible => "VISIBLE",
        CatalogTextVisibility.Hidden => "HIDDEN",
        _ => throw new ArgumentOutOfRangeException(nameof(visibility), visibility, "Unknown catalog text visibility.")
    };

    public static bool TryParse(string? code, out CatalogTextVisibility visibility)
    {
        switch (code)
        {
            case "VISIBLE":
                visibility = CatalogTextVisibility.Visible;
                return true;
            case "HIDDEN":
                visibility = CatalogTextVisibility.Hidden;
                return true;
            default:
                visibility = default;
                return false;
        }
    }
}

public sealed record DataClassification
{
    public string Scope { get; init; } = ClassificationScopes.Global;
    public required string Code { get; init; }
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public string? ParentCode { get; init; }
    public int RestrictionRank { get; init; }
    public bool ClearGrantable { get; init; } = true;
    public bool SeparateApproval { get; init; } = true;
    public ApproverMode ApproverMode { get; init; } = ApproverMode.DataOwner;
    public string? StewardGroupSid { get; init; }
    public int? MaxClearDays { get; init; }
    public bool IsActive { get; init; } = true;
    public int Version { get; init; } = 1;
    public string UpdatedBy { get; init; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record ClassificationMaskRule
{
    public string Scope { get; init; } = ClassificationScopes.Global;
    public required string Code { get; init; }
    public MaskFunction MaskFunction { get; init; } = MaskFunction.TypeSafeRedact;
    public string? ParamsJson { get; init; }
    public string? KeyRef { get; init; }
    public CatalogTextVisibility CatalogText { get; init; } = CatalogTextVisibility.Visible;
    public int Version { get; init; } = 1;
    public string UpdatedBy { get; init; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record ColumnClassification
{
    public string Scope { get; init; } = ClassificationScopes.Global;
    public required string TableColumnId { get; init; }
    public required string Code { get; init; }
    public required string Source { get; init; }
    public string AssignedBy { get; init; } = string.Empty;
    public DateTimeOffset AssignedAt { get; init; }
}

public sealed record ColumnMaskOverride
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Scope { get; init; } = ClassificationScopes.Global;
    public required string TableColumnId { get; init; }
    public string? Code { get; init; }
    public MaskFunction MaskFunction { get; init; } = MaskFunction.TypeSafeRedact;
    public string? ParamsJson { get; init; }
    public string? KeyRef { get; init; }
    public CatalogTextVisibility CatalogText { get; init; } = CatalogTextVisibility.Visible;
    public string UpdatedBy { get; init; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>Validates classification definitions, rank invariants, cycles, and tenant overrides.</summary>
public static class ClassificationHierarchyValidator
{
    public const int MinRestrictionRank = 0;
    public const int MaxRestrictionRank = 100;

    private static readonly Regex ClassificationCodeRegex = new(@"^[A-Z][A-Z0-9_]{1,63}\z", RegexOptions.Compiled);

    public static bool IsValidClassificationCode(string? code) =>
        !string.IsNullOrEmpty(code) && ClassificationCodeRegex.IsMatch(code);

    public static bool IsValidMaskRuleCode(string? code) =>
        string.Equals(code, ClassificationScopes.DefaultMaskRuleCode, StringComparison.Ordinal) ||
        IsValidClassificationCode(code);

    public static IReadOnlyDictionary<string, DataClassification> ResolveEffective(
        IEnumerable<DataClassification> rows,
        string tenantScope) =>
        ResolveEffective(rows, new TenantId(tenantScope));

    public static IReadOnlyDictionary<string, DataClassification> ResolveEffective(
        IEnumerable<DataClassification> rows,
        TenantId tenantId)
    {
        ArgumentNullException.ThrowIfNull(rows);

        if (TenantId.IsReserved(tenantId))
        {
            throw new ArgumentException("Reserved platform tenant cannot resolve classification vocabulary.", nameof(tenantId));
        }

        var effective = new Dictionary<string, DataClassification>(StringComparer.Ordinal);
        var tenantOverrides = new List<DataClassification>();

        foreach (var row in rows)
        {
            if (ClassificationScopes.IsGlobal(row.Scope))
            {
                effective[row.Code] = row;
            }
            else if (string.Equals(row.Scope, tenantId.Value, StringComparison.Ordinal))
            {
                tenantOverrides.Add(row);
            }
        }

        foreach (var row in tenantOverrides)
        {
            effective[row.Code] = row;
        }

        return effective;
    }

    public static void ValidateDefinition(
        DataClassification item,
        IReadOnlyDictionary<string, DataClassification> effectiveCatalog)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(effectiveCatalog);

        _ = ClassificationScopes.ValidateScope(item.Scope, allowGlobal: true);

        if (!IsValidClassificationCode(item.Code))
        {
            throw new ArgumentException($"Invalid classification code '{item.Code}'.", nameof(item));
        }

        if (string.IsNullOrWhiteSpace(item.DisplayName))
        {
            throw new ArgumentException("DisplayName is required.", nameof(item));
        }

        if (item.RestrictionRank < MinRestrictionRank || item.RestrictionRank > MaxRestrictionRank)
        {
            throw new ArgumentOutOfRangeException(
                nameof(item),
                item.RestrictionRank,
                $"RestrictionRank must be between {MinRestrictionRank} and {MaxRestrictionRank}.");
        }

        if (string.Equals(item.Code, ClassificationScopes.CredentialsCode, StringComparison.Ordinal) &&
            item.ClearGrantable)
        {
            throw new ArgumentException("CREDENTIALS classification can never allow clear access.", nameof(item));
        }

        if (item.ApproverMode != ApproverMode.DataOwner &&
            string.IsNullOrWhiteSpace(item.StewardGroupSid))
        {
            throw new ArgumentException(
                "StewardGroupSid is required when ApproverMode involves a steward.",
                nameof(item));
        }

        if (item.MaxClearDays is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(item), item.MaxClearDays, "MaxClearDays must be positive.");
        }

        if (item.ParentCode is not null)
        {
            ValidateParentHierarchy(item, effectiveCatalog);
        }

        foreach (var existing in effectiveCatalog.Values)
        {
            if (string.Equals(existing.ParentCode, item.Code, StringComparison.Ordinal) &&
                existing.RestrictionRank < item.RestrictionRank)
            {
                throw new ArgumentException(
                    $"Existing child '{existing.Code}' has restriction rank {existing.RestrictionRank} below parent rank {item.RestrictionRank}.",
                    nameof(item));
            }
        }
    }

    private static void ValidateParentHierarchy(
        DataClassification item,
        IReadOnlyDictionary<string, DataClassification> effectiveCatalog)
    {
        if (string.Equals(item.ParentCode, item.Code, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Classification '{item.Code}' cannot reference itself as parent.", nameof(item));
        }

        if (!effectiveCatalog.TryGetValue(item.ParentCode!, out var parent) || !parent.IsActive)
        {
            throw new ArgumentException($"Parent classification '{item.ParentCode}' was not found or is inactive.", nameof(item));
        }

        if (item.RestrictionRank < parent.RestrictionRank)
        {
            throw new ArgumentException(
                $"Child classification '{item.Code}' rank ({item.RestrictionRank}) cannot be lower than parent '{parent.Code}' rank ({parent.RestrictionRank}).",
                nameof(item));
        }

        var merged = new Dictionary<string, DataClassification>(effectiveCatalog, StringComparer.Ordinal)
        {
            [item.Code] = item
        };

        var visited = new HashSet<string>(StringComparer.Ordinal) { item.Code };
        var cursor = item.ParentCode;
        while (cursor is not null)
        {
            if (!visited.Add(cursor))
            {
                throw new ArgumentException(
                    $"Cycle detected in classification hierarchy involving '{cursor}'.",
                    nameof(item));
            }

            if (!merged.TryGetValue(cursor, out var current))
            {
                break;
            }

            cursor = current.ParentCode;
        }
    }
}
