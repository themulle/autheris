using Autheris.Application.Governance.Ownership;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

namespace Autheris.Tests.Common.Fakes;

/// <summary>
/// In-memory <see cref="IContainerOwnership"/> for unit tests.
/// </summary>
public sealed class InMemoryDataOwnershipRepository : IContainerOwnership
{
    private readonly object _gate = new();
    private readonly Dictionary<TableIdentifier, List<DataOwner>> _owners = new();
    private readonly List<DataOwnerDelegation> _delegations = [];

    public InMemoryDataOwnershipRepository AssignOwner(TableIdentifier table, DataOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        lock (_gate)
        {
            if (!_owners.TryGetValue(table, out var list))
            {
                list = [];
                _owners[table] = list;
            }

            list.Add(owner);
        }

        return this;
    }

    public Task<DataOwnerDelegation> DelegateDataOwnershipAsync(DataOwnerDelegation delegation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(delegation);
        lock (_gate)
        {
            _delegations.Add(delegation);
        }

        return Task.FromResult(delegation);
    }

    public Task<IReadOnlyList<DataOwner>> GetDataOwnersForTableAsync(TableIdentifier table, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<DataOwner>>(
                _owners.TryGetValue(table, out var list) ? list.ToArray() : []);
        }
    }

    public Task<bool> IsAuthorizedApproverForTableAsync(TableIdentifier table, Sid approverSid, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_owners.TryGetValue(table, out var list))
            {
                return Task.FromResult(false);
            }

            if (list.Any(o => o.IsActive && o.AdSid == approverSid))
            {
                return Task.FromResult(true);
            }

            var ownerIds = list.Where(o => o.IsActive).Select(o => o.Id).ToHashSet();
            var now = DateTimeOffset.UtcNow;
            var delegated = _delegations.Any(d => ownerIds.Contains(d.DataOwnerId) && d.DelegateSid == approverSid && d.IsActive(now));
            return Task.FromResult(delegated);
        }
    }
}
