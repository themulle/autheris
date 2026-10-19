namespace Autheris.Domain.Options;

using System;
using System.Collections.Generic;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifiers should not contain type names", Justification = "Exact database/primitive type names")]
public enum WatermarkDimensionType
{
    DateTimeOffset = 1,
    Int64 = 2,
    Int32 = 3,
    String = 4
}

public enum SoftDeleteStrategy
{
    NotNullTimestamp = 1,
    BooleanFlag = 2,
    StringStatus = 3
}

public class WatermarkDimensionDefinition
{
    public string Name { get; set; } = string.Empty;
    public WatermarkDimensionType Type { get; set; } = WatermarkDimensionType.DateTimeOffset;
    public string ParameterName { get; set; } = string.Empty;
    public string Column { get; set; } = string.Empty;

    /// <summary>
    /// SEC-CDC101-02: Mandatory initial value to prevent SQL three-valued logic silent NULL starvation.
    /// </summary>
    public string InitialValue { get; set; } = string.Empty;
}

public sealed class WatermarkDimensionOptions : WatermarkDimensionDefinition
{
}

public class SoftDeleteDefinition
{
    public bool Enabled { get; set; } = true;
    public string Column { get; set; } = string.Empty;
    public SoftDeleteStrategy Strategy { get; set; } = SoftDeleteStrategy.NotNullTimestamp;
    public string? DeletedValue { get; set; }
}

public sealed class SoftDeleteOptions : SoftDeleteDefinition
{
}

public class CustomQueryPollerStreamOptions
{
    public string StreamName { get; set; } = string.Empty;
    public string Table { get; set; } = string.Empty;
    public string DataSource { get; set; } = string.Empty;
    public string QueryTemplate { get; set; } = string.Empty;
    public int PollingIntervalMilliseconds { get; set; } = 1000;
    public int BatchSize { get; set; } = 500;
    public int QueryTimeoutSeconds { get; set; } = 30;
    public List<string> KeyColumns { get; set; } = [];
    public List<WatermarkDimensionDefinition> Watermarks { get; set; } = [];

    public List<WatermarkDimensionDefinition> Dimensions
    {
        get => Watermarks;
        set => Watermarks = value;
    }

    public SoftDeleteDefinition? SoftDelete { get; set; }
    public string? TenantColumn { get; set; }
    public string? OwnerTenantId { get; set; }
}

public sealed class CustomQueryPollerStreamDefinition : CustomQueryPollerStreamOptions
{
}

public sealed class CustomQueryPollerOptions
{
    public const string SectionName = "Gateway:CustomQueryPoller";

    public bool Enabled { get; set; } = false;

    public List<CustomQueryPollerStreamOptions> Pollers { get; set; } = [];

    public List<CustomQueryPollerStreamOptions> Streams
    {
        get => Pollers;
        set => Pollers = value;
    }
}
