namespace Autheris.Infrastructure.Persistence;

using System.Data.Common;
using System.Globalization;
using Autheris.Domain.Model;

/// <summary>
/// Runtime-managed, app-scoped trusted GraphQL documents (Issue #281): portable SQL (named parameters) shared by the three governance
/// repositories. Every mutation is one transaction that changes the data and bumps the generation row (SEC-281-7); the store never runs DDL.
/// </summary>
internal static class TrustedDocumentStore
{
    public static async Task<long> GetGenerationAsync(DbConnection connection, CancellationToken ct) =>
        await ReadGenerationAsync(connection, null, ct).ConfigureAwait(false);

    public static async Task<TrustedDocumentStoreSnapshot> LoadSnapshotAsync(DbConnection connection, CancellationToken ct, DbTransaction? snapshot = null)
    {
        await using var ownedTx = snapshot == null ? await connection.BeginTransactionAsync(ct).ConfigureAwait(false) : null;
        var tx = snapshot ?? ownedTx!;
        // Without a snapshot transaction (PostgreSql/SqlServer) a concurrent write can land between the generation read and the data reads:
        // the registry then holds newer data under an older generation and reloads once more at the next poll. It never serves stale data.
        long generation = await ReadGenerationAsync(connection, tx, ct).ConfigureAwait(false);

        var versions = new List<(string App, string Version)>();
        await using (var cmd = Command(connection, tx, "SELECT app_id, version FROM TRUSTED_DOCUMENT_APP_VERSIONS WHERE status = @active ORDER BY app_id, version;",
            [("@active", TrustedDocumentVersionStatus.Active)]))
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                versions.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        var hashes = new Dictionary<(string, string), List<string>>();
        await using (var cmd = Command(connection, tx, @"
            SELECT h.app_id, h.version, h.hash FROM TRUSTED_DOCUMENT_HASHES h
            INNER JOIN TRUSTED_DOCUMENT_APP_VERSIONS v ON v.app_id = h.app_id AND v.version = h.version
            WHERE v.status = @active ORDER BY h.app_id, h.version, h.hash;", [("@active", TrustedDocumentVersionStatus.Active)]))
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var key = (reader.GetString(0), reader.GetString(1));
                if (!hashes.TryGetValue(key, out var list))
                {
                    hashes[key] = list = [];
                }

                list.Add(reader.GetString(2).Trim());
            }
        }

        var origins = await ReadOriginsAsync(connection, tx, null, ct).ConfigureAwait(false);
        var records = versions
            .Select(v => new TrustedDocumentVersionRecord(
                v.App, v.Version,
                hashes.TryGetValue(v, out var h) ? h : [],
                origins.TryGetValue(v.App, out var o) ? o : []))
            .ToList();
        return new TrustedDocumentStoreSnapshot(generation, records);
    }

    public static async Task<TrustedDocumentAppView?> GetAppAsync(DbConnection connection, string appId, CancellationToken ct)
    {
        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        if (!await ExistsAsync(connection, tx, "SELECT 1 FROM TRUSTED_DOCUMENT_APPS WHERE app_id = @app;", [("@app", appId)], ct).ConfigureAwait(false))
        {
            return null;
        }

        var origins = (await ReadOriginsAsync(connection, tx, appId, ct).ConfigureAwait(false)).GetValueOrDefault(appId) ?? [];
        var versions = new List<TrustedDocumentVersionSummary>();
        await using (var cmd = Command(connection, tx, @"
            SELECT v.version, v.status, v.created_at, v.updated_at, v.retired_at,
                   (SELECT COUNT(*) FROM TRUSTED_DOCUMENT_HASHES h WHERE h.app_id = v.app_id AND h.version = v.version)
            FROM TRUSTED_DOCUMENT_APP_VERSIONS v WHERE v.app_id = @app ORDER BY v.version;", [("@app", appId)]))
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                versions.Add(new TrustedDocumentVersionSummary(
                    reader.GetString(0), reader.GetString(1), Convert.ToInt32(reader.GetValue(5), CultureInfo.InvariantCulture),
                    ParseTime(reader.GetString(2)), ParseTime(reader.GetString(3)), reader.IsDBNull(4) ? null : ParseTime(reader.GetString(4))));
            }
        }

        return new TrustedDocumentAppView(appId, origins, versions);
    }

