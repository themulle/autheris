using System.Data.Common;
using System.Globalization;
using Autheris.Domain.Common;
using Autheris.Domain.Diagnostics;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

namespace Autheris.Infrastructure.Persistence;

/// <summary>
/// Issue #111 (PLAN-111 A-1, A-2, A-6, A-10): shared read/write of the persisted dataset catalog tags and visibility tier for all governance
/// engines. Tags (<c>TABLE_TAGS</c>) and the tier (<c>TABLE_CATALOG_SETTINGS</c>) are separate from <c>TABLES</c> because ingestion upserts
/// rewrite it. They are never stored in the metadata cache: the repositories overlay a fresh read on every call.
/// Read side (catalog mode): every failure degrades to the secure value (Hidden, no tags) and is counted, never thrown; strict mode (admin reads)
/// throws instead. Write side: the callers own the transaction and the cache invalidation; no epoch is bumped (tags are not policy inputs).
/// </summary>
internal static class DatasetCatalogStore
{
    internal const string TagsTable = "TABLE_TAGS";
    internal const string SettingsTable = "TABLE_CATALOG_SETTINGS";
    internal const string SystemDefaultActor = "system:default";
    private const string AdminSource = "admin";
    private const string DbtSource = "dbt";

    /// <summary>The effective settings of one table after validation; <see cref="Degraded"/> rows carry the secure value.</summary>
    internal readonly record struct RowOverlay(DatasetVisibility Visibility, IReadOnlyList<DatasetTag> Tags, bool Degraded)
    {
        internal static readonly RowOverlay Secure = new(DatasetVisibility.Hidden, [], true);
        internal static readonly RowOverlay Empty = new(DatasetVisibility.Hidden, [], false);
        internal IReadOnlyList<string> TagNames => Tags.Select(t => t.Tag).ToList();
    }

    /// <summary>All validated rows of one read; a table without any row is <see cref="RowOverlay.Empty"/> (Hidden, no tags).</summary>
    internal sealed class OverlaySet
    {
        private readonly Dictionary<Guid, DatasetVisibility> _visibility = [];
        private readonly Dictionary<Guid, List<DatasetTag>> _tags = [];
        private readonly HashSet<Guid> _poisoned = [];

        internal bool AllDegraded { get; init; }

        internal void SetVisibility(Guid id, DatasetVisibility visibility) => _visibility[id] = visibility;

        internal void AddTag(Guid id, DatasetTag tag)
        {
            if (!_tags.TryGetValue(id, out var list))
            {
                _tags[id] = list = [];
            }

            list.Add(tag);
        }

        internal void Poison(Guid id) => _poisoned.Add(id);

        internal RowOverlay Get(Guid tableId)
        {
            if (AllDegraded || _poisoned.Contains(tableId))
            {
                return RowOverlay.Secure;
            }

            var visibility = _visibility.TryGetValue(tableId, out var v) ? v : DatasetVisibility.Hidden;
            var tags = _tags.TryGetValue(tableId, out var list)
                ? list.OrderBy(t => t.Tag, StringComparer.Ordinal).ToList()
                : [];
            return new RowOverlay(visibility, tags, false);
        }
    }

    /// <summary>Overlays the fresh tier and tags onto a structural (possibly cached) record without mutating it.</summary>
    internal static TableMetadata Apply(TableMetadata structural, RowOverlay row) => structural with
    {
        Table = structural.Table with { Visibility = row.Visibility },
        CatalogTags = row.TagNames
    };

    internal static TableMetadata Apply(TableMetadata structural, OverlaySet overlay) => Apply(structural, overlay.Get(structural.Table.Id));

