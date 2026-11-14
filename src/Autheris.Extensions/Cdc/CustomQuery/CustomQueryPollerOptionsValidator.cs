namespace Autheris.Extensions.Cdc.CustomQuery;

using System;
using System.Collections.Generic;
using System.Threading;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Streaming.Validation;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;

/// <summary>
/// Options validator for <see cref="CustomQueryPollerOptions"/> enforcing AST DQL validation,
/// supported read-only source dialects, read-only data source registration, and owner tenant consistency.
/// </summary>
public sealed class CustomQueryPollerOptionsValidator : IValidateOptions<CustomQueryPollerOptions>
{
    private readonly CustomQueryPollerValidator _pollerValidator;
    private readonly IOptionsMonitor<GatewayOptions> _gatewayOptions;
    private readonly ITableMetadataRepository? _tableMetadataRepository;

    public CustomQueryPollerOptionsValidator(
        CustomQueryPollerValidator pollerValidator,
        IOptionsMonitor<GatewayOptions> gatewayOptions,
        ITableMetadataRepository? tableMetadataRepository = null)
    {
        _pollerValidator = pollerValidator ?? throw new ArgumentNullException(nameof(pollerValidator));
        _gatewayOptions = gatewayOptions ?? throw new ArgumentNullException(nameof(gatewayOptions));
        _tableMetadataRepository = tableMetadataRepository;
    }

    public ValidateOptionsResult Validate(string? name, CustomQueryPollerOptions options)
    {
        if (options is null)
        {
            return ValidateOptionsResult.Fail("CustomQueryPollerOptions cannot be null.");
        }

        if (!options.Enabled && options.Pollers.Count == 0)
        {
            return ValidateOptionsResult.Success;
        }

        var errors = new List<string>();
        var connections = _gatewayOptions.CurrentValue.DataSources?.Connections;

        foreach (var poller in options.Pollers)
        {
            var result = _pollerValidator.ValidatePollerOptions(poller);
            if (!result.IsValid)
            {
                foreach (var err in result.Errors)
                {
                    errors.Add($"Poller '{poller?.StreamName ?? "<unnamed>"}': {err}");
                }
            }

            if (poller is null)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(poller.DataSource))
            {
                errors.Add($"Poller '{poller.StreamName}': DataSource is required.");
            }
            else if (connections is null || !connections.TryGetValue(poller.DataSource, out var dsOptions) || string.IsNullOrWhiteSpace(dsOptions.ConnectionString))
            {
                errors.Add($"Poller '{poller.StreamName}': DataSource '{poller.DataSource}' is not configured with a read connection in Gateway:DataSources:Connections.");
            }
            else if (!CustomQueryPollerValidator.TryResolveSupportedDialect(dsOptions.Provider, out _))
            {
                errors.Add($"Poller '{poller.StreamName}': DataSource '{poller.DataSource}' provider '{dsOptions.Provider}' is not a supported read-only polling dialect.");
            }

            if (_tableMetadataRepository is not null && !string.IsNullOrWhiteSpace(poller.Table))
            {
                if (!TableIdentifier.TryParse(poller.Table, out var tableId))
                {
                    errors.Add($"Poller '{poller.StreamName}': Table '{poller.Table}' is not a valid table identifier.");
                }
                else
                {
                    var tableMeta = _tableMetadataRepository.GetTableMetadataAsync(tableId, CancellationToken.None).GetAwaiter().GetResult();
                    if (tableMeta is not null && !string.IsNullOrWhiteSpace(tableMeta.OwnerTenantId))
                    {
                        if (string.IsNullOrWhiteSpace(poller.OwnerTenantId) ||
                            !string.Equals(poller.OwnerTenantId, tableMeta.OwnerTenantId, StringComparison.OrdinalIgnoreCase))
                        {
                            errors.Add($"Poller '{poller.StreamName}': OwnerTenantId does not match the registered owner tenant of table '{poller.Table}'.");
                        }
                    }
                }
            }
        }

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }
}
