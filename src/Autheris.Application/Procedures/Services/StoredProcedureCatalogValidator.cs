namespace Autheris.Application.Procedures.Services;

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// F-SQL-02: Validates a procedure declaration against the SQL Server catalog (fail-closed). The validator never
/// reads or executes any procedure code beyond inspecting metadata views.
/// </summary>
public sealed class StoredProcedureCatalogValidator
{
    private static readonly Regex DynamicSqlRegex = new(
        @"\b(sp_executesql|exec(?:ute)?\s*\(|exec(?:ute)?\s+@)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(1));

    private readonly ProcedureConnectionProvider _connections;
    private readonly IOptions<GatewayOptions> _options;
    private readonly ITableMetadataRepository? _tableRepository;
    private readonly ILogger<StoredProcedureCatalogValidator>? _logger;

    public StoredProcedureCatalogValidator(
        ProcedureConnectionProvider connections,
        IOptions<GatewayOptions> options,
        ITableMetadataRepository? tableRepository = null,
        ILogger<StoredProcedureCatalogValidator>? logger = null)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _tableRepository = tableRepository;
        _logger = logger;
    }

    public async Task<ProcedureValidationResult> ValidateAsync(ProcedureDefinition definition, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var errors = new List<string>();
        var settings = _options.Value.SqlEndpoints.Procedures;

        var parts = definition.ProcedureName.Split('.');
        if (parts.Length != 2 || !settings.AllowedSchemas.Contains(parts[0], StringComparer.OrdinalIgnoreCase))
        {
            return ProcedureValidationResult.Failed("Procedure schema is not listed in SqlEndpoints.Procedures.AllowedSchemas.");
        }

        // The identifiers were validated against [A-Za-z0-9_] by the parser; quoting is defense in depth.
        string quoted = $"[{parts[0]}].[{parts[1]}]";

        DbConnection connection;
        try
        {
            (connection, _) = await _connections.OpenAsync(definition, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException)
        {
            // Configuration problem (missing DataSources.Connections entry, empty connection string, unsupported provider).
            _logger?.LogWarning("Procedure '{Procedure}' could not be validated: configuration error: {Message}", definition.Name, ex.Message);
            return ProcedureValidationResult.Failed($"Procedure connection is not configured correctly: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Network/login problem. Message only (no stack trace): this repeats every minute while the database is down.
            _logger?.LogWarning("Procedure '{Procedure}' could not be validated: database not reachable ({ExceptionType}: {Message}).", definition.Name, ex.GetType().Name, ex.Message);
            return ProcedureValidationResult.Failed("The database is not reachable with the configured procedure connection (check DNS/VPN and credentials).");
        }

        await using (connection.ConfigureAwait(false))
        {
            try
            {
                return await ValidateCoreAsync(connection, definition, quoted, errors, ct).ConfigureAwait(false);
            }
            catch (DbException ex)
            {
                _logger?.LogWarning(ex, "Procedure '{Procedure}' validation failed with a database error.", definition.Name);
                return ProcedureValidationResult.Failed("Database metadata could not be read (missing VIEW DEFINITION / VIEW DATABASE STATE?).");
            }
        }
    }

    private async Task<ProcedureValidationResult> ValidateCoreAsync(
        DbConnection connection,
        ProcedureDefinition definition,
        string quoted,
        List<string> errors,
        CancellationToken ct)
    {
        // 1. Existence, object type and EXECUTE (procedure) / SELECT (table-valued function) permission
        bool isFunction = definition.Kind == ProcedureKind.TableValuedFunction;
        int objectId;
        string objectType;
        await using (var cmd = CreateCommand(connection, "SELECT object_id, type FROM sys.objects WHERE object_id = OBJECT_ID(@n)", ("@n", quoted)))
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                return ProcedureValidationResult.Failed("The object does not exist.");
            }

            objectId = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture);
            objectType = reader.GetString(1).Trim();
        }

        bool typeMatches = isFunction
            ? objectType is "IF" or "TF"
            : string.Equals(objectType, "P", StringComparison.Ordinal);
        if (!typeMatches)
        {
            return ProcedureValidationResult.Failed(isFunction
                ? "The object is not an inline or multi-statement table-valued function."
                : "The object is not a stored procedure.");
        }

        string permission = isFunction ? "SELECT" : "EXECUTE";
        object? execPerm = await ScalarAsync(connection, "SELECT HAS_PERMS_BY_NAME(@n, N'OBJECT', @perm)", ct, ("@n", quoted), ("@perm", permission)).ConfigureAwait(false);
        if (execPerm == null || execPerm is DBNull || Convert.ToInt32(execPerm, CultureInfo.InvariantCulture) != 1)
        {
            errors.Add($"The technical procedure login has no {permission} permission on '{definition.ProcedureName}'.");
        }

