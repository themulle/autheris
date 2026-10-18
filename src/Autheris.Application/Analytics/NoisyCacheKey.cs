namespace Autheris.Application.Analytics;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

/// <summary>
/// #112 A1-2 / A1-12 (SEC-112-12, -21, -26): canonical, collision-free key of the noisy-result cache and the fingerprint
/// of the access decision behind it. Every field is length-prefixed, parameters are sorted by name and typed, and the
/// outputs are SHA-256 digests so neither the key nor any log line ever carries a bound value.
/// </summary>
internal static class NoisyCacheKey
{
    /// <summary>Digest of everything the access decision contributes to a result: column levels, row filters, bound values and the caller-attribute generation.</summary>
    public static string Fingerprint(TableAccessDecision decision, TableMetadata metadata, long attributeGeneration)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(metadata);

        var w = new CanonicalWriter();
        w.Add("decision-v1");
        w.Add(decision.Table.ToString());
        w.Add(decision.IsAllowed ? "allow" : "deny");
        w.Add(decision.HasUnconstrainedColumnAllow ? "1" : "0");
        w.Add(attributeGeneration.ToString(CultureInfo.InvariantCulture));

        var access = decision.ColumnAccess
            .Select(kv => (Name: kv.Key.ToLowerInvariant(), Level: kv.Value))
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .ToList();
        w.Add(access.Count);
        foreach (var (name, level) in access)
        {
            w.Add(name);
            w.Add(level.ToString());
        }

        var effective = metadata.Columns
            .Select(c => c.ColumnName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        w.Add(effective.Count);
        foreach (var column in effective)
        {
            w.Add(column);
            w.Add(decision.GetEffectiveColumnAccess(column, metadata).ToString());
        }

        w.Add(decision.CombinedRowFilterSql);
        w.Add(decision.MandatoryRowPredicateSql);
        w.AddParameters(decision.RowFilterParameters);
        w.Add(decision.AppliedVirtualFilters is { Count: > 0 } vf ? string.Join('\n', vf.Order(StringComparer.Ordinal)) : null);
        w.Add(decision.ColumnMasks?.GetType().FullName);
        return w.Digest();
    }

    public static string Build(
        TenantId tenantId, Sid userSid, TableIdentifier table, IReadOnlyList<string> groupBy,
        IReadOnlyList<AggregateSelection> aggregates, TableFilterClause? where, IReadOnlyList<TableOrderBy>? orderBy,
        int limit, int minGroupSize, SmallGroupStrategy strategy, string utcDay, string decisionFingerprint)
    {
        var w = new CanonicalWriter();
        w.Add("noisy-key-v1");
        w.Add(tenantId.Value);
        w.Add(userSid.Value);
        w.Add(table.Domain);
        w.Add(table.Schema);
        w.Add(table.TableName);
        w.Add(groupBy.Count);
        foreach (var g in groupBy)
        {
            w.Add(g);
        }

        w.Add(aggregates.Count);
        foreach (var a in aggregates)
        {
            w.Add(a.Function.ToString());
            w.Add(a.Column);
            w.Add(a.EffectiveAlias);
        }

        w.Add(where?.SqlPredicate);
        w.AddParameters(where?.Parameters);
        var order = orderBy ?? [];
        w.Add(order.Count);
        foreach (var o in order)
        {
            w.Add(o.Column);
            w.Add(o.Descending ? "D" : "A");
        }

        w.Add(limit);
        w.Add(minGroupSize);
        w.Add(strategy.ToString());
        w.Add(utcDay);
        w.Add(decisionFingerprint);
        return w.Digest();
    }

    private sealed class CanonicalWriter
    {
        private readonly StringBuilder _sb = new();

        public void Add(int value) => Add(value.ToString(CultureInfo.InvariantCulture));

        /// <summary>Length-prefixed field; null is distinct from the empty string.</summary>
        public void Add(string? value)
        {
            if (value is null)
            {
                _sb.Append("~;");
                return;
            }

            _sb.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append(';');
        }

        public void AddParameters(IReadOnlyDictionary<string, object?>? parameters)
        {
            var sorted = (parameters ?? new Dictionary<string, object?>())
                .OrderBy(p => p.Key, StringComparer.Ordinal)
                .ToList();
            Add(sorted.Count);
            foreach (var (name, value) in sorted)
            {
                Add(name);
                AddTyped(value);
            }
        }

        private void AddTyped(object? value)
        {
            if (value is null)
            {
                Add("null");
                return;
            }

            Add(value.GetType().FullName);
            Add(value switch
            {
                byte[] bytes => Convert.ToBase64String(bytes),
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString()
            });
        }

        public string Digest() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_sb.ToString()))).ToLowerInvariant();
    }
}
