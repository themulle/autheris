namespace Autheris.Application.Policy.WriteGrants;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Tenancy;

public sealed record WriteGrantQuery(
    ClaimsPrincipal User,
    TenantId Tenant,
    TableMetadata Table,
    WriteOperation Operation,
    IReadOnlySet<string> WrittenColumns)
{
    /// <summary>Issue #1047: the caller's consents read once for a batch of decisions; null reads them per decision.</summary>
    public Autheris.Application.Policy.ConsentBatchPrefetch? ConsentPrefetch { get; init; }
}

public sealed record WriteGrantDecision(
    bool IsAllowed,
    WriteRowScope RowScope,
    IReadOnlySet<string> WritableColumns,
    long? MaxRowsPerStatement,
    IReadOnlyList<string> GrantIds,
    string? DenyCode)
{
    public static WriteGrantDecision Denied(string denyCode) =>
        new(false, WriteRowScope.OwnRows, ImmutableHashSet<string>.Empty, null, Array.Empty<string>(), denyCode);

    public static WriteGrantDecision Allowed(
        WriteRowScope rowScope,
        IReadOnlySet<string> writableColumns,
        long? maxRowsPerStatement,
        IReadOnlyList<string> grantIds) =>
        new(true, rowScope, writableColumns, maxRowsPerStatement, grantIds, null);
}

public interface IWriteGrantEvaluator
{
    Task<WriteGrantDecision> EvaluateAsync(WriteGrantQuery query, CancellationToken ct = default);
}

public interface IRowPolicyDependencyIndex
{
    bool IsPolicyDependency(TenantId tenant, TableIdentifier table);
}
