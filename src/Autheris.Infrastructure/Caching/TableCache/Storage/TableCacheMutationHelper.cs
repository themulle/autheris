namespace Autheris.Infrastructure.Caching.TableCache.Storage;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using Autheris.Domain.Caching.TableCache;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options.TableCache;
using DuckDB.NET.Data;

/// <summary>
/// Shared schema validation, column-type inference, and parameterized mutation planning for DuckDB table cache stores.
/// Never interpolates values into SQL text and never assigns <see cref="DbCommand.CommandText"/> directly.
/// </summary>
internal static class TableCacheMutationHelper
{
    private enum InferredKind
    {
        Unknown = 0,
        Boolean,
        Int32,
        Int64,
        Decimal,
        Double,
        DateTime,
        DateTimeOffset,
        Blob,
        Uuid,
        Varchar
    }

    internal sealed record PlannedMutation(
        CdcOperation Operation,
        string Sql,
        IReadOnlyList<(string Name, object? Value)> Parameters);

    public static string SanitizeIdentifier(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        var trimmed = identifier.Trim();
        return SqlIdentifierGuard.ValidateIdentifier(
            trimmed,
            nameof(identifier),
            maxLength: SqlIdentifierGuard.DefaultMaxLength,
            allowLeadingDigit: true,
            message: trimmed.Length > SqlIdentifierGuard.DefaultMaxLength
                ? $"Identifier exceeds maximum length of {SqlIdentifierGuard.DefaultMaxLength} characters: '{identifier}'"
                : $"Identifier contains invalid characters: '{identifier}'");
    }

    public static TableIdentifier ResolveTableIdentifier(TableCacheTargetOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var trimmed = options.Table.Trim();
        var dotCount = trimmed.Count('.');
        return dotCount switch
        {
            0 => TableIdentifier.Parse($"{options.DataSource}.default.{trimmed}"),
            1 => TableIdentifier.Parse($"{options.DataSource}.{trimmed}"),
            _ => TableIdentifier.Parse(trimmed)
        };
    }

