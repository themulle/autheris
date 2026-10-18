namespace Autheris.Domain.Options;

using System.ComponentModel.DataAnnotations;

public sealed class QueryCostOptions
{
    public const string SectionName = "Gateway:Governance:QueryCost";

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

    [Range(1, 1000)]
    public int DefaultListMultiplier { get; set; } = 10;

    public bool EnablePostExecutionSettlement { get; set; } = true;
}
