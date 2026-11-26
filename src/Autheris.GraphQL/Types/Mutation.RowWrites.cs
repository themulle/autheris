namespace Autheris.GraphQL.Types;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Writes;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using HotChocolate;
using Microsoft.AspNetCore.Http;

public sealed record RowFieldInput(
    string Column,
    string? StringValue = null,
    long? IntValue = null,
    double? FloatValue = null,
    bool? BoolValue = null,
    bool IsNull = false);

public sealed record RowWritePayload(
    string Dataset,
    string Operation,
    int AffectedRows,
    bool Replayed,
    string IdempotencyKey);

public sealed record ServerStampedColumnInput(
    string Column,
    string StampKind);

public sealed record SetTableWritePolicyInput(
    string Dataset,
    string Mode,
    IReadOnlyList<string>? AllowedOperations = null,
    bool RequirePrimaryKeyPredicate = true,
    bool RequireRowFilter = false,
    long? MaxRowsPerStatement = 100,
    IReadOnlyList<string>? WritableColumns = null,
    IReadOnlyList<ServerStampedColumnInput>? ServerStampedColumns = null);

public sealed record TableWritePolicyPayload(
    string Dataset,
    string Mode,
    IReadOnlyList<string> AllowedOperations,
    bool RequirePrimaryKeyPredicate,
    bool RequireRowFilter,
    long? MaxRowsPerStatement,
    IReadOnlyList<string>? WritableColumns,
    DateTimeOffset UpdatedAtUtc);

public sealed partial class Mutation
{
    public Task<RowWritePayload> InsertDatasetRowAsync(
        string dataset,
        IReadOnlyList<RowFieldInput> row,
        string idempotencyKey,
        [Service] IBoundedRowWriteService rowWriteService,
        [Service] IHttpContextAccessor httpContextAccessor,
        HotChocolate.Resolvers.IResolverContext? resolverContext = null,
        CancellationToken ct = default) =>
        ExecuteRowWriteAsync(
            dataset,
            httpContextAccessor,
            resolverContext,
            (principal, tenantId, table, token) =>
                rowWriteService.InsertRowAsync(principal, tenantId, table, ToFieldMap(row), idempotencyKey, token),
            ct);

    public Task<RowWritePayload> UpsertDatasetRowAsync(
        string dataset,
        IReadOnlyList<RowFieldInput> row,
        string idempotencyKey,
        [Service] IBoundedRowWriteService rowWriteService,
        [Service] IHttpContextAccessor httpContextAccessor,
        HotChocolate.Resolvers.IResolverContext? resolverContext = null,
        CancellationToken ct = default) =>
        ExecuteRowWriteAsync(
            dataset,
            httpContextAccessor,
            resolverContext,
            (principal, tenantId, table, token) =>
                rowWriteService.UpsertRowAsync(principal, tenantId, table, ToFieldMap(row), idempotencyKey, token),
            ct);

    public Task<RowWritePayload> UpdateDatasetRowByPkAsync(
        string dataset,
        IReadOnlyList<RowFieldInput> key,
        IReadOnlyList<RowFieldInput> patch,
        string idempotencyKey,
        [Service] IBoundedRowWriteService rowWriteService,
        [Service] IHttpContextAccessor httpContextAccessor,
        HotChocolate.Resolvers.IResolverContext? resolverContext = null,
        CancellationToken ct = default) =>
        ExecuteRowWriteAsync(
            dataset,
            httpContextAccessor,
            resolverContext,
            (principal, tenantId, table, token) =>
                rowWriteService.UpdateRowByPkAsync(principal, tenantId, table, ToFieldMap(key), ToFieldMap(patch), idempotencyKey, token),
            ct);

    public Task<RowWritePayload> DeleteDatasetRowByPkAsync(
        string dataset,
        IReadOnlyList<RowFieldInput> key,
        string idempotencyKey,
        [Service] IBoundedRowWriteService rowWriteService,
        [Service] IHttpContextAccessor httpContextAccessor,
        HotChocolate.Resolvers.IResolverContext? resolverContext = null,
        CancellationToken ct = default) =>
        ExecuteRowWriteAsync(
            dataset,
            httpContextAccessor,
            resolverContext,
            (principal, tenantId, table, token) =>
                rowWriteService.DeleteRowByPkAsync(principal, tenantId, table, ToFieldMap(key), idempotencyKey, token),
            ct);

