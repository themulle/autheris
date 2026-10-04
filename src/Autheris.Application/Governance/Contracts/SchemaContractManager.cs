namespace Autheris.Application.Governance.Contracts;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// F-GOV-08: In-memory registry and lifecycle manager for dynamic schema contracts.
/// Translates configuration options into compiled contract slices.
/// </summary>
public sealed class SchemaContractManager : ISchemaContractManager
{
    private readonly IOptions<GatewayOptions> _gatewayOptions;
    private readonly ILogger<SchemaContractManager> _logger;
    private readonly ConcurrentDictionary<string, SchemaContractDefinition> _contracts = new(StringComparer.OrdinalIgnoreCase);

    public SchemaContractManager(
        IOptions<GatewayOptions> gatewayOptions,
        ILogger<SchemaContractManager> logger)
    {
        _gatewayOptions = gatewayOptions ?? throw new ArgumentNullException(nameof(gatewayOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        InitializeContracts();
    }

    public bool IsEnabled => _gatewayOptions.Value.SchemaContracts.Enabled;
    public string DefaultContract => _gatewayOptions.Value.SchemaContracts.DefaultContract;

    public bool HasContract(string contractName)
    {
        return _contracts.ContainsKey(contractName);
    }

    public IReadOnlyList<string> GetAvailableContracts()
    {
        return _contracts.Keys.ToList();
    }

    public SchemaContractDefinition? GetContract(string contractName)
    {
        return _contracts.TryGetValue(contractName, out var def) ? def : null;
    }

    public string FilterSchemaSdl(string originalSdl, SchemaContractDefinition contract)
    {
        return SchemaContractFilter.FilterSchema(originalSdl, contract);
    }

    public void RegisterContract(SchemaContractDefinition contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        _contracts[contract.Name] = contract;
    }

    private void InitializeContracts()
    {
        var options = _gatewayOptions.Value.SchemaContracts;

        foreach (var (name, opt) in options.Contracts)
        {
            var def = new SchemaContractDefinition(
                name: name,
                includedTags: opt.IncludedTags,
                excludedTags: opt.ExcludedTags,
                excludeInaccessible: opt.ExcludeInaccessible
            );
            _contracts[name] = def;
        }

        // Always register default baseline contract if not specified
        if (!_contracts.ContainsKey("default"))
        {
            _contracts["default"] = new SchemaContractDefinition("default", excludeInaccessible: true);
        }

        _logger.LogInformation(
            "F-GOV-08 SchemaContractManager initialized with {Count} contracts: [{Contracts}]",
            _contracts.Count, string.Join(", ", _contracts.Keys));
    }
}
