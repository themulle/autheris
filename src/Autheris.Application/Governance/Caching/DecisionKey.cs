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
        string[]? tableColumns)
    {
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
        ArgumentNullException.ThrowIfNull(action);

        return new DecisionKey(
            epoch,
            context.Tenant.Value,
            context.UserSid.Value,
            context.TargetTable.ToString(),
            action,
            context.PurposeId,
            context.ClientIp,
            context.TargetDialect,
            SortedGroups(context.GroupSids),
            SortedAttributes(context.Attributes),
            SortedStrings(context.RequestedColumns),
            context.TableColumns is null ? null : SortedStrings(context.TableColumns));
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

    private static KeyValuePair<string, string?>[] SortedAttributes(IReadOnlyDictionary<string, object?>? attributes)
    {
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

        Array.Sort(result, static (a, b) => string.CompareOrdinal(a.Key, b.Key));
        return result;
    }

    // Scalars use their invariant text. Collections get a length-prefixed encoding: a plain ToString() of a list is the type
    // name, which would let different contents share a key.
    private static string? Canonical(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case string text:
                return text;
            case IEnumerable sequence:
                var builder = new StringBuilder("[");
                foreach (var element in sequence)
                {
                    var item = Canonical(element) ?? string.Empty;
                    builder.Append(item.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(item);
                }

                return builder.Append(']').ToString();
            default:
                return Convert.ToString(value, CultureInfo.InvariantCulture);
        }
    }
}