    /// <summary>
    /// Catalog read mode: all rows (<paramref name="tableId"/> null) or one table. A failing query degrades every row, a malformed row degrades
    /// that table only (Hidden, no tags); the Error log carries the row id but no values, the counter is incremented. Never throws except for cancellation.
    /// </summary>
    internal static async Task<OverlaySet> ReadOverlayAsync(
        DbConnection conn, DbTransaction? tx, KeyDefinitionDialect dialect, Guid? tableId, ILogger? logger, CancellationToken ct)
    {
        try
        {
            return await LoadAsync(conn, tx, dialect, tableId, strict: false, logger, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or FormatException or InvalidCastException)
        {
            logger?.LogError(ex, "The dataset catalog settings could not be read; affected tables are treated as Hidden without tags.");
            GatewayDiagnostics.CatalogVisibilityDegradedCounter.Add(1);
            return new OverlaySet { AllDegraded = true };
        }
    }

    /// <summary>
    /// Admin read mode: the stored settings of one table (the table is known to exist). Throws on any store error or malformed row, so the
    /// caller never reports a fake Hidden as "before" state.
    /// </summary>
    internal static async Task<DatasetCatalogSettings> ReadStrictAsync(
        DbConnection conn, DbTransaction? tx, KeyDefinitionDialect dialect, Guid tableId, CancellationToken ct)
    {
        var set = await LoadAsync(conn, tx, dialect, tableId, strict: true, logger: null, ct).ConfigureAwait(false);
        var row = set.Get(tableId);
        return new DatasetCatalogSettings(row.Tags, row.Visibility);
    }

    /// <summary>
    /// The settings of an active table by identifier (null when unknown or inactive). <paramref name="strict"/>: admin read mode, throws on any
    /// store error or malformed row; otherwise the catalog read mode degrades to Hidden without tags.
    /// </summary>
    internal static async Task<DatasetCatalogSettings?> ReadSettingsAsync(
        DbConnection conn, DbTransaction? tx, KeyDefinitionDialect dialect, TableIdentifier table, bool strict, ILogger? logger, CancellationToken ct)
    {
        var tableId = await FindActiveTableIdAsync(conn, tx, dialect, table, ct).ConfigureAwait(false);
        if (tableId is null)
        {
            return null;
        }

        if (strict)
        {
            return await ReadStrictAsync(conn, tx, dialect, tableId.Value, ct).ConfigureAwait(false);
        }

        var row = (await ReadOverlayAsync(conn, tx, dialect, tableId.Value, logger, ct).ConfigureAwait(false)).Get(tableId.Value);
        return new DatasetCatalogSettings(row.Tags, row.Visibility);
    }

    /// <summary>Id of the active table with this identifier (no row lock); null when unknown or inactive.</summary>
    internal static async Task<Guid?> FindActiveTableIdAsync(
        DbConnection conn, DbTransaction? tx, KeyDefinitionDialect dialect, TableIdentifier table, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        Enlist(cmd, tx);
        cmd.CommandText = $@"SELECT id FROM {dialect.Prefix}TABLES
                             WHERE LOWER(source_name) = LOWER(@domain) AND LOWER(schema_name) = LOWER(@schema) AND LOWER(table_name) = LOWER(@table) AND is_active = 1";
        AddParameter(cmd, "@domain", table.Domain);
        AddParameter(cmd, "@schema", table.Schema);
        AddParameter(cmd, "@table", table.TableName);
        var id = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return id is not null && id is not DBNull && Guid.TryParse(Convert.ToString(id, CultureInfo.InvariantCulture), out var parsed) ? parsed : null;
    }

    private static async Task<OverlaySet> LoadAsync(
        DbConnection conn, DbTransaction? tx, KeyDefinitionDialect dialect, Guid? tableId, bool strict, ILogger? logger, CancellationToken ct)
    {
        var set = new OverlaySet();
        var filter = tableId is null ? string.Empty : " WHERE table_id = @tid";

        await using (var cmd = conn.CreateCommand())
        {
            Enlist(cmd, tx);
            cmd.CommandText = $"SELECT table_id, visibility FROM {dialect.Prefix}{SettingsTable}{filter}";
            if (tableId is { } id)
            {
                AddParameter(cmd, "@tid", id.ToString());
            }

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (!TryReadId(reader, out var rowId))
                {
                    Degrade(strict, logger, "<unreadable table_id>", SettingsTable);
                    continue;
                }

                if (TryReadVisibility(reader, out var visibility))
                {
                    set.SetVisibility(rowId, visibility);
                }
                else
                {
                    Degrade(strict, logger, rowId.ToString(), SettingsTable);
                    set.Poison(rowId);
                }
            }
        }

        await using (var cmd = conn.CreateCommand())
        {
            Enlist(cmd, tx);
            cmd.CommandText = $"SELECT table_id, tag, source FROM {dialect.Prefix}{TagsTable}{filter}";
            if (tableId is { } id)
            {
                AddParameter(cmd, "@tid", id.ToString());
            }

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (!TryReadId(reader, out var rowId))
                {
                    Degrade(strict, logger, "<unreadable table_id>", TagsTable);
                    continue;
                }

                if (TryReadTag(reader, out var tag))
                {
                    set.AddTag(rowId, tag);
                }
                else
                {
                    Degrade(strict, logger, rowId.ToString(), TagsTable);
                    set.Poison(rowId);
                }
            }
        }

