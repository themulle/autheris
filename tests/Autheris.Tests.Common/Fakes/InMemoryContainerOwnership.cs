using Autheris.Application.Governance.Ownership;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

namespace Autheris.Tests.Common.Fakes;

/// <summary>
/// In-memory <see cref="IContainerOwnership"/> for unit tests: an owner entry stands for the owner group of the table's container.
/// </summary>
public sealed class InMemoryContainerOwnership : IContainerOwnership
{
    private readonly object _gate = new();
    private readonly Dictionary<TableIdentifier, List<ContainerOwnerEntry>> _owners = new();

    public InMemoryContainerOwnership AssignOwner(TableIdentifier table, ContainerOwnerEntry owner)
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

    public Task<IReadOnlyList<ContainerOwnerEntry>> GetDataOwnersForTableAsync(TableIdentifier table, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<ContainerOwnerEntry>>(
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

            return Task.FromResult(list.Any(o => o.IsActive && o.AdSid == approverSid));
        }
    }
}
