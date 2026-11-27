namespace Autheris.Application.Sql.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autheris.Application.Sql.Errors;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Internal;
using Autheris.Application.Sql.ListParameters;
using Microsoft.Extensions.Options;
using TrinoSqlEngine.Ast.Emit;

public sealed partial class GovernedSqlExecutor
{
    private IReadOnlyDictionary<string, object?> BuildClientValues(GovernedSqlQueryRequest request, IReadOnlySet<string> referenced)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var parameters = request.Parameters;
        if (parameters != null)
        {
            AddClientValues(request, parameters, referenced, values);
        }

        // Issue #280: gateway-generated parameters of a planned read (typed scalars, validated by MergeGatewayParameters). Add, not the
        // indexer: a duplicate name throws instead of overwriting a client value.
        if (request.GatewayParameters is { } gatewayParameters)
        {
            foreach (var (name, value) in gatewayParameters)
            {
                values.Add(GovernedSqlExecutionService.GatewayParameterAlias(name), value);
            }
        }

        return values;
    }

    private void AddClientValues(
        GovernedSqlQueryRequest request,
        IReadOnlyDictionary<string, object?> parameters,
        IReadOnlySet<string> referenced,
        Dictionary<string, object?> values)
    {
        var limits = ListParameterLimits.From(_options.Value.WebSql);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int lists = 0;
        foreach (var (name, value) in parameters)
        {
            // The first spelling wins when two names differ only by case (as before).
            string trimmed = name.TrimStart('@');
            if (Autheris.Application.Identity.Attributes.CallerAttributeParameterName.IsReserved(trimmed))
            {
                // 129 (BC-129-16, ST-129-14): a client parameter never uses the reserved name of a caller attribute slot (any case).
                throw new ArgumentException("A client parameter must not use a reserved gateway parameter name.");
            }

            if (!seen.Add(trimmed))
            {
                continue;
            }

            if (value is not ClientListValue && !ListParameterNormalizer.IsListCandidate(value))
            {
                values.Add(trimmed, WebSqlParameterValues.Normalize(value));
                continue;
            }

            // admission by allowlist, before any element is read.
            AdmitList(request, value);
            if (++lists > limits.MaxListParameters)
            {
                throw new ClientListException(GovernedSqlErrorCodes.ListParamTooLarge);
            }

            if (!referenced.Contains(trimmed))
            {
                continue;   // dropped: it neither enters the cache key nor costs a normalization
            }

            values.Add(trimmed, value switch
            {
                // An endpoint-built value is not trusted: the configured maximum applies again (its elements were validated by Create).
                ClientListValue list when list.Count > limits.ConfiguredMaxValues => throw new ClientListException(GovernedSqlErrorCodes.ListParamTooLarge),
                ClientListValue list => list,
                _ => ListParameterNormalizer.Normalize(value!, limits, null)
            });
        }
    }

    /// <summary>
    /// a list value is admitted only on WebSql, Trino and Mcp (a JSON array) or SqlEndpoints (an already normalized
    /// <see cref="ClientListValue"/>), never on a planned table read, never with another transport or none, never as a loose collection.
    /// </summary>
    private static void AdmitList(GovernedSqlQueryRequest request, object? value)
    {
        string? transport = request.Transport;
        bool admitted = !request.PlannedTableRead && value switch
        {
            JsonElement => transport is SqlTransports.WebSql or SqlTransports.Trino or SqlTransports.Mcp,
            ClientListValue => transport is SqlTransports.SqlEndpoints,
            _ => false
        };
        if (!admitted)
        {
            throw new ClientListException(GovernedSqlErrorCodes.ListParamPosition);
        }
    }

    private static void AddCallerAttributeAuditFields(Dictionary<string, object?> fields, GovernedRewrite rewrite, CompiledSql compiled)
    {
        if (rewrite.CallerAttributes is not { Count: > 0 } attributes)
        {
            return;
        }

        fields["caller_attributes"] = attributes.ToDictionary(
            p => p.Key,
            p => p.Value.Select(a => new Dictionary<string, object?> { ["id"] = a.Id, ["outcome"] = a.Outcome, ["reason"] = a.Reason }).ToList(),
            StringComparer.OrdinalIgnoreCase);
        fields["caller_attribute_binds"] = compiled.Parameters.Count(p => p.Origin == ParameterOrigin.Policy && p.SourceName != null && Autheris.Application.Identity.Attributes.CallerAttributeParameterName.IsReserved(p.SourceName));
    }
}