        return set;
    }

    private static void Degrade(bool strict, ILogger? logger, string rowId, string table)
    {
        if (strict)
        {
            throw new InvalidOperationException($"A row of {table} is malformed (table_id {rowId}).");
        }

        logger?.LogError("A row of {Table} is malformed (table_id {RowId}); the table is treated as Hidden without tags.", table, rowId);
        GatewayDiagnostics.CatalogVisibilityDegradedCounter.Add(1);
    }

    private static bool TryReadId(DbDataReader reader, out Guid id)
    {
        id = Guid.Empty;
        try
        {
            return !reader.IsDBNull(0) && Guid.TryParse(Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture), out id);
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException)
        {
            return false;
        }
    }

    private static bool TryReadVisibility(DbDataReader reader, out DatasetVisibility visibility)
    {
        visibility = DatasetVisibility.Hidden;
        try
        {
            if (reader.IsDBNull(1))
            {
                return false;
            }

            var stored = Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture);
            if (stored is < 0 or > 2)
            {
                return false;
            }

            visibility = DatasetTagRules.ReadStored(stored);
            return true;
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
        {
            return false;
        }
    }

    private static bool TryReadTag(DbDataReader reader, out DatasetTag tag)
    {
        tag = new DatasetTag(string.Empty, DatasetTagSource.Admin);
        try
        {
            if (reader.IsDBNull(1) || reader.IsDBNull(2))
            {
                return false;
            }

            var raw = reader.GetString(1);
            // A stored tag must already be in its normalized form: anything else was not written through the rules.
            if (!DatasetTagRules.TryNormalize(raw, out var normalized) || !string.Equals(normalized, raw, StringComparison.Ordinal))
            {
                return false;
            }

            var source = reader.GetString(2);
            DatasetTagSource parsed;
            if (string.Equals(source, AdminSource, StringComparison.Ordinal))
            {
                parsed = DatasetTagSource.Admin;
            }
            else if (string.Equals(source, DbtSource, StringComparison.Ordinal))
            {
                parsed = DatasetTagSource.Dbt;
            }
            else
            {
                return false;
            }

            tag = new DatasetTag(normalized, parsed);
            return true;
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException)
        {
            return false;
        }
    }

    // ---- writes (the caller owns the transaction and invalidates its metadata cache after the commit) ----

    /// <summary>Replaces the tags of one source (admin only touches admin rows; dbt never deletes or re-sources an admin row). Null when the table is unknown or inactive.</summary>
    internal static Task<DatasetCatalogChange?> ReplaceTagsAsync(
        DbConnection conn, DbTransaction tx, KeyDefinitionDialect dialect, TableIdentifier table,
        IReadOnlyList<string> tags, DatasetTagSource source, Sid actor, CancellationToken ct)
    {
        var normalized = Normalize(tags);
        return MutateAsync(conn, tx, dialect, table, actor, async (tableId, before) =>
        {
            var existing = before.Tags.ToDictionary(t => t.Tag, t => t.Source, StringComparer.Ordinal);
            var wanted = new HashSet<string>(normalized, StringComparer.Ordinal);
            var sourceName = SourceName(source);

            foreach (var (tag, existingSource) in existing)
            {
                if (existingSource == source && !wanted.Contains(tag))
                {
                    await DeleteTagAsync(conn, tx, dialect, tableId, tag, ct).ConfigureAwait(false);
                }
            }

            foreach (var tag in normalized)
            {
                if (!existing.TryGetValue(tag, out var existingSource))
                {
                    await InsertTagAsync(conn, tx, dialect, tableId, tag, sourceName, actor, ct).ConfigureAwait(false);
                }
                else if (source == DatasetTagSource.Admin && existingSource == DatasetTagSource.Dbt)
                {
                    // An owner decision wins: the tag becomes admin-owned and survives later dbt runs.
                    await SetTagSourceAsync(conn, tx, dialect, tableId, tag, AdminSource, actor, ct).ConfigureAwait(false);
                }
            }
        }, ct);
    }

    /// <summary>Adds and removes admin-source tags; removing a tag never deletes a dbt row. Null when the table is unknown or inactive.</summary>
    internal static Task<DatasetCatalogChange?> ChangeTagsAsync(
        DbConnection conn, DbTransaction tx, KeyDefinitionDialect dialect, TableIdentifier table,
        IReadOnlyList<string> add, IReadOnlyList<string> remove, Sid actor, CancellationToken ct)
    {
        var toAdd = Normalize(add);
        var toRemove = Normalize(remove);
        return MutateAsync(conn, tx, dialect, table, actor, async (tableId, before) =>
        {
            var existing = before.Tags.ToDictionary(t => t.Tag, t => t.Source, StringComparer.Ordinal);
            foreach (var tag in toRemove)
            {
                if (existing.TryGetValue(tag, out var s) && s == DatasetTagSource.Admin)
                {
                    await DeleteTagAsync(conn, tx, dialect, tableId, tag, ct).ConfigureAwait(false);
                }
            }

            foreach (var tag in toAdd)
            {
                if (toRemove.Contains(tag))
                {
                    continue;
                }

                if (!existing.TryGetValue(tag, out var s))
                {
                    await InsertTagAsync(conn, tx, dialect, tableId, tag, AdminSource, actor, ct).ConfigureAwait(false);
                }
                else if (s == DatasetTagSource.Dbt)
                {
                    await SetTagSourceAsync(conn, tx, dialect, tableId, tag, AdminSource, actor, ct).ConfigureAwait(false);
                }
            }
        }, ct);
    }

    /// <summary>Sets the tier (no write when unchanged). Null when the table is unknown or inactive.</summary>
    internal static Task<DatasetCatalogChange?> SetVisibilityAsync(
        DbConnection conn, DbTransaction tx, KeyDefinitionDialect dialect, TableIdentifier table,
        DatasetVisibility visibility, Sid actor, CancellationToken ct)
    {
        if (!Enum.IsDefined(visibility))
        {
            throw new ArgumentOutOfRangeException(nameof(visibility));
        }

        return MutateAsync(conn, tx, dialect, table, actor, async (tableId, before) =>
        {
            if (before.Visibility == visibility)
            {
                return;
            }

            var parameters = new (string, object)[]
            {
                ("@tid", tableId.ToString()), ("@vis", (int)visibility), ("@by", actor.Value), ("@now", Now())
            };
            var updated = await ExecAsync(conn, tx,
                $"UPDATE {dialect.Prefix}{SettingsTable} SET visibility = @vis, updated_by = @by, updated_at = @now WHERE table_id = @tid", ct, parameters).ConfigureAwait(false);
            if (updated == 0)
            {
                await ExecAsync(conn, tx,
                    $"INSERT INTO {dialect.Prefix}{SettingsTable} (table_id, visibility, updated_by, updated_at) VALUES (@tid, @vis, @by, @now)", ct, parameters).ConfigureAwait(false);
            }
        }, ct);
    }

    /// <summary>
    /// A-10: inserts the system default tier for a table created in this transaction (only when no row exists). The callers pass it only for
    /// non-Hidden defaults, so the Hidden default writes nothing.
    /// </summary>
    internal static Task<int> InsertDefaultAsync(
        DbConnection conn, DbTransaction tx, KeyDefinitionDialect dialect, Guid tableId, DatasetVisibility visibility, CancellationToken ct) =>
        ExecAsync(conn, tx,
            $"INSERT INTO {dialect.Prefix}{SettingsTable} (table_id, visibility, updated_by, updated_at) " +
            $"SELECT @tid, @vis, @by, @now WHERE NOT EXISTS (SELECT 1 FROM {dialect.Prefix}{SettingsTable} WHERE table_id = @tid)",
            ct, ("@tid", tableId.ToString()), ("@vis", (int)visibility), ("@by", SystemDefaultActor), ("@now", Now()));

    private static async Task<DatasetCatalogChange?> MutateAsync(
        DbConnection conn, DbTransaction tx, KeyDefinitionDialect dialect, TableIdentifier table, Sid actor,
        Func<Guid, DatasetCatalogSettings, Task> write, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(actor.Value))
        {
            throw new ArgumentException("An actor is required.", nameof(actor));
        }

        var tableId = await ResolveActiveTableAsync(conn, tx, dialect, table, ct).ConfigureAwait(false);
        if (tableId is null)
        {
            return null;
        }

        var before = await ReadStrictAsync(conn, tx, dialect, tableId.Value, ct).ConfigureAwait(false);
        await write(tableId.Value, before).ConfigureAwait(false);
        var after = await ReadStrictAsync(conn, tx, dialect, tableId.Value, ct).ConfigureAwait(false);
        return new DatasetCatalogChange(table, before, after);
    }

    /// <summary>Locks and resolves the active table (the dialect lookup carries the engine's row lock); null when unknown or inactive.</summary>
    private static async Task<Guid?> ResolveActiveTableAsync(
        DbConnection conn, DbTransaction tx, KeyDefinitionDialect dialect, TableIdentifier table, CancellationToken ct)
    {
        Guid tableId;
        await using (var cmd = conn.CreateCommand())
        {
            Enlist(cmd, tx);
            cmd.CommandText = dialect.TableLookupSql;
            AddParameter(cmd, "@domain", table.Domain);
            AddParameter(cmd, "@schema", table.Schema);
            AddParameter(cmd, "@table", table.TableName);
            var id = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (id is null || !Guid.TryParse(Convert.ToString(id, CultureInfo.InvariantCulture), out tableId))
            {
                return null;
            }
        }

        await using var active = conn.CreateCommand();
        Enlist(active, tx);
        active.CommandText = $"SELECT is_active FROM {dialect.Prefix}TABLES WHERE id = @tid";
        AddParameter(active, "@tid", tableId.ToString());
        var flag = await active.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return flag is not null && flag is not DBNull && Convert.ToInt32(flag, CultureInfo.InvariantCulture) == 1 ? tableId : null;
    }

    private static List<string> Normalize(IReadOnlyList<string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        if (!DatasetTagRules.TryNormalizeSet(tags, DatasetTagRules.HardMaxTagsPerDataset, out var normalized, out var error))
        {
            throw new ArgumentException($"The tag set is invalid ({error}).", nameof(tags));
        }

        // A repository stores exactly what the rules accept: a tag that only normalizes (upper case, padding) is the caller's bug.
        for (var i = 0; i < tags.Count; i++)
        {
            if (!DatasetTagRules.TryNormalize(tags[i], out var n) || !string.Equals(n, tags[i], StringComparison.Ordinal))
            {
                throw new ArgumentException($"The tag set is invalid ({DatasetTagRules.InvalidTag}).", nameof(tags));
            }
        }

        return normalized.ToList();
    }

    private static string SourceName(DatasetTagSource source) => source == DatasetTagSource.Dbt ? DbtSource : AdminSource;

    private static string Now() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    private static Task<int> InsertTagAsync(
        DbConnection conn, DbTransaction tx, KeyDefinitionDialect dialect, Guid tableId, string tag, string source, Sid actor, CancellationToken ct) =>
        ExecAsync(conn, tx,
            $"INSERT INTO {dialect.Prefix}{TagsTable} (table_id, tag, source, created_by, created_at) VALUES (@tid, @tag, @source, @by, @now)",
            ct, ("@tid", tableId.ToString()), ("@tag", tag), ("@source", source), ("@by", actor.Value), ("@now", Now()));

    private static Task<int> DeleteTagAsync(
        DbConnection conn, DbTransaction tx, KeyDefinitionDialect dialect, Guid tableId, string tag, CancellationToken ct) =>
        ExecAsync(conn, tx, $"DELETE FROM {dialect.Prefix}{TagsTable} WHERE table_id = @tid AND tag = @tag", ct, ("@tid", tableId.ToString()), ("@tag", tag));

    private static Task<int> SetTagSourceAsync(
        DbConnection conn, DbTransaction tx, KeyDefinitionDialect dialect, Guid tableId, string tag, string source, Sid actor, CancellationToken ct) =>
        ExecAsync(conn, tx,
            $"UPDATE {dialect.Prefix}{TagsTable} SET source = @source, created_by = @by, created_at = @now WHERE table_id = @tid AND tag = @tag",
            ct, ("@tid", tableId.ToString()), ("@tag", tag), ("@source", source), ("@by", actor.Value), ("@now", Now()));

    private static void Enlist(DbCommand cmd, DbTransaction? tx)
    {
        if (tx is not null)
        {
            cmd.Transaction = tx;
        }
    }

    private static void AddParameter(DbCommand cmd, string name, object value)
    {
        var parameter = cmd.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        cmd.Parameters.Add(parameter);
    }

    private static async Task<int> ExecAsync(DbConnection conn, DbTransaction tx, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var cmd = conn.CreateCommand();
        Enlist(cmd, tx);
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            AddParameter(cmd, name, value);
        }

        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
