namespace Autheris.Application.Governance.Contracts;

using System.Collections.Generic;

/// <summary>
/// F-GOV-08: Service interface for managing dynamic schema contracts.
/// Filters the central supergraph into contract slices for partners, mobile, and public APIs.
/// </summary>
public interface ISchemaContractManager
{
    bool IsEnabled { get; }
    string DefaultContract { get; }

    bool HasContract(string contractName);
    IReadOnlyList<string> GetAvailableContracts();
    SchemaContractDefinition? GetContract(string contractName);
    string FilterSchemaSdl(string originalSdl, SchemaContractDefinition contract);
}
