namespace Autheris.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence.Catalog;
using Autheris.Infrastructure.Persistence.Dialects;
using Microsoft.Extensions.Logging;

public partial class GovernanceRepository
{
    public async Task<IReadOnlyList<TableRelation>> GetRelationsForTableAsync(TableIdentifier parentTable, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await GetRelationsForTableAsync(conn, RelationsForParentTableSql, parentTable, _logger, ct).ConfigureAwait(false);
    }

    public async Task<RelationWriteResult> CreateRelationAsync(TableRelation relation, CancellationToken ct = default)
    {
        // Rule check before any connection is opened.
        RelationWriteSupport.ValidateKeysOrThrow(relation);

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        // One connection, one explicit transaction; disposing it uncommitted (failure or cancellation) rolls back all rows.
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        var parentTableId = await ResolveActiveTableIdAsync(conn, tx, relation.RelationName, relation.ParentTableIdentifier, ct).ConfigureAwait(false);
        var childTableId = await ResolveActiveTableIdAsync(conn, tx, relation.RelationName, relation.ChildTableIdentifier, ct).ConfigureAwait(false);
        var parentKeys = RelationWriteSupport.Canonicalize(relation.RelationName, "parent", relation.JoinKeysParent, await LoadColumnNamesAsync(conn, tx, parentTableId, ct).ConfigureAwait(false));
        var childKeys = RelationWriteSupport.Canonicalize(relation.RelationName, "child", relation.JoinKeysChild, await LoadColumnNamesAsync(conn, tx, childTableId, ct).ConfigureAwait(false));

        string? existingCardinality = null;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = CatalogSql.SelectExistingRelationSql;
            cmd.AddBound("@pId", parentTableId);
            cmd.AddBound("@cId", childTableId);
            cmd.AddBound("@relName", relation.RelationName);
            var existingObj = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (existingObj is string s)
            {
                existingCardinality = s;
            }
        }

