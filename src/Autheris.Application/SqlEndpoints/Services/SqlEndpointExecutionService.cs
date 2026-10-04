namespace Autheris.Application.SqlEndpoints.Services;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.SqlEndpoints.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public sealed class SqlEndpointExecutionService : ISqlEndpointExecutionService
{
    private readonly ISqlEndpointRegistry _registry;
    private readonly IGovernedSqlExecutionService _governedSql;
    private readonly IOptions<GatewayOptions> _options;
    private readonly ILogger<SqlEndpointExecutionService>? _logger;

    public SqlEndpointExecutionService(
        ISqlEndpointRegistry registry,
        IGovernedSqlExecutionService governedSql,
        IOptions<GatewayOptions> options,
        ILogger<SqlEndpointExecutionService>? logger = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _governedSql = governedSql ?? throw new ArgumentNullException(nameof(governedSql));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
    }

    public async Task<GovernedSqlResult> ExecuteEndpointAsync(
        string endpointName,
        IReadOnlyDictionary<string, object?>? rawInputs,
        ClaimsPrincipal user,
        TenantId tenantId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointName);
        ArgumentNullException.ThrowIfNull(user);

        if (!_registry.TryGet(endpointName, out var endpoint) || endpoint == null)
        {
            throw new KeyNotFoundException($"Declarative SQL endpoint '{endpointName}' is not registered.");
        }

        var validatedParams = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var inputs = rawInputs ?? new Dictionary<string, object?>();

        foreach (var paramDef in endpoint.Parameters)
        {
            object? val = null;

            if (inputs.TryGetValue(paramDef.Name, out var providedVal) && providedVal != null)
            {
                val = CoerceValue(providedVal, paramDef.ClrType, paramDef.Name);
            }
            else if (paramDef.DefaultValue != null)
            {
                val = paramDef.DefaultValue;
            }
            else if (paramDef.IsRequired)
            {
                throw new ArgumentException(
                    $"Missing required parameter '{paramDef.Name}' (type {paramDef.ClrType.Name}) for endpoint '{endpointName}'.",
                    paramDef.Name);
            }

            validatedParams[paramDef.Name] = val;
            validatedParams["@" + paramDef.Name] = val;
        }

        var request = new GovernedSqlQueryRequest(
            Sql: endpoint.RawSql,
            Parameters: validatedParams,
            DataSourceName: endpoint.DataSource);

        _logger?.LogDebug("Executing declarative SQL endpoint '{EndpointName}' with {ParamCount} parameters.", endpointName, endpoint.Parameters.Count);

        return await _governedSql.ExecuteQueryBufferedAsync(request, user, tenantId, ct).ConfigureAwait(false);
    }

    private static object? CoerceValue(object val, Type targetType, string paramName)
    {
        if (val == null) return null;
        if (targetType.IsInstanceOfType(val)) return val;

        string s = val.ToString() ?? string.Empty;

        try
        {
            if (targetType == typeof(int)) return int.Parse(s, CultureInfo.InvariantCulture);
            if (targetType == typeof(long)) return long.Parse(s, CultureInfo.InvariantCulture);
            if (targetType == typeof(decimal)) return decimal.Parse(s, CultureInfo.InvariantCulture);
            if (targetType == typeof(double)) return double.Parse(s, CultureInfo.InvariantCulture);
            if (targetType == typeof(bool)) return bool.Parse(s);
            if (targetType == typeof(DateTimeOffset)) return DateTimeOffset.Parse(s, CultureInfo.InvariantCulture);
            if (targetType == typeof(DateTime)) return DateTime.Parse(s, CultureInfo.InvariantCulture);
            if (targetType == typeof(Guid)) return Guid.Parse(s);
            if (targetType == typeof(string)) return s;

            return Convert.ChangeType(val, targetType, CultureInfo.InvariantCulture);
        }
        catch (Exception ex)
        {
            throw new ArgumentException(
                $"Parameter '{paramName}' with value '{s}' cannot be converted to expected type '{targetType.Name}': {ex.Message}",
                paramName,
                ex);
        }
    }
}
