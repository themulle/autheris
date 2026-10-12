namespace Autheris.Infrastructure.Streaming;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

public sealed record WalRelation(
    uint RelationId,
    string Namespace,
    string RelationName,
    IReadOnlyList<string> ColumnNames
)
{
    public string Schema => Namespace;
    public string TableName => RelationName;
    public TableIdentifier TableIdentifier => new("postgresql", Namespace, RelationName);
}

public sealed record WalChange(
    uint RelationId,
    CdcOperation Operation,
    IReadOnlyDictionary<string, object?>? Before,
    IReadOnlyDictionary<string, object?>? After,
    ulong Lsn,
    DateTimeOffset Timestamp
);

/// <summary>
/// F-CDC-03: Decodes PostgreSQL logical replication messages (pgoutput) into gateway CdcEvents.
/// Extracts tenant identity, formats qualified table names, and builds before/after states.
/// </summary>
public sealed class WalMessageDecoder
{
    private readonly ConcurrentDictionary<uint, WalRelation> _relations = new();

    public void RegisterRelation(uint relationId, string schema, string table, IReadOnlyList<string> columns)
    {
        _relations[relationId] = new WalRelation(relationId, schema, table, columns);
    }

    public void RegisterRelation(uint relationId, TableIdentifier table, IReadOnlyList<string> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        RegisterRelation(relationId, table.Schema, table.TableName, columns);
    }

    public bool TryGetRelation(uint relationId, out WalRelation? relation)
    {
        return _relations.TryGetValue(relationId, out relation);
    }

    public CdcEvent? DecodeChange(WalChange change)
    {
        ArgumentNullException.ThrowIfNull(change);

        if (!_relations.TryGetValue(change.RelationId, out var relation))
        {
            return null;
        }

        var tableId = new TableIdentifier("postgresql", relation.Namespace, relation.RelationName);

        // Extract tenant id if present in After or Before columns
        string? tenantId = null;
        if (change.After != null)
        {
            tenantId = ExtractTenantId(change.After);
        }
        if (tenantId == null && change.Before != null)
        {
            tenantId = ExtractTenantId(change.Before);
        }

        var metadata = new Dictionary<string, string>
        {
            ["lsn"] = change.Lsn.ToString(),
            ["source"] = "pgoutput",
            ["relationId"] = change.RelationId.ToString()
        };

        return new CdcEvent(
            EventId: $"pg_{change.RelationId}_{change.Lsn}",
            Table: tableId,
            Operation: change.Operation,
            TenantId: tenantId,
            Before: change.Before,
            After: change.After,
            Timestamp: change.Timestamp,
            Metadata: metadata
        );
    }

    public static string? ExtractTenantId(IReadOnlyDictionary<string, object?> data)
    {
        foreach (var key in data.Keys)
        {
            if (string.Equals(key, "tenant_id", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "tenantId", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "tid", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "tenant", StringComparison.OrdinalIgnoreCase))
            {
                return data[key]?.ToString();
            }
        }
        return null;
    }
}