    public static async Task<TrustedDocumentSetDelta> ReplaceVersionAsync(
        DbConnection connection, string appId, string version, IReadOnlyList<string> origins, IReadOnlyList<string> hashes,
        int maxActiveVersions, DateTimeOffset at, CancellationToken ct)
    {
        var stamp = Time(at);
        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        // Serialize writers per app: the app row is locked first, so the active-version count and the version insert cannot interleave.
        // A concurrent first PUT of a new app loses on the app insert (primary key) and gets a conflict.
        if (await ExecuteAsync(connection, tx, "UPDATE TRUSTED_DOCUMENT_APPS SET updated_at = @at WHERE app_id = @app;", [("@at", stamp), ("@app", appId)], ct).ConfigureAwait(false) == 0)
        {
            try
            {
                await ExecuteAsync(connection, tx, "INSERT INTO TRUSTED_DOCUMENT_APPS (app_id, created_at, updated_at) VALUES (@app, @at, @at);", [("@app", appId), ("@at", stamp)], ct).ConfigureAwait(false);
            }
            catch (DbException)
            {
                throw new TrustedDocumentConcurrentChangeException();
            }
        }

        string? status = null;
        await using (var cmd = Command(connection, tx, "SELECT status FROM TRUSTED_DOCUMENT_APP_VERSIONS WHERE app_id = @app AND version = @ver;", [("@app", appId), ("@ver", version)]))
        {
            var value = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            status = value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        if (status == TrustedDocumentVersionStatus.Retired)
        {
            throw new TrustedDocumentVersionRetiredException();
        }

        bool versionCreated = status is null;
        if (versionCreated)
        {
            await using var cmd = Command(connection, tx, "SELECT COUNT(*) FROM TRUSTED_DOCUMENT_APP_VERSIONS WHERE app_id = @app AND status = @active;",
                [("@app", appId), ("@active", TrustedDocumentVersionStatus.Active)]);
            var active = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
            if (active >= maxActiveVersions)
            {
                throw new TrustedDocumentVersionLimitException();
            }
        }

        var storedOrigins = (await ReadOriginsAsync(connection, tx, appId, ct).ConfigureAwait(false)).GetValueOrDefault(appId) ?? [];
        var wantedOrigins = origins.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        bool originsChanged = !storedOrigins.SequenceEqual(wantedOrigins, StringComparer.Ordinal);
        if (originsChanged)
        {
            await ExecuteAsync(connection, tx, "DELETE FROM TRUSTED_DOCUMENT_APP_ORIGINS WHERE app_id = @app;", [("@app", appId)], ct).ConfigureAwait(false);
            foreach (var origin in wantedOrigins)
            {
                await ExecuteAsync(connection, tx, "INSERT INTO TRUSTED_DOCUMENT_APP_ORIGINS (app_id, origin) VALUES (@app, @origin);", [("@app", appId), ("@origin", origin)], ct).ConfigureAwait(false);
            }
        }

        if (versionCreated)
        {
            try
            {
                await ExecuteAsync(connection, tx,
                    "INSERT INTO TRUSTED_DOCUMENT_APP_VERSIONS (app_id, version, status, created_at, updated_at) VALUES (@app, @ver, @status, @at, @at);",
                    [("@app", appId), ("@ver", version), ("@status", TrustedDocumentVersionStatus.Active), ("@at", stamp)], ct).ConfigureAwait(false);
            }
            catch (DbException)
            {
                throw new TrustedDocumentConcurrentChangeException();
            }
        }
        else
        {
            await ExecuteAsync(connection, tx, "UPDATE TRUSTED_DOCUMENT_APP_VERSIONS SET updated_at = @at WHERE app_id = @app AND version = @ver;",
                [("@at", stamp), ("@app", appId), ("@ver", version)], ct).ConfigureAwait(false);
        }

        var stored = new HashSet<string>(StringComparer.Ordinal);
        await using (var cmd = Command(connection, tx, "SELECT hash FROM TRUSTED_DOCUMENT_HASHES WHERE app_id = @app AND version = @ver;", [("@app", appId), ("@ver", version)]))
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                stored.Add(reader.GetString(0).Trim());
            }
        }

        var wanted = new HashSet<string>(hashes, StringComparer.Ordinal);
        var added = wanted.Where(h => !stored.Contains(h)).Order(StringComparer.Ordinal).ToList();
        var removed = stored.Where(h => !wanted.Contains(h)).Order(StringComparer.Ordinal).ToList();
        foreach (var hash in removed)
        {
            await ExecuteAsync(connection, tx, "DELETE FROM TRUSTED_DOCUMENT_HASHES WHERE app_id = @app AND version = @ver AND hash = @hash;",
                [("@app", appId), ("@ver", version), ("@hash", hash)], ct).ConfigureAwait(false);
        }

