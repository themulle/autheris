namespace Autheris.Application.Governance.Interfaces;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

public interface IDataContractEngine
{
    Task RegisterContractAsync(DataContract contract, CancellationToken cancellationToken = default);
    Task<DataContract?> GetContractAsync(TableIdentifier table, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DataContract>> ListContractsAsync(CancellationToken cancellationToken = default);
    Task<bool> UpdateAssertionResultAsync(string contractId, string assertionId, bool isPassing, string? failureReason = null, CancellationToken cancellationToken = default);
    Task<bool> IsTableHealthyAsync(TableIdentifier table, CancellationToken cancellationToken = default);
}

public interface IDataHubCatalogClient
{
    Task<bool> IngestMetadataChangeEventAsync(string mclPayloadJson, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> FetchExternalTagsAsync(TableIdentifier table, CancellationToken cancellationToken = default);
}