    public async Task<TableWritePolicyPayload> SetTableWritePolicyAsync(
        SetTableWritePolicyInput input,
        [Service] TableWritePolicyService policyService,
        [Service] IHttpContextAccessor httpContextAccessor,
        HotChocolate.Resolvers.IResolverContext? resolverContext = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        _ = GetAuthenticatedUserSid(httpContextAccessor, resolverContext);
        var principal = Autheris.GraphQL.Security.GraphQlCallerPrincipal.Resolve(resolverContext, httpContextAccessor);
        var tenantId = ResolveRowWriteTenantId(httpContextAccessor, principal, resolverContext);

        if (!TableIdentifier.TryParse(input.Dataset?.Trim(), out var table))
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("VALIDATION_ERROR")
                .SetMessage("Dataset identifier must have the form 'domain.schema.table'.")
                .Build());
        }

        if (!Enum.TryParse<TableWriteMode>(input.Mode?.Trim(), ignoreCase: true, out var mode))
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("WRITE_POLICY_INVALID")
                .SetMessage("Unsupported table write mode.")
                .Build());
        }

        var ops = ParseWriteOperations(input.AllowedOperations);
        IReadOnlyDictionary<string, string>? stamped = null;
        if (input.ServerStampedColumns is { Count: > 0 })
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in input.ServerStampedColumns)
            {
                if (string.IsNullOrWhiteSpace(entry.Column) || string.IsNullOrWhiteSpace(entry.StampKind))
                {
                    throw new GraphQLException(ErrorBuilder.New()
                        .SetCode("WRITE_POLICY_INVALID")
                        .SetMessage("Server-stamped column entries require column name and stamp kind.")
                        .Build());
                }

                map[entry.Column.Trim()] = entry.StampKind.Trim();
            }

            stamped = map;
        }

        var caller = SecurityPrincipalContext.FromPrincipal(principal, tenantId);
        var result = await policyService.UpsertAsync(
            caller,
            tenantId,
            new UpsertTableWritePolicyInput(
                DatasetId: table.ToString(),
                Mode: mode,
                AllowedOperations: ops,
                WritableColumns: input.WritableColumns,
                RequirePrimaryKeyPredicate: input.RequirePrimaryKeyPredicate,
                RequireRowFilter: input.RequireRowFilter,
                MaxRowsPerStatement: input.MaxRowsPerStatement,
                ServerStampedColumns: stamped),
            ct).ConfigureAwait(false);

        if (result.Status == TableWritePolicyStatus.Forbidden)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("FORBIDDEN")
                .SetMessage("GovernanceAdmin role is required to manage table write policies.")
                .Build());
        }

        if (result.Status == TableWritePolicyStatus.NotFound)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("TARGET_NOT_FOUND")
                .SetMessage("The target dataset was not found.")
                .Build());
        }

        if (result.Status != TableWritePolicyStatus.Ok || result.Value is null)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("WRITE_POLICY_INVALID")
                .SetMessage("The table write policy configuration is invalid.")
                .Build());
        }

        var saved = result.Value;
        return new TableWritePolicyPayload(
            Dataset: table.ToString(),
            Mode: saved.Mode.ToString(),
            AllowedOperations: saved.AllowedOperations.Select(static o => o.ToString()).ToArray(),
            RequirePrimaryKeyPredicate: saved.EffectiveRequirePrimaryKeyPredicate,
            RequireRowFilter: saved.RequireRowFilter,
            MaxRowsPerStatement: saved.MaxRowsPerStatement,
            WritableColumns: saved.WritableColumns,
            UpdatedAtUtc: saved.UpdatedAt);
    }

    private static async Task<RowWritePayload> ExecuteRowWriteAsync(
        string dataset,
        IHttpContextAccessor httpContextAccessor,
        HotChocolate.Resolvers.IResolverContext? resolverContext,
        Func<System.Security.Claims.ClaimsPrincipal, TenantId, TableIdentifier, CancellationToken, Task<BoundedRowWriteResult>> action,
        CancellationToken ct)
    {
        _ = GetAuthenticatedUserSid(httpContextAccessor, resolverContext);
        var principal = Autheris.GraphQL.Security.GraphQlCallerPrincipal.Resolve(resolverContext, httpContextAccessor);
        if (principal.IsReadOnly())
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("READ_ONLY_TOKEN")
                .SetMessage("Read-only tokens cannot execute write mutations.")
                .Build());
        }

        var tenantId = ResolveRowWriteTenantId(httpContextAccessor, principal, resolverContext);
        if (!TableIdentifier.TryParse(dataset?.Trim(), out var table))
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("VALIDATION_ERROR")
                .SetMessage("Dataset identifier must have the form 'domain.schema.table'.")
                .Build());
        }

        try
        {
            var result = await action(principal, tenantId, table, ct).ConfigureAwait(false);
            return new RowWritePayload(
                result.DatasetId,
                result.Operation,
                result.AffectedRows,
                result.Replayed,
                result.IdempotencyKey);
        }
        catch (BoundedRowWriteException ex)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode(ex.Code)
                .SetMessage(ex.Message)
                .Build());
        }
    }

    internal static IReadOnlyDictionary<string, object?> ToFieldMap(IReadOnlyList<RowFieldInput>? fields)
    {
        if (fields is null || fields.Count == 0)
        {
            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        }

        var map = new Dictionary<string, object?>(fields.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var field in fields)
        {
            if (string.IsNullOrWhiteSpace(field.Column))
            {
                throw new GraphQLException(ErrorBuilder.New()
                    .SetCode("VALIDATION_ERROR")
                    .SetMessage("Row field column name is required.")
                    .Build());
            }

            var col = field.Column.Trim();
            if (map.ContainsKey(col))
            {
                throw new GraphQLException(ErrorBuilder.New()
                    .SetCode("VALIDATION_ERROR")
                    .SetMessage("Duplicate column in row write payload.")
                    .Build());
            }

            int specified =
                (field.IsNull ? 1 : 0) +
                (field.StringValue is not null ? 1 : 0) +
                (field.IntValue.HasValue ? 1 : 0) +
                (field.FloatValue.HasValue ? 1 : 0) +
                (field.BoolValue.HasValue ? 1 : 0);
            if (specified != 1)
            {
                throw new GraphQLException(ErrorBuilder.New()
                    .SetCode("VALIDATION_ERROR")
                    .SetMessage("Each row field must specify exactly one typed value or isNull=true.")
                    .Build());
            }

            object? val = field.IsNull
                ? null
                : field.StringValue is not null
                    ? field.StringValue
                    : field.IntValue.HasValue
                        ? field.IntValue.Value
                        : field.FloatValue.HasValue
                            ? field.FloatValue.Value
                            : field.BoolValue!.Value;

            map[col] = val;
        }

        return map;
    }

    private static TenantId ResolveRowWriteTenantId(
        IHttpContextAccessor? httpContextAccessor,
        System.Security.Claims.ClaimsPrincipal principal,
        HotChocolate.Resolvers.IResolverContext? resolverContext)
    {
        var contextTenant = GetContextTenantId(httpContextAccessor, resolverContext);
        if (!string.IsNullOrWhiteSpace(contextTenant.Value))
        {
            return contextTenant;
        }

        return principal.GetTenantId();
    }

    private static IReadOnlyList<WriteOperation> ParseWriteOperations(IReadOnlyList<string>? names)
    {
        if (names is null || names.Count == 0)
        {
            return Array.Empty<WriteOperation>();
        }

        var result = new List<WriteOperation>(names.Count);
        foreach (var raw in names)
        {
            if (string.IsNullOrWhiteSpace(raw) || !Enum.TryParse<WriteOperation>(raw.Trim(), ignoreCase: true, out var op))
            {
                throw new GraphQLException(ErrorBuilder.New()
                    .SetCode("WRITE_POLICY_INVALID")
                    .SetMessage("Unsupported write operation in table write policy.")
                    .Build());
            }

            result.Add(op);
        }

        return result;
    }
}
