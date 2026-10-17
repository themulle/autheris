namespace Autheris.Application.SqlEndpoints.Services;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.ListParameters;
using Autheris.Application.SqlEndpoints.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;
using TrinoSqlEngine.Analysis;
using TrinoSqlEngine.Ast.Emit;

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
        var listLimits = ListParameterLimits.From(_options.Value.WebSql ?? new WebSqlOptions());

        foreach (var paramDef in endpoint.Parameters)
        {
            object? val = null;

            // A JSON null of a list parameter is "not supplied" (default or required rule); scalars keep their behaviour.
            if (inputs.TryGetValue(paramDef.Name, out var providedVal) && providedVal != null
                && !(paramDef.IsList && providedVal is JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined }))
            {
                val = paramDef.IsList
                    ? CoerceList(providedVal, paramDef, listLimits)
                    : ListParameterNormalizer.IsListCandidate(providedVal)
                        ? throw new ClientListException(GovernedSqlErrorCodes.ListParamType)   // a list for a scalar parameter: never echoed
                        : CoerceValue(providedVal, paramDef.ClrType, paramDef.Name);
            }
            else if (paramDef.DefaultValue != null)
            {
                val = paramDef.IsList ? CoerceList(paramDef.DefaultValue, paramDef, listLimits) : paramDef.DefaultValue;
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
            // Governed execution parses a single statement and rejects comments (SQ-02): the endpoint file's
            // annotation header (-- @name ...) and trailing ';' are only meaningful to the loader.
            Sql: SqlParameterExtractor.StripComments(endpoint.RawSql).Trim().TrimEnd(';').Trim(),
            Parameters: validatedParams,
            DataSourceName: endpoint.DataSource,
            RowLimit: SqlRowLimit.For(_options.Value.WebSql ?? new WebSqlOptions(), _options.Value.RowLimits?.SqlEndpoints),
            Transport: SqlTransports.SqlEndpoints);

        _logger?.LogDebug("Executing declarative SQL endpoint '{EndpointName}' with {ParamCount} parameters.", endpointName, endpoint.Parameters.Count);

        return await _governedSql.ExecuteQueryBufferedAsync(request, user, tenantId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A list-declared parameter accepts a JSON array (POST), a collection (a repeated GET key) or one value, which is a one-element list: a
    /// comma-separated value is one string and is never split. Elements are coerced by the normalizer, which throws only
    /// <see cref="ClientListException"/> (no value, no inner message): the scalar message of <see cref="CoerceValue"/> echoes the value and is not used.
    /// </summary>
    private static ClientListValue CoerceList(object provided, SqlEndpointParameter definition, ListParameterLimits limits)
    {
        var elementType = ListElementTypes.For(definition.ClrType, out bool guidStrings);
        object raw = provided is ClientListValue existing
            ? throw new ClientListException(GovernedSqlErrorCodes.ListParamType)   // the caller never supplies a ready-made list value
            : ListParameterNormalizer.IsListCandidate(provided) ? provided : new[] { provided };
        return ListParameterNormalizer.Normalize(raw, limits, elementType, guidStrings);
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