    public static string BuildCreateTableSql(
        string shadowTable,
        IReadOnlyList<string> sanitizedCols,
        IReadOnlyList<IReadOnlyList<object?>> sampleRows,
        IReadOnlyList<string>? declaredPrimaryKeys)
    {
        if (sanitizedCols.Count == 0)
        {
            throw new ArgumentException("At least one column is required to create a cached table.", nameof(sanitizedCols));
        }

        var kinds = new InferredKind[sanitizedCols.Count];
        for (int r = 0; r < sampleRows.Count; r++)
        {
            var row = sampleRows[r];
            for (int c = 0; c < sanitizedCols.Count && c < row.Count; c++)
            {
                kinds[c] = MergeKind(kinds[c], ClassifyValue(row[c]));
            }
        }

        var colDefs = new string[sanitizedCols.Count];
        for (int c = 0; c < sanitizedCols.Count; c++)
        {
            colDefs[c] = $"\"{sanitizedCols[c]}\" {ToDuckDbType(kinds[c])}";
        }

        string pkClause = string.Empty;
        if (declaredPrimaryKeys is { Count: > 0 })
        {
            var colSet = new HashSet<string>(sanitizedCols, StringComparer.OrdinalIgnoreCase);
            var pkCols = new string[declaredPrimaryKeys.Count];
            for (int i = 0; i < declaredPrimaryKeys.Count; i++)
            {
                var pk = SanitizeIdentifier(declaredPrimaryKeys[i]);
                if (!colSet.Contains(pk))
                {
                    throw new InvalidOperationException($"Declared primary key column '{pk}' does not exist in cached table column set.");
                }

                // Resolve canonical sanitized casing from sanitizedCols
                pkCols[i] = sanitizedCols.First(c => string.Equals(c, pk, StringComparison.OrdinalIgnoreCase));
            }

            pkClause = $", PRIMARY KEY ({string.Join(", ", pkCols.Select(c => $"\"{c}\""))})";
        }

        return $"CREATE TABLE \"{SanitizeIdentifier(shadowTable)}\" ({string.Join(", ", colDefs)}{pkClause});";
    }

    public static IReadOnlyList<PlannedMutation> ValidateAndPlanBatch(
        string targetTable,
        IReadOnlyList<TableCacheMutation> mutations,
        IReadOnlyList<string> declaredColumns,
        IReadOnlyList<string>? declaredPrimaryKeys)
    {
        ArgumentNullException.ThrowIfNull(mutations);
        var safeTable = SanitizeIdentifier(targetTable);

        if (declaredColumns == null || declaredColumns.Count == 0)
        {
            throw new InvalidOperationException("Declared column schema is unavailable for cached table mutation.");
        }

        var canonicalCols = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < declaredColumns.Count; i++)
        {
            var sanitized = SanitizeIdentifier(declaredColumns[i]);
            canonicalCols[sanitized] = sanitized;
        }

        List<string>? canonicalPks = null;
        if (declaredPrimaryKeys is { Count: > 0 })
        {
            canonicalPks = new List<string>(declaredPrimaryKeys.Count);
            for (int i = 0; i < declaredPrimaryKeys.Count; i++)
            {
                var sanitized = SanitizeIdentifier(declaredPrimaryKeys[i]);
                if (!canonicalCols.TryGetValue(sanitized, out var canonical))
                {
                    throw new InvalidOperationException("Declared primary key column is not present in table schema.");
                }

                canonicalPks.Add(canonical);
            }
        }

        var planned = new List<PlannedMutation>(mutations.Count);
        for (int i = 0; i < mutations.Count; i++)
        {
            var mut = mutations[i] ?? throw new InvalidOperationException("Cache mutation entry cannot be null.");
            planned.Add(PlanSingle(safeTable, mut, canonicalCols, canonicalPks));
        }

        return planned;
    }

    public static void BindParameters(DbCommand cmd, IReadOnlyList<(string Name, object? Value)> parameters)
    {
        cmd.Parameters.Clear();
        for (int i = 0; i < parameters.Count; i++)
        {
            var (name, val) = parameters[i];
            cmd.Parameters.Add(new DuckDBParameter(name, NormalizeParameterValue(val)));
        }
    }

    public static object NormalizeParameterValue(object? value) =>
        value is null or DBNull ? DBNull.Value : value;

    private static PlannedMutation PlanSingle(
        string safeTable,
        TableCacheMutation mut,
        Dictionary<string, string> canonicalCols,
        List<string>? canonicalPks)
    {
        int paramIdx = 1;
        var parameters = new List<(string Name, object? Value)>();

        switch (mut.Operation)
        {
            case CdcOperation.Insert:
            case CdcOperation.Snapshot:
            {
                if (mut.RowValues == null || mut.RowValues.Count == 0)
                {
                    throw new InvalidOperationException("Insert cache mutation must contain row values.");
                }

                var cols = new List<string>(mut.RowValues.Count);
                var placeholders = new List<string>(mut.RowValues.Count);
                var providedKeys = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

                foreach (var kv in mut.RowValues)
                {
                    var col = ResolveDeclaredColumn(kv.Key, canonicalCols);
                    cols.Add($"\"{col}\"");
                    var pName = $"p{paramIdx++}";
                    placeholders.Add($"${pName}");
                    parameters.Add((pName, kv.Value));
                    providedKeys[col] = kv.Value;
                }

                if (mut.PrimaryKeyValues is { Count: > 0 })
                {
                    foreach (var kv in mut.PrimaryKeyValues)
                    {
                        var pkCol = ResolveDeclaredColumn(kv.Key, canonicalCols);
                        if (kv.Value is null or DBNull)
                        {
                            throw new InvalidOperationException("Primary key value in cache mutation cannot be null.");
                        }

                        providedKeys.TryAdd(pkCol, kv.Value);
                    }
                }

                if (canonicalPks is { Count: > 0 })
                {
                    for (int k = 0; k < canonicalPks.Count; k++)
                    {
                        var pkCol = canonicalPks[k];
                        if (!providedKeys.TryGetValue(pkCol, out var pkVal) || pkVal is null or DBNull)
                        {
                            throw new InvalidOperationException("Insert cache mutation is missing a non-null declared primary key value.");
                        }
                    }
                }

                var sql = $"INSERT INTO \"{safeTable}\" ({string.Join(", ", cols)}) VALUES ({string.Join(", ", placeholders)});";
                return new PlannedMutation(mut.Operation, sql, parameters);
            }

            case CdcOperation.Update:
            {
                if (mut.RowValues == null || mut.RowValues.Count == 0)
                {
                    throw new InvalidOperationException("Update cache mutation must contain row values.");
                }

                var pkEntries = ValidatePrimaryKeyValues(mut.PrimaryKeyValues, canonicalCols, canonicalPks);

                var setClauses = new List<string>(mut.RowValues.Count);
                foreach (var kv in mut.RowValues)
                {
                    var col = ResolveDeclaredColumn(kv.Key, canonicalCols);
                    var pName = $"p{paramIdx++}";
                    setClauses.Add($"\"{col}\" = ${pName}");
                    parameters.Add((pName, kv.Value));
                }

                var whereClauses = BuildWhereClauses(pkEntries, ref paramIdx, parameters);
                var sql = $"UPDATE \"{safeTable}\" SET {string.Join(", ", setClauses)} WHERE {string.Join(" AND ", whereClauses)};";
                return new PlannedMutation(mut.Operation, sql, parameters);
            }

            case CdcOperation.Delete:
            {
                var pkEntries = ValidatePrimaryKeyValues(mut.PrimaryKeyValues, canonicalCols, canonicalPks);
                var whereClauses = BuildWhereClauses(pkEntries, ref paramIdx, parameters);
                var sql = $"DELETE FROM \"{safeTable}\" WHERE {string.Join(" AND ", whereClauses)};";
                return new PlannedMutation(mut.Operation, sql, parameters);
            }

            default:
                throw new InvalidOperationException($"Unsupported cache mutation operation '{mut.Operation}'.");
        }
    }

    private static List<(string Column, object Value)> ValidatePrimaryKeyValues(
        IReadOnlyDictionary<string, object?>? primaryKeyValues,
        Dictionary<string, string> canonicalCols,
        List<string>? canonicalPks)
    {
        if (primaryKeyValues == null || primaryKeyValues.Count == 0)
        {
            throw new InvalidOperationException("Cache mutation requires non-empty primary key values.");
        }

        var resolved = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in primaryKeyValues)
        {
            var col = ResolveDeclaredColumn(kv.Key, canonicalCols);
            if (kv.Value is null or DBNull)
            {
                throw new InvalidOperationException("Primary key value in cache mutation cannot be null.");
            }

            resolved[col] = kv.Value;
        }

        if (canonicalPks is { Count: > 0 })
        {
            if (resolved.Count != canonicalPks.Count)
            {
                throw new InvalidOperationException("Cache mutation primary key does not match declared primary key column count.");
            }

            var ordered = new List<(string Column, object Value)>(canonicalPks.Count);
            for (int i = 0; i < canonicalPks.Count; i++)
            {
                var pkCol = canonicalPks[i];
                if (!resolved.TryGetValue(pkCol, out var pkVal))
                {
                    throw new InvalidOperationException("Cache mutation is missing a required declared primary key column.");
                }

                ordered.Add((pkCol, pkVal));
            }

            return ordered;
        }

        var result = new List<(string Column, object Value)>(resolved.Count);
        foreach (var kv in resolved)
        {
            result.Add((kv.Key, kv.Value));
        }

        return result;
    }

    private static List<string> BuildWhereClauses(
        List<(string Column, object Value)> pkEntries,
        ref int paramIdx,
        List<(string Name, object? Value)> parameters)
    {
        var whereClauses = new List<string>(pkEntries.Count);
        for (int i = 0; i < pkEntries.Count; i++)
        {
            var pName = $"p{paramIdx++}";
            whereClauses.Add($"\"{pkEntries[i].Column}\" = ${pName}");
            parameters.Add((pName, pkEntries[i].Value));
        }

        return whereClauses;
    }

    private static string ResolveDeclaredColumn(string columnName, Dictionary<string, string> canonicalCols)
    {
        var sanitized = SanitizeIdentifier(columnName);
        if (!canonicalCols.TryGetValue(sanitized, out var canonical))
        {
            throw new InvalidOperationException("Cache mutation references an undeclared column.");
        }

        return canonical;
    }

    private static InferredKind ClassifyValue(object? value) => value switch
    {
        null or DBNull => InferredKind.Unknown,
        bool => InferredKind.Boolean,
        sbyte or byte or short or ushort or int => InferredKind.Int32,
        uint or long or ulong => InferredKind.Int64,
        decimal => InferredKind.Decimal,
        float or double => InferredKind.Double,
        DateTime => InferredKind.DateTime,
        DateTimeOffset => InferredKind.DateTimeOffset,
        byte[] => InferredKind.Blob,
        Guid => InferredKind.Uuid,
        _ => InferredKind.Varchar
    };

    private static InferredKind MergeKind(InferredKind current, InferredKind next)
    {
        if (next == InferredKind.Unknown || current == next) return current;
        if (current == InferredKind.Unknown) return next;

        if ((current == InferredKind.Int32 && next == InferredKind.Int64) ||
            (current == InferredKind.Int64 && next == InferredKind.Int32))
        {
            return InferredKind.Int64;
        }

        if ((current is InferredKind.Int32 or InferredKind.Int64 && next == InferredKind.Decimal) ||
            (current == InferredKind.Decimal && next is InferredKind.Int32 or InferredKind.Int64))
        {
            return InferredKind.Decimal;
        }

        if ((current is InferredKind.Int32 or InferredKind.Int64 or InferredKind.Decimal && next == InferredKind.Double) ||
            (current == InferredKind.Double && next is InferredKind.Int32 or InferredKind.Int64 or InferredKind.Decimal))
        {
            return InferredKind.Double;
        }

        return InferredKind.Varchar;
    }

    private static string ToDuckDbType(InferredKind kind) => kind switch
    {
        InferredKind.Boolean => "BOOLEAN",
        InferredKind.Int32 => "INTEGER",
        InferredKind.Int64 => "BIGINT",
        InferredKind.Decimal => "DECIMAL(38,10)",
        InferredKind.Double => "DOUBLE",
        InferredKind.DateTime => "TIMESTAMP",
        InferredKind.DateTimeOffset => "TIMESTAMPTZ",
        InferredKind.Blob => "BLOB",
        InferredKind.Uuid => "UUID",
        _ => "VARCHAR"
    };
}
