namespace Autheris.Application.Usage;

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Autheris.Domain.Common;

/// <summary>Shared field validation rules for tariff versions and bootstrap tariff options.</summary>
public static partial class UsageTariffValidation
{
    public const decimal MaxRate = 1_000_000_000m;
    public const int MaxIdentifierLength = 128;
    public const int MaxSourceCostUnitLength = 32;
    public const int MaxCreatedByLength = 184;
    public const int MaxReasonLength = 512;

    [GeneratedRegex(@"^[A-Za-z0-9 _.-]{1,32}\z", RegexOptions.CultureInvariant)]
    public static partial Regex UnitLabelRegex();

    public static void ValidateRatesAndMetadata(
        string? tenantId,
        string? dataSource,
        decimal basePerQuery,
        decimal perRow,
        decimal perMib,
        decimal perComputeSecond,
        decimal perAnalysisCost,
        decimal sourceCostFactor,
        string? sourceCostUnit,
        string? unitLabel,
        short computeBasis,
        string? reason,
        ICollection<string> failures,
        string prefix = "")
    {
        ArgumentNullException.ThrowIfNull(failures);

        if (string.IsNullOrWhiteSpace(tenantId))
        {
            failures.Add($"{prefix}TenantId is required.");
        }
        else if (!string.Equals(tenantId, "*", StringComparison.Ordinal) && !TenantId.TryParse(tenantId, out _))
        {
            failures.Add($"{prefix}TenantId '{tenantId}' must be '*' or a valid tenant identifier.");
        }

        if (string.IsNullOrWhiteSpace(dataSource) || dataSource.Length > MaxIdentifierLength)
        {
            failures.Add($"{prefix}DataSource must be between 1 and {MaxIdentifierLength} characters.");
        }

        ValidateRate(basePerQuery, $"{prefix}BasePerQuery", failures);
        ValidateRate(perRow, $"{prefix}PerRow", failures);
        ValidateRate(perMib, $"{prefix}PerMib", failures);
        ValidateRate(perComputeSecond, $"{prefix}PerComputeSecond", failures);
        ValidateRate(perAnalysisCost, $"{prefix}PerAnalysisCost", failures);
        ValidateRate(sourceCostFactor, $"{prefix}SourceCostFactor", failures);

        if (basePerQuery <= 0m && perRow <= 0m && perMib <= 0m && perComputeSecond <= 0m && perAnalysisCost <= 0m && sourceCostFactor <= 0m)
        {
            failures.Add($"{prefix}At least one tariff rate must be greater than 0.");
        }

        if (sourceCostFactor > 0m)
        {
            if (string.IsNullOrWhiteSpace(sourceCostUnit) || sourceCostUnit.Length > MaxSourceCostUnitLength)
            {
                failures.Add($"{prefix}SourceCostUnit is required (max {MaxSourceCostUnitLength} chars) when SourceCostFactor > 0.");
            }
        }
        else if (!string.IsNullOrWhiteSpace(sourceCostUnit))
        {
            failures.Add($"{prefix}SourceCostUnit may only be set when SourceCostFactor > 0.");
        }

        if (string.IsNullOrWhiteSpace(unitLabel) || !UnitLabelRegex().IsMatch(unitLabel))
        {
            failures.Add($"{prefix}UnitLabel must match ^[A-Za-z0-9 _.-]{{1,32}}$ and not be blank.");
        }

        if (computeBasis is not (0 or 1))
        {
            failures.Add($"{prefix}ComputeBasis must be 0 or 1.");
        }

        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > MaxReasonLength)
        {
            failures.Add($"{prefix}Reason is required and must be at most {MaxReasonLength} characters.");
        }
    }

    private static void ValidateRate(decimal rate, string fieldName, ICollection<string> failures)
    {
        if (rate < 0m || rate > MaxRate)
        {
            failures.Add($"{fieldName} must be between 0 and {MaxRate}.");
        }
    }
}
