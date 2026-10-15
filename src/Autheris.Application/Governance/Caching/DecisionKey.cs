namespace Autheris.Application.Governance.Caching;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// Value key of a cached policy decision. Every input that can change a decision is a separate field (no string joining, so
/// separator characters inside groups, attributes or columns cannot make two different contexts collide). Collections are
/// normalised (sorted ordinal) so permutations hit the same entry. <c>Timestamp</c> is deliberately not part of the key:
/// decisions that depend on time are never cached.
/// </summary>
internal readonly struct DecisionKey : IEquatable<DecisionKey>
{
    private static readonly string[] NoStrings = [];
    private static readonly KeyValuePair<string, string?>[] NoAttributes = [];

    private readonly int _hash;

    /// <summary>False when the context is ambiguous (attribute keys that differ only by case): such a decision is never cached.</summary>
    public bool IsCacheable { get; }

    private DecisionKey(
        long epoch,
        string tenant,
        string userSid,
        string table,
        string action,
        string? purposeId,
        IPAddress? clientIp,
        DatabaseDialect? targetDialect,
        string[] groups,
        KeyValuePair<string, string?>[] attributes,
        string[] columns,
        string[]? tableColumns,
        bool isCacheable)
    {
        IsCacheable = isCacheable;
        Epoch = epoch;
        Tenant = tenant;
        UserSid = userSid;
        Table = table;
        Action = action;
        PurposeId = purposeId;
        ClientIp = clientIp;
        TargetDialect = targetDialect;
        Groups = groups;
        Attributes = attributes;
        Columns = columns;
        TableColumns = tableColumns;

        var hash = new HashCode();
        hash.Add(epoch);
        hash.Add(tenant, StringComparer.Ordinal);
        hash.Add(userSid, StringComparer.Ordinal);
        hash.Add(table, StringComparer.Ordinal);
        hash.Add(action, StringComparer.Ordinal);
        hash.Add(purposeId, StringComparer.Ordinal);
        hash.Add(clientIp);
        hash.Add(targetDialect);
        AddAll(ref hash, groups);
        foreach (var attribute in attributes)
        {
            hash.Add(attribute.Key, StringComparer.Ordinal);
            hash.Add(attribute.Value, StringComparer.Ordinal);
        }

        AddAll(ref hash, columns);
        if (tableColumns is not null)
        {
            hash.Add(1);
            AddAll(ref hash, tableColumns);
        }

        _hash = hash.ToHashCode();
    }

    public long Epoch { get; }

    public string Tenant { get; }

    public string UserSid { get; }

    public string Table { get; }

    public string Action { get; }

    public string? PurposeId { get; }

    public IPAddress? ClientIp { get; }

    public DatabaseDialect? TargetDialect { get; }

    public string[] Groups { get; }

    public KeyValuePair<string, string?>[] Attributes { get; }

    public string[] Columns { get; }

    public string[]? TableColumns { get; }

    public static DecisionKey Create(long epoch, SecurityEvaluationContext context, string action)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Create(epoch, context, action, context.TargetTable.ToString());
    }

    public static DecisionKey Create(long epoch, SecurityEvaluationContext context, string action, string table)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(table);

        var attributes = SortedAttributes(context.Attributes, out var cacheable);
        return new DecisionKey(
            epoch,
            context.Tenant.Value,
            context.UserSid.Value,
            table,
            action,
            context.PurposeId,
            context.ClientIp,
            context.TargetDialect,
            SortedGroups(context.GroupSids),
            attributes,
            SortedStrings(context.RequestedColumns),
            context.TableColumns is null ? null : SortedStrings(context.TableColumns),
            cacheable);
    }

    public bool Equals(DecisionKey other)
    {
        if (_hash != other._hash || Epoch != other.Epoch || TargetDialect != other.TargetDialect)
        {
            return false;
        }

        if (!string.Equals(Tenant, other.Tenant, StringComparison.Ordinal) ||
            !string.Equals(UserSid, other.UserSid, StringComparison.Ordinal) ||
            !string.Equals(Table, other.Table, StringComparison.Ordinal) ||
            !string.Equals(Action, other.Action, StringComparison.Ordinal) ||
            !string.Equals(PurposeId, other.PurposeId, StringComparison.Ordinal) ||
            !Equals(ClientIp, other.ClientIp))
        {
            return false;
        }

        if ((TableColumns is null) != (other.TableColumns is null))
        {
            return false;
        }

        return SameStrings(Groups, other.Groups) &&
               SameStrings(Columns, other.Columns) &&
               (TableColumns is null || SameStrings(TableColumns, other.TableColumns!)) &&
               SameAttributes(Attributes, other.Attributes);
    }

    public override bool Equals(object? obj) => obj is DecisionKey other && Equals(other);

    public override int GetHashCode() => _hash;

    private static void AddAll(ref HashCode hash, string[] values)
    {
        hash.Add(values.Length);
        foreach (var value in values)
        {
            hash.Add(value, StringComparer.Ordinal);
        }
    }

    private static bool SameStrings(string[]? left, string[]? right)
    {
        left ??= NoStrings;
        right ??= NoStrings;
        if (left.Length != right.Length)
        {
            return false;
        }

        for (var i = 0; i < left.Length; i++)
        {
            if (!string.Equals(left[i], right[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameAttributes(KeyValuePair<string, string?>[]? left, KeyValuePair<string, string?>[]? right)
    {
        left ??= NoAttributes;
        right ??= NoAttributes;
        if (left.Length != right.Length)
        {
            return false;
        }

        for (var i = 0; i < left.Length; i++)
        {
            if (!string.Equals(left[i].Key, right[i].Key, StringComparison.Ordinal) ||
                !string.Equals(left[i].Value, right[i].Value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static string[] SortedGroups(IReadOnlyCollection<Sid>? groups)
    {
        if (groups is null || groups.Count == 0)
        {
            return NoStrings;
        }

        var result = new string[groups.Count];
        var index = 0;
        foreach (var group in groups)
        {
            result[index++] = group.Value;
        }

        Array.Sort(result, StringComparer.Ordinal);
        return result;
    }

    private static string[] SortedStrings(IReadOnlyCollection<string>? values)
    {
        if (values is null || values.Count == 0)
        {
            return NoStrings;
        }

        var result = new string[values.Count];
        var index = 0;
        foreach (var value in values)
        {
            result[index++] = value;
        }

        Array.Sort(result, StringComparer.Ordinal);
        return result;
    }

    private static KeyValuePair<string, string?>[] SortedAttributes(IReadOnlyDictionary<string, object?>? attributes, out bool cacheable)
    {
        cacheable = true;
        if (attributes is null || attributes.Count == 0)
        {
            return NoAttributes;
        }

        var result = new KeyValuePair<string, string?>[attributes.Count];
        var index = 0;
        foreach (var (key, value) in attributes)
        {
            result[index++] = new KeyValuePair<string, string?>(key, Canonical(value));
        }

        // Case-insensitive primary order puts keys that differ only by case next to each other.
        Array.Sort(result, static (a, b) =>
        {
            var c = string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : string.CompareOrdinal(a.Key, b.Key);
        });

        for (var i = 1; i < result.Length; i++)
        {
            if (string.Equals(result[i - 1].Key, result[i].Key, StringComparison.OrdinalIgnoreCase))
            {
                // Evaluation resolves such keys by dictionary comparer: ambiguous, so never cached.
                cacheable = false;
                break;
            }
        }

        return result;
    }

    // Every value carries a type tag, so a string, a number and a collection with the same text never share a key. Scalars that
    // are formatted by the evaluator with the current culture are keyed with both the invariant and the current-culture text.
    // Collections get a length-prefixed encoding.
    private static string? Canonical(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case string text:
                return "s:" + text;
            case IEnumerable sequence:
                var builder = new StringBuilder("e:[");
                foreach (var element in sequence)
                {
                    var item = Canonical(element) ?? "null";
                    builder.Append(item.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(item);
                }

                return builder.Append(']').ToString();
            default:
                var invariant = Convert.ToString(value, CultureInfo.InvariantCulture);
                var current = value.ToString();
                return string.Equals(invariant, current, StringComparison.Ordinal)
                    ? $"{value.GetType().FullName}:{invariant}"
                    : $"{value.GetType().FullName}:{invariant}|{current}";
        }
    }
}
