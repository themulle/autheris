namespace Autheris.Application.Governance.Services;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

public sealed class InMemoryDataContractEngine : IDataContractEngine
{
    private readonly ConcurrentDictionary<string, DataContract> _contractsByTable = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DataContract> _contractsById = new(StringComparer.OrdinalIgnoreCase);

    public Task RegisterContractAsync(DataContract contract, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contract);
        _contractsById[contract.Id] = contract;
        _contractsByTable[contract.Table.ToString()] = contract;
        return Task.CompletedTask;
    }

    public Task<DataContract?> GetContractAsync(TableIdentifier table, CancellationToken cancellationToken = default)
    {
        _contractsByTable.TryGetValue(table.ToString(), out var contract);
        return Task.FromResult(contract);
    }

    public Task<IReadOnlyList<DataContract>> ListContractsAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<DataContract>>(_contractsById.Values.OrderBy(c => c.Table.ToString()).ToList());
    }

    public Task<bool> UpdateAssertionResultAsync(
        string contractId,
        string assertionId,
        bool isPassing,
        string? failureReason = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contractId);
        ArgumentException.ThrowIfNullOrWhiteSpace(assertionId);

        if (!_contractsById.TryGetValue(contractId, out var existing))
        {
            return Task.FromResult(false);
        }

        var updatedAssertions = existing.Assertions.Select(a =>
        {
            if (string.Equals(a.Id, assertionId, StringComparison.OrdinalIgnoreCase))
            {
                return a with
                {
                    IsPassing = isPassing,
                    FailureReason = failureReason,
                    LastEvaluated = DateTimeOffset.UtcNow
                };
            }
            return a;
        }).ToList();

        var newStatus = updatedAssertions.All(a => a.IsPassing) ? DataContractStatus.Active : DataContractStatus.Broken;

        var updatedContract = existing with
        {
            Assertions = updatedAssertions,
            Status = newStatus,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        _contractsById[contractId] = updatedContract;
        _contractsByTable[updatedContract.Table.ToString()] = updatedContract;

        return Task.FromResult(true);
    }

    public Task<bool> IsTableHealthyAsync(TableIdentifier table, CancellationToken cancellationToken = default)
    {
        if (_contractsByTable.TryGetValue(table.ToString(), out var contract))
        {
            return Task.FromResult(contract.IsHealthy);
        }

        // Tables without explicit contracts are considered healthy by default
        return Task.FromResult(true);
    }
}
