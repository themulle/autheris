namespace Autheris.Extensions.Cdc.CustomQuery;

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using Autheris.Domain.Model;
using Autheris.Domain.Options;

/// <summary>
/// Strictly typed DbParameter binder for multi-watermark query polling (PLAN-CDC-101 D-3, SEC-CDC101-02).
/// Enforces parameter whitelisting and Silent-NULL defense.
/// </summary>
public static class QueryPollerParameterBinder
{
    public static void BindParameters(
        DbCommand cmd,
        IReadOnlyList<WatermarkDimensionDefinition> dimensions,
        MultiWatermarkVector currentVector,
        int batchSize)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        ArgumentNullException.ThrowIfNull(dimensions);
        ArgumentNullException.ThrowIfNull(currentVector);

        foreach (var dim in dimensions)
        {
            var p = cmd.CreateParameter();
            var paramName = string.IsNullOrWhiteSpace(dim.ParameterName) ? "p_wm_" + dim.Name : dim.ParameterName.TrimStart('@');
            p.ParameterName = "@" + paramName;

            var rawValue = currentVector.GetValue(dim.Name);
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                rawValue = dim.InitialValue;
            }

            // SEC-CDC101-02: Silent-NULL Defense
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                throw new InvalidOperationException(
                    $"Silent-NULL defense violation: Watermark dimension '{dim.Name}' has no current or initial value. Binding NULL is forbidden.");
            }

            p.DbType = MapToDbType(dim.Type);
            p.Value = ParseTypedValue(rawValue, dim.Type);
            cmd.Parameters.Add(p);
        }

        var batchParam = cmd.CreateParameter();
        batchParam.ParameterName = "@p_batch_size";
        batchParam.DbType = DbType.Int32;
        batchParam.Value = batchSize;
        cmd.Parameters.Add(batchParam);
    }

    public static DbType MapToDbType(WatermarkDimensionType type) => type switch
    {
        WatermarkDimensionType.DateTimeOffset => DbType.DateTimeOffset,
        WatermarkDimensionType.Int64 => DbType.Int64,
        WatermarkDimensionType.Int32 => DbType.Int32,
        WatermarkDimensionType.String => DbType.String,
        _ => DbType.String
    };

    public static object ParseTypedValue(string raw, WatermarkDimensionType type)
    {
        return type switch
        {
            WatermarkDimensionType.DateTimeOffset => DateTimeOffset.Parse(raw, CultureInfo.InvariantCulture),
            WatermarkDimensionType.Int64 => long.Parse(raw, CultureInfo.InvariantCulture),
            WatermarkDimensionType.Int32 => int.Parse(raw, CultureInfo.InvariantCulture),
            _ => raw
        };
    }
}
