namespace Autheris.Domain.Options;

using System.ComponentModel.DataAnnotations;

public sealed class QueryCostOptions
{
    public const string SectionName = "Gateway:QueryCost";

    [Range(1, 1000)]
    public int BaseCost { get; set; } = 1;

    public int BaseQueryCost
    {
        get => BaseCost;
        set => BaseCost = value;
    }

    [Range(0, 1000)]
    public double RowCostFactor { get; set; } = 0.5;

    public double CostPerRowReturned
    {
        get => RowCostFactor;
        set => RowCostFactor = value;
    }

    [Range(0, 500)]
    public int NestedCostFactor { get; set; } = 1;

    public int NestedDepthFactor
    {
        get => NestedCostFactor;
        set => NestedCostFactor = value;
    }

    [Range(1, 1000)]
    public int FailureFee { get; set; } = 1;

    [Range(1, 1000)]
    public int CostPerSingleRowLookup { get; set; } = 1;

    /// <summary>I417-SC-A02: an out-of-range multiplier is rejected, never replaced by a fallback value.</summary>
    public static int RequireValidListMultiplier(int value) =>
        value is >= 1 and <= 1000 ? value : throw new ArgumentOutOfRangeException(nameof(value), "DefaultListMultiplier must be between 1 and 1000.");

    [Range(1, 1000)]
    public int DefaultListMultiplier { get; set; } = 10;

    public bool EnablePostExecutionSettlement { get; set; } = true;
}
