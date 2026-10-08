using System;
using System.Collections.Generic;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using MemoryPack;

namespace Autheris.Domain.Model;

/// <summary>
/// High-speed binary DTO serialized with MemoryPack for Redis and Microsoft Garnet L2 caching.
/// Zero-allocation, blitting-optimized binary layout.
/// </summary>
[MemoryPackable]
public partial class CachedConsentEnvelope
{
    public string Domain { get; set; } = "default";
    public string Schema { get; set; } = string.Empty;
    public string TableName { get; set; } = string.Empty;
    public bool IsAllowed { get; set; }
    public long Epoch { get; set; }
    public string? RowFilterSql { get; set; }
    public bool HasUnconstrainedColumnAllow { get; set; }
    public Dictionary<string, int> ColumnAccess { get; set; } = new();
    public List<string> DeniedReasons { get; set; } = new();

    public static CachedConsentEnvelope FromDecision(TableAccessDecision decision, long epoch)
    {
        ArgumentNullException.ThrowIfNull(decision);

        var env = new CachedConsentEnvelope
        {
            Domain = string.IsNullOrWhiteSpace(decision.Table.Domain) ? "default" : decision.Table.Domain,
            Schema = decision.Table.Schema ?? string.Empty,
            TableName = decision.Table.TableName ?? string.Empty,
            IsAllowed = decision.IsAllowed,
            Epoch = epoch,
            RowFilterSql = decision.CombinedRowFilterSql,
            HasUnconstrainedColumnAllow = decision.HasUnconstrainedColumnAllow,
            ColumnAccess = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            DeniedReasons = new List<string>(decision.DeniedReasons)
        };

        foreach (var (col, level) in decision.ColumnAccess)
        {
            env.ColumnAccess[col] = (int)level;
        }

        return env;
    }

    public TableAccessDecision ToDecision()
    {
        var colMap = new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase);
        foreach (var (col, levelVal) in ColumnAccess)
        {
            colMap[col] = Enum.IsDefined((ColumnAccessLevel)levelVal)
                ? (ColumnAccessLevel)levelVal
                : ColumnAccessLevel.Deny;
        }

        var tableId = new TableIdentifier(
            string.IsNullOrWhiteSpace(Domain) ? "default" : Domain,
            string.IsNullOrWhiteSpace(Schema) ? "public" : Schema,
            string.IsNullOrWhiteSpace(TableName) ? "table" : TableName);

        return new TableAccessDecision(
            tableId,
            IsAllowed,
            colMap,
            RowFilterSql,
            DeniedReasons,
            HasUnconstrainedColumnAllow);
    }
}
