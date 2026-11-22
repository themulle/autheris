using Autheris.Domain.Common;
using Autheris.Domain.Model;

namespace Autheris.Application.Common.Interfaces;

/// <summary>Provides cached, epoch-coherent effective protection snapshots for governed tables.</summary>
public interface IProtectionSnapshotProvider
{
    ValueTask<EffectiveProtection> GetAsync(TenantId tenant, TableIdentifier table, CancellationToken ct = default);
}