        foreach (var hash in added)
        {
            await ExecuteAsync(connection, tx, "INSERT INTO TRUSTED_DOCUMENT_HASHES (app_id, version, hash) VALUES (@app, @ver, @hash);",
                [("@app", appId), ("@ver", version), ("@hash", hash)], ct).ConfigureAwait(false);
        }

        await BumpAsync(connection, tx, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return new TrustedDocumentSetDelta(added, removed, wanted.Count - added.Count, versionCreated, originsChanged);
    }

    public static async Task<TrustedDocumentRetireOutcome> RetireVersionAsync(DbConnection connection, string appId, string version, DateTimeOffset at, CancellationToken ct)
    {
        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        string? status;
        await using (var cmd = Command(connection, tx, "SELECT status FROM TRUSTED_DOCUMENT_APP_VERSIONS WHERE app_id = @app AND version = @ver;", [("@app", appId), ("@ver", version)]))
        {
            var value = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            status = value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        if (status is null)
        {
            return TrustedDocumentRetireOutcome.NotFound;
        }

        if (status == TrustedDocumentVersionStatus.Retired)
        {
            return TrustedDocumentRetireOutcome.AlreadyRetired;
        }

        var stamp = Time(at);
        await ExecuteAsync(connection, tx,
            "UPDATE TRUSTED_DOCUMENT_APP_VERSIONS SET status = @status, updated_at = @at, retired_at = @at WHERE app_id = @app AND version = @ver;",
            [("@status", TrustedDocumentVersionStatus.Retired), ("@at", stamp), ("@app", appId), ("@ver", version)], ct).ConfigureAwait(false);
        await BumpAsync(connection, tx, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return TrustedDocumentRetireOutcome.Retired;
    }

    private static async Task<Dictionary<string, IReadOnlyList<string>>> ReadOriginsAsync(DbConnection connection, DbTransaction tx, string? appId, CancellationToken ct)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        await using var cmd = appId is null
            ? Command(connection, tx, "SELECT app_id, origin FROM TRUSTED_DOCUMENT_APP_ORIGINS ORDER BY app_id, origin;", [])
            : Command(connection, tx, "SELECT app_id, origin FROM TRUSTED_DOCUMENT_APP_ORIGINS WHERE app_id = @app ORDER BY origin;", [("@app", appId)]);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var app = reader.GetString(0);
            if (!map.TryGetValue(app, out var list))
            {
                map[app] = list = [];
            }

            list.Add(reader.GetString(1));
        }

        return map.ToDictionary(p => p.Key, p => (IReadOnlyList<string>)p.Value, StringComparer.Ordinal);
    }

    private static async Task<bool> ExistsAsync(DbConnection connection, DbTransaction tx, string sql, (string Name, object? Value)[] parameters, CancellationToken ct)
    {
        await using var cmd = Command(connection, tx, sql, parameters);
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is not (null or DBNull);
    }

    private static string Time(DateTimeOffset at) => at.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTime(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);

    private static Task BumpAsync(DbConnection connection, DbTransaction tx, CancellationToken ct) =>
        ExecuteAsync(connection, tx, "UPDATE TRUSTED_DOCUMENT_GENERATION SET generation = generation + 1 WHERE id = 1;", [], ct);

    private static async Task<int> ExecuteAsync(DbConnection connection, DbTransaction tx, string sql, (string Name, object? Value)[] parameters, CancellationToken ct)
    {
        await using var cmd = Command(connection, tx, sql, parameters);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<long> ReadGenerationAsync(DbConnection connection, DbTransaction? tx, CancellationToken ct)
    {
        await using var cmd = Command(connection, tx, "SELECT generation FROM TRUSTED_DOCUMENT_GENERATION WHERE id = 1;", []);
        var value = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static DbCommand Command(DbConnection connection, DbTransaction? tx, string sql, (string Name, object? Value)[] parameters)
    {
        var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            if (name == "@hash" && value is string && connection.GetType().Name.Contains("SqlConnection", StringComparison.Ordinal))
            {
                // CHAR(64) column on SQL Server: a fixed-length ANSI parameter keeps the primary key seek (no NVARCHAR conversion).
                p.DbType = System.Data.DbType.AnsiStringFixedLength;
                p.Size = 64;
            }

            cmd.Parameters.Add(p);
        }

        return cmd;
    }
}
