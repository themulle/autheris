namespace Autheris.Application.VirtualFilters;

using System;
using System.Collections.Generic;
using Autheris.Application.Policy;
using Autheris.Domain.Common;

/// <summary>
/// Immutable, allocation-free composite key for memoizing <see cref="MandatoryFilterOutcome"/> per generation.
/// Avoids string formatting and heap allocations during high-frequency filter resolution.
/// </summary>
public readonly struct VirtualFilterMemoKey : IEquatable<VirtualFilterMemoKey>
{
    private readonly long _generation;
    private readonly TenantId _tenant;
    private readonly Sid _userSid;
    private readonly IReadOnlySet<Sid> _groupSids;
    private readonly IReadOnlySet<string> _roles;
    private readonly TableIdentifier _tableId;
    private readonly DatabaseDialect _dialect;
    private readonly FilterObjectKinds _objectKind;
    private readonly IReadOnlySet<Sid>? _allUserSids;
    private readonly int _hashCode;

    public VirtualFilterMemoKey(long generation, MandatoryFilterQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        _generation = generation;
        _tenant = query.Tenant;
        _userSid = query.UserSid;
        _groupSids = query.GroupSids ?? new HashSet<Sid>();
        _roles = query.Roles ?? new HashSet<string>();
        _tableId = query.Metadata.Identifier;
        _dialect = query.Metadata.Dialect;
        _objectKind = query.ObjectKind;
        _allUserSids = query.AllUserSids;

        var hc = new HashCode();
        hc.Add(_generation);
        hc.Add(_tenant);
        hc.Add(_userSid);
        hc.Add(_tableId);
        hc.Add((int)_dialect);
        hc.Add((int)_objectKind);

        // Order-independent set hashing via XOR accumulation
        int groupsHash = 0;
        foreach (var group in _groupSids)
        {
            groupsHash ^= group.GetHashCode();
        }
        hc.Add(groupsHash);

        int rolesHash = 0;
        foreach (var role in _roles)
        {
            rolesHash ^= StringComparer.Ordinal.GetHashCode(role);
        }
        hc.Add(rolesHash);

        if (_allUserSids != null)
        {
            int allSidsHash = 0;
            foreach (var sid in _allUserSids)
            {
                allSidsHash ^= sid.GetHashCode();
            }
            hc.Add(allSidsHash);
        }

        _hashCode = hc.ToHashCode();
    }

    public bool Equals(VirtualFilterMemoKey other)
    {
        if (_hashCode != other._hashCode ||
            _generation != other._generation ||
            _dialect != other._dialect ||
            _objectKind != other._objectKind ||
            !_tenant.Equals(other._tenant) ||
            !_userSid.Equals(other._userSid) ||
            !_tableId.Equals(other._tableId))
        {
            return false;
        }

        if (_roles.Count != other._roles.Count || _groupSids.Count != other._groupSids.Count)
        {
            return false;
        }

        if (!_roles.SetEquals(other._roles) || !_groupSids.SetEquals(other._groupSids))
        {
            return false;
        }

        if ((_allUserSids == null) != (other._allUserSids == null))
        {
            return false;
        }

        if (_allUserSids != null && other._allUserSids != null)
        {
            if (_allUserSids.Count != other._allUserSids.Count || !_allUserSids.SetEquals(other._allUserSids))
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => obj is VirtualFilterMemoKey other && Equals(other);

    public override int GetHashCode() => _hashCode;

    public static bool operator ==(VirtualFilterMemoKey left, VirtualFilterMemoKey right) => left.Equals(right);
    public static bool operator !=(VirtualFilterMemoKey left, VirtualFilterMemoKey right) => !left.Equals(right);
}