        // 2. Module properties: EXECUTE AS and dynamic SQL
        string? definitionText = null;
        bool hasExecuteAs = false;
        await using (var cmd = CreateCommand(connection, "SELECT definition, execute_as_principal_id FROM sys.sql_modules WHERE object_id = @id", ("@id", objectId)))
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                definitionText = reader.IsDBNull(0) ? null : reader.GetString(0);
                hasExecuteAs = !reader.IsDBNull(1);
            }
        }

        if (hasExecuteAs)
        {
            errors.Add("The procedure uses EXECUTE AS; this requires a separate review and is not supported.");
        }

        if (definitionText == null)
        {
            // Encrypted modules cannot be inspected.
            errors.Add("The procedure definition is not readable (encrypted or missing VIEW DEFINITION); dynamic SQL cannot be excluded.");
        }
        else if (!definition.AllowDynamicSql && DynamicSqlRegex.IsMatch(definitionText))
        {
            errors.Add("The procedure appears to use dynamic SQL (sp_executesql / EXEC(...)). Declare '@allow-dynamic-sql' after a DBA review.");
        }

        // 3. Parameters
        var dbParams = new Dictionary<string, (string Type, int MaxLength, bool HasDefault, bool IsOutput)>(StringComparer.OrdinalIgnoreCase);
        var dbParamOrder = new List<string>();
        await using (var cmd = CreateCommand(
            connection,
            "SELECT name, TYPE_NAME(user_type_id), max_length, has_default_value, is_output FROM sys.parameters WHERE object_id = @id AND parameter_id > 0 ORDER BY parameter_id",
            ("@id", objectId)))
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                string pname = reader.GetString(0).TrimStart('@');
                dbParamOrder.Add(pname);
                dbParams[pname] = (
                    reader.GetString(1).ToLowerInvariant(),
                    Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture),
                    Convert.ToBoolean(reader.GetValue(3), CultureInfo.InvariantCulture),
                    Convert.ToBoolean(reader.GetValue(4), CultureInfo.InvariantCulture));
            }
        }

        var declared = definition.Parameters.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        var bound = definition.ContextBindings.Select(b => b.ParameterName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var (pname, info) in dbParams)
        {
            if (info.IsOutput)
            {
                errors.Add($"Parameter '@{pname}' is an OUTPUT parameter; OUTPUT parameters are not supported yet.");
                continue;
            }

            if (!declared.ContainsKey(pname) && !bound.Contains(pname) && !info.HasDefault)
            {
                errors.Add($"Required procedure parameter '@{pname}' is neither declared via @param nor bound via @context.");
            }
        }

        foreach (var p in definition.Parameters)
        {
            if (!dbParams.TryGetValue(p.Name, out var info))
            {
                errors.Add($"Declared parameter '{p.Name}' does not exist on the procedure.");
                continue;
            }

            string declaredBase = p.SqlType.Contains('(') ? p.SqlType[..p.SqlType.IndexOf('(')] : p.SqlType;
            if (!string.Equals(declaredBase, info.Type, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"Parameter '{p.Name}' is declared as '{declaredBase}' but the procedure expects '{info.Type}'.");
                continue;
            }

            if (p.ClrType == typeof(string) && p.MaxLength.HasValue && info.MaxLength > 0)
            {
                int dbChars = declaredBase.StartsWith('n') ? info.MaxLength / 2 : info.MaxLength;
                if (p.MaxLength.Value > dbChars)
                {
                    errors.Add($"Parameter '{p.Name}' allows {p.MaxLength} characters but the procedure accepts only {dbChars}.");
                }
            }

            if (!p.IsRequired && !info.HasDefault)
            {
                errors.Add($"Parameter '{p.Name}' is declared optional but the procedure defines no default value.");
            }
        }

        foreach (var binding in definition.ContextBindings)
        {
            if (!dbParams.ContainsKey(binding.ParameterName))
            {
                errors.Add($"Context parameter '@{binding.ParameterName}' does not exist on the procedure.");
            }
        }

        // Review P-2: function arguments are positional. The declared order (client and context arguments) must match
        // the signature exactly, otherwise a client value could land in the tenant argument.
        if (isFunction && !definition.ArgumentOrder.SequenceEqual(dbParamOrder, StringComparer.OrdinalIgnoreCase))
        {
            errors.Add($"The declared argument order ({string.Join(", ", definition.ArgumentOrder)}) does not match the function signature ({string.Join(", ", dbParamOrder)}).");
        }

        // 4. Result set structure
        var resultColumns = new List<string>();
        if (isFunction)
        {
            await using var fnCmd = CreateCommand(connection, "SELECT name FROM sys.columns WHERE object_id = @id ORDER BY column_id", ("@id", objectId));
            await using var fnReader = await fnCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await fnReader.ReadAsync(ct).ConfigureAwait(false))
            {
                resultColumns.Add(fnReader.GetString(0));
            }
        }
        else
        {
            await using (var cmd = CreateCommand(
                connection,
                "SELECT name, error_number FROM sys.dm_exec_describe_first_result_set_for_object(@id, 0) WHERE is_hidden = 0 ORDER BY column_ordinal",
                ("@id", objectId)))
            await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    if (!reader.IsDBNull(1) || reader.IsDBNull(0))
                    {
                        errors.Add("The result set structure cannot be determined (dynamic result, temp tables or unnamed columns).");
                        resultColumns.Clear();
                        break;
                    }

                    resultColumns.Add(reader.GetString(0));
                }
            }
        }

        // 5. Referenced objects: read/write mode, nesting and row-level security
        var tables = new List<(string Schema, string Name)>();
        await using (var cmd = CreateCommand(
            connection,
            "SELECT r.referenced_schema_name, r.referenced_entity_name, MAX(o.type) AS obj_type, MAX(CAST(r.is_updated AS int)) AS is_updated, " +
            "MAX(r.referenced_database_name) AS db_name, MAX(r.referenced_server_name) AS server_name " +
            "FROM sys.dm_sql_referenced_entities(@n, N'OBJECT') AS r " +
            "LEFT JOIN sys.objects AS o ON o.object_id = r.referenced_id " +
            "WHERE r.referenced_class = 1 " +
            "GROUP BY r.referenced_schema_name, r.referenced_entity_name " +
            "ORDER BY r.referenced_schema_name, r.referenced_entity_name",
            ("@n", quoted)))
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                string schema = reader.IsDBNull(0) ? "dbo" : reader.GetString(0);
                string name = reader.GetString(1);
                string type = reader.IsDBNull(2) ? string.Empty : reader.GetString(2).Trim();
                bool updated = !reader.IsDBNull(3) && Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture) == 1;
                bool crossDatabase = !reader.IsDBNull(4) || !reader.IsDBNull(5);

                if (crossDatabase)
                {
                    errors.Add($"The procedure references an object in another database or server ('{name}'), which cannot be governed.");
                    continue;
                }

                if (!string.Equals(type, "U", StringComparison.Ordinal))
                {
                    errors.Add($"The procedure references '{schema}.{name}' of type '{(type.Length == 0 ? "unknown" : type)}'. Only direct references to user tables can be verified; nested objects are rejected.");
                    continue;
                }

                if (updated && definition.Mode == ProcedureMode.Read)
                {
                    errors.Add($"The procedure is declared '@mode read' but modifies '{schema}.{name}'.");
                }

                tables.Add((schema, name));
            }
        }

        foreach (var (schema, name) in tables)
        {
            await CheckTableAsync(connection, definition, schema, name, errors, ct).ConfigureAwait(false);
        }

        var parameterTypes = dbParams.ToDictionary(kv => kv.Key, kv => kv.Value.Type, StringComparer.OrdinalIgnoreCase);

        return new ProcedureValidationResult(
            errors.Count == 0,
            errors,
            resultColumns,
            tables.Select(t => $"{t.Schema}.{t.Name}").ToList(),
            parameterTypes);
    }

    private async Task CheckTableAsync(
        DbConnection connection,
        ProcedureDefinition definition,
        string schema,
        string name,
        List<string> errors,
        CancellationToken ct)
    {
        string full = $"{schema}.{name}";
        string quotedTable = $"[{schema}].[{name}]";

        // Every table must be catalogued, otherwise consents and masking cannot be applied.
        if (_tableRepository == null)
        {
            errors.Add($"No catalog available; table '{full}' cannot be governed.");
            return;
        }

        var meta = await _tableRepository.GetTableMetadataAsync(new TableIdentifier("default", schema, name), ct).ConfigureAwait(false);
        if (meta == null || meta.Columns.Count == 0)
        {
            errors.Add($"Table '{full}' is not registered in the data catalog; governance is impossible.");
            return;
        }

        if (definition.RlsMode == ProcedureRlsMode.SessionContext)
        {
            object? policies = await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM sys.security_predicates AS pr " +
                "JOIN sys.security_policies AS sp ON sp.object_id = pr.object_id " +
                "WHERE sp.is_enabled = 1 AND pr.predicate_type = 0 AND pr.target_object_id = OBJECT_ID(@t)",
                ct,
                ("@t", quotedTable)).ConfigureAwait(false);

            int count = policies == null || policies is DBNull ? 0 : Convert.ToInt32(policies, CultureInfo.InvariantCulture);
            if (count == 0)
            {
                errors.Add($"Table '{full}' has no enabled SECURITY POLICY with a FILTER predicate; the procedure would bypass row-level security.");
            }
        }

        // ADR-018: the technical login must not be able to read the table directly (EXECUTE only, ownership chaining).
        object? direct = await ScalarAsync(connection, "SELECT HAS_PERMS_BY_NAME(@t, N'OBJECT', N'SELECT')", ct, ("@t", quotedTable)).ConfigureAwait(false);
        if (direct != null && direct is not DBNull && Convert.ToInt32(direct, CultureInfo.InvariantCulture) == 1)
        {
            errors.Add($"The technical procedure login has direct SELECT permission on '{full}'. It must only hold EXECUTE (ADR-018).");
        }
    }

    private static DbCommand CreateCommand(DbConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandType = CommandType.Text;
        cmd.CommandTimeout = 30;
        foreach (var (pname, value) in parameters)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = pname;
            p.Value = value;
            p.DbType = value is int ? DbType.Int32 : DbType.String;
            cmd.Parameters.Add(p);
        }

        return cmd;
    }

    private static async Task<object?> ScalarAsync(DbConnection connection, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var cmd = CreateCommand(connection, sql, parameters);
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
    }
}
