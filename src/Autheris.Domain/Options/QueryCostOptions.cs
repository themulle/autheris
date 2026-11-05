namespace Autheris.Domain.Options;

using System.ComponentModel.DataAnnotations;

public sealed class QueryCostOptions
{
    public const string SectionName = "Gateway:QueryCost";

    [Range(1, 1000)]
    public int BaseCost { get; set; } = 1;

    [Range(0, 1000)]
    public double RowCostFactor { get; set; } = 0.5;

    [Range(0, 500)]
    public int NestedCostFactor { get; set; } = 1;

    [Range(1, 1000)]
    public int FailureFee { get; set; } = 1;

    [Range(1, 1000)]
    public int CostPerSingleRowLookup { get; set; } = 1;

    /// <summary>I417-SC-A02: an out-of-range multiplier is rejected, never replaced by a fallback value.</summary>
    public static int RequireValidListMultiplier(int value) =>
        value is >= 1 and <= 1000 ? value : throw new ArgumentOutOfRangeException(nameof(value), "DefaultListMultiplier must be between 1 and 1000.");

    [Range(1, 1000)]
    public int DefaultListMultiplier { get; set; } = 10;

    /// <summary>
    /// REQ-COST-419: Relative weight for plain scalar columns selected on a list (default 1.0).
    /// Lower values (e.g. 0.2) discount scalar fields to allow realistic page sizes on wide tables.
    /// </summary>
    [Range(0.01, 1.0)]
    public double ScalarColumnWeight { get; set; } = 1.0;

    /// <summary>
    /// Issue #421: Multiplier applied to root list pricing when neither filter nor ordering can utilize a database index.
    /// Default 2.0 (doubles the base list pricing for unindexed queries).
    /// </summary>
    [Range(1.0, 50.0)]
    public double UnindexedRootListSurchargeMultiplier { get; set; } = 2.0;

    public bool EnablePostExecutionSettlement { get; set; } = true;
}