        string storedId;
        bool created;
        // Natural-key upsert: only discovery-owned columns are in the update list (I-18 keeps allow_cross_data_source out of it).
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = InsertRelationSql;
            cmd.AddBound("@id", relation.Id.ToString());
            cmd.AddBound("@pId", parentTableId);
            cmd.AddBound("@cId", childTableId);
            cmd.AddBound("@relName", relation.RelationName);
            cmd.AddBound("@card", relation.Cardinality.ToString());
            cmd.AddBound("@allowCross", IsPostgreSql ? relation.AllowCrossDataSource : (relation.AllowCrossDataSource ? 1 : 0));
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await reader.ReadAsync(ct).ConfigureAwait(false);
            storedId = reader.GetString(0);
            created = IsPostgreSql ? reader.GetBoolean(1) : string.Equals(reader.GetString(1), "INSERT", StringComparison.OrdinalIgnoreCase);
        }

        var keysChanged = await ReplaceRelationKeysAsync(conn, tx, storedId, created, parentKeys, childKeys, ct).ConfigureAwait(false);

        ct.ThrowIfCancellationRequested();
        var cardinalityUnchanged = !created && string.Equals(existingCardinality, relation.Cardinality.ToString(), StringComparison.Ordinal);
        if (cardinalityUnchanged && !keysChanged)
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            return new RelationWriteResult(Guid.Parse(storedId), false, false);
        }

        var relationScope = CatalogWriteScopeContext.TryGetActive(this);
        Autheris.Application.Catalog.CatalogVersion? relationVersion = null;
        if (relationScope is null)
        {
            relationVersion = await BumpCatalogVersionInternalAsync(conn, tx, 1, 0, ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        if (relationVersion.HasValue)
        {
            await PublishCatalogChangedEventAsync(relationVersion.Value, ct).ConfigureAwait(false);
        }
        else
        {
            // A relation add is additive: its version bump and event are coalesced by the active write scope.
            await relationScope!.RecordAdditiveWriteAsync(1, 0, ct).ConfigureAwait(false);
        }

        return new RelationWriteResult(Guid.Parse(storedId), created, keysChanged);
    }

    /// <summary>Resolves an active table id from its identifier: zero or several matches throw (no LIMIT 1).</summary>
    private static Task<string> ResolveActiveTableIdAsync(DbConnection conn, DbTransaction tx, string relationName, TableIdentifier table, CancellationToken ct)
        => ResolveActiveTableIdAsync(conn, tx, CatalogSql.ResolveActiveTableIdSql, relationName, table, ct);

    public async Task<IReadOnlyList<DataOwner>> GetDataOwnersForTableAsync(TableIdentifier table, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await GetDataOwnersForTableAsync(conn, CatalogSql.DataOwnersForTableSql, table, ct).ConfigureAwait(false);
    }

    public Task<bool> IsAuthorizedApproverForTableAsync(TableIdentifier table, Sid approverSid, CancellationToken ct = default) =>
        IsAuthorizedApproverForTableInternalAsync(table, approverSid, null, ct);

    public Task<bool> IsAuthorizedApproverForTableAsync(TableIdentifier table, Sid approverSid, string? itsmApproverAccount, CancellationToken ct = default) =>
        IsAuthorizedApproverForTableInternalAsync(table, approverSid, itsmApproverAccount, ct);

    public async Task<IReadOnlySet<string>> GetTransitiveRolesAsync(Sid subjectSid, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await GetTransitiveRolesAsync(conn, subjectSid, ct).ConfigureAwait(false);
    }

    public async Task<DataOwnerDelegation> DelegateDataOwnershipAsync(DataOwnerDelegation delegation, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = InsertDelegationSql;
            cmd.AddBound("@id", delegation.Id.ToString());
            cmd.AddBound("@ownerId", delegation.DataOwnerId.ToString());
            cmd.AddBound("@delegateSid", delegation.DelegateSid.Value);
            cmd.AddBound("@validFrom", delegation.ValidFrom.ToString("O"));
            cmd.AddBound("@validTo", delegation.ValidTo.ToString("O"));
            cmd.AddBound("@reason", delegation.Reason);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            var newVersion = await BumpCatalogVersionInternalAsync(conn, tx, 0, 1, ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            await PublishCatalogChangedEventAsync(newVersion, ct).ConfigureAwait(false);
            await FlushScopePendingAsync(ct).ConfigureAwait(false);
            return delegation;
        }
        catch
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<bool> SetRelationCrossDataSourceOptInAsync(Guid relationId, bool allowCrossDataSource, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            var updated = await UpdateRelationCrossDataSourceAsync(conn, tx, relationId, IsPostgreSql ? allowCrossDataSource : (allowCrossDataSource ? 1 : 0), ct).ConfigureAwait(false);
            if (updated)
            {
                var newVersion = await BumpCatalogVersionInternalAsync(conn, tx, 1, 0, ct).ConfigureAwait(false);
                await tx.CommitAsync(ct).ConfigureAwait(false);
                await PublishCatalogChangedEventAsync(newVersion, ct).ConfigureAwait(false);
                await FlushScopePendingAsync(ct).ConfigureAwait(false);
                return true;
            }

            await tx.RollbackAsync(ct).ConfigureAwait(false);
            return false;
        }
        catch
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<Autheris.Application.Catalog.CatalogVersion> GetCatalogVersionAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await GetCatalogVersionAsync(conn, CatalogVersionSql, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TableRelation>> GetAllRelationsAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await GetAllRelationsAsync(conn, AllRelationsSql, _logger, ct).ConfigureAwait(false);
    }

    private Task FlushScopePendingAsync(CancellationToken ct) =>
        CatalogWriteScopeContext.TryGetActive(this)?.FlushPendingAsync(ct) ?? Task.CompletedTask;

    /// <summary>
    /// Flushes one catalog version bump and its <c>catalog:changed</c> event in its own short transaction. Used by catalog
    /// write scopes (chunk and end-of-scope flush) and by the snapshot poll to repair a failed flush.
    /// </summary>
    public async Task FlushCatalogVersionAsync(long structureDelta, long contentDelta, CancellationToken ct = default)
    {
        Autheris.Application.Catalog.CatalogVersion version;
        await using (var conn = await OpenConnectionAsync(ct).ConfigureAwait(false))
        {
            await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
            {
                version = await BumpCatalogVersionInternalAsync(conn, tx, structureDelta, contentDelta, ct).ConfigureAwait(false);
                await tx.CommitAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                throw;
            }
        }

        await PublishCatalogChangedEventAsync(version, ct).ConfigureAwait(false);
    }

    private async Task<Autheris.Application.Catalog.CatalogVersion> BumpCatalogVersionInternalAsync(
        DbConnection conn,
        DbTransaction tx,
        long structureDelta,
        long contentDelta,
        CancellationToken ct)
    {
        ValidateVersionDeltas(structureDelta, contentDelta);

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = BumpCatalogVersionSql;
        cmd.AddBound("@structureDelta", structureDelta);
        cmd.AddBound("@contentDelta", contentDelta);
        cmd.AddBound("@now", TimeProvider.System.GetUtcNow(), System.Data.DbType.DateTimeOffset);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return new Autheris.Application.Catalog.CatalogVersion(reader.GetInt64(0), reader.GetInt64(1));
        }
        return new Autheris.Application.Catalog.CatalogVersion(1, 1);
    }
}
