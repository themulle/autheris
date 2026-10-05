namespace Autheris.Application.Procedures.Services;

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Procedures.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;

/// <summary>
/// F-SQL-02: All SQL Server specific procedure calls. The procedure is invoked with <see cref="CommandType.StoredProcedure"/>
/// and typed parameters; no EXEC text is ever composed. Before the call the security context is written to the
/// read-only SESSION_CONTEXT on the same connection so database-side RLS policies can evaluate it.
/// </summary>
public sealed class MssqlProcedureInvoker : IProcedureInvoker
{
    private static readonly Regex DecimalPrecisionRegex = new(
        @"\((\d+)(?:,(\d+))?\)", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    private readonly ProcedureConnectionProvider _connections;
    private readonly IOptions<GatewayOptions> _options;

    public MssqlProcedureInvoker(ProcedureConnectionProvider connections, IOptions<GatewayOptions> options)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<RawProcedureResult> ExecuteReadAsync(
        ProcedureDefinition definition,
        IReadOnlyDictionary<string, object?> clientValues,
        ProcedureSecurityContext security,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(clientValues);
        ArgumentNullException.ThrowIfNull(security);

        var settings = _options.Value.SqlEndpoints.Procedures;
        var (connection, connOptions) = await _connections.OpenAsync(definition, ct).ConfigureAwait(false);

        await using (connection.ConfigureAwait(false))
        {
            ProcedureConnectionProvider.TryResolveDialect(connOptions.Provider, out var dialect);
            DbTransaction? transaction = null;
            try
            {
                // 1. Session settings + security context on the very same connection.
                if (dialect == Autheris.Domain.Common.DatabaseDialect.SqlServer)
                {
                    // Read-only SESSION_CONTEXT (pool reuse safe: set on every call, sp_reset_connection clears it).
                    await using var init = connection.CreateCommand();
                    init.CommandType = CommandType.Text;
                    init.CommandTimeout = 30;
                    init.CommandText =
                        "SET XACT_ABORT ON; " +
                        "SET LOCK_TIMEOUT " + settings.LockTimeoutMs.ToString(CultureInfo.InvariantCulture) + "; " +
                        "EXEC sys.sp_set_session_context @key = N'autheris.tenant_id', @value = @tenant, @read_only = 1; " +
                        "EXEC sys.sp_set_session_context @key = N'autheris.user_sid', @value = @sid, @read_only = 1; " +
                        "EXEC sys.sp_set_session_context @key = N'autheris.purpose', @value = @purpose, @read_only = 1;";
                    AddParameter(init, "@tenant", DbType.String, security.TenantId);
                    AddParameter(init, "@sid", DbType.String, security.UserSid);
                    AddParameter(init, "@purpose", DbType.String, security.Purpose);
                    await init.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                else if (dialect == Autheris.Domain.Common.DatabaseDialect.PostgreSql)
                {
                    // Review P-6: transaction-local settings (set_config(..., true)) inside the same transaction as the
                    // call. They vanish at the end of the transaction and stay on one server connection even behind a
                    // transaction-pooling proxy (PgBouncer). PostgreSQL has no read-only GUCs; the function must not
                    // overwrite them (DBA review).
                    transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
                    await using var init = connection.CreateCommand();
                    init.Transaction = transaction;
                    init.CommandType = CommandType.Text;
                    init.CommandTimeout = 30;
                    init.CommandText = "SELECT set_config('autheris.tenant_id', @tenant, true), set_config('autheris.user_sid', @sid, true), set_config('autheris.purpose', @purpose, true);";
                    AddParameter(init, "@tenant", DbType.String, security.TenantId);
                    AddParameter(init, "@sid", DbType.String, security.UserSid);
                    AddParameter(init, "@purpose", DbType.String, security.Purpose);
                    await init.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                else if (definition.RlsMode == ProcedureRlsMode.SessionContext)
                {
                    // Review P-6: never run without the security context (the connection provider rejects this too).
                    throw new InvalidOperationException("The database provider cannot carry the security context.");
                }

                // 2. The procedure or table-valued function call.
                await using var cmd = connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandTimeout = Math.Max(1, Math.Min(definition.TimeoutSeconds, settings.MaxTimeoutSeconds));
                TryEnableBindByName(cmd);

                string paramPrefix = dialect == Autheris.Domain.Common.DatabaseDialect.Oracle ? ":" : "@";
                var paramPlaceholders = BuildArguments(cmd, definition, clientValues, security, dialect, paramPrefix);

                EnsureIdentifier(definition.ProcedureName, allowSchema: true);
                if (definition.Kind == ProcedureKind.TableValuedFunction)
                {
                    cmd.CommandType = CommandType.Text;
                    string args = string.Join(", ", paramPlaceholders);
                    cmd.CommandText = dialect == Autheris.Domain.Common.DatabaseDialect.Oracle
                        ? $"SELECT * FROM TABLE({definition.ProcedureName}({args}))"
                        : $"SELECT * FROM {definition.ProcedureName}({args})";
                }
                else if (dialect is Autheris.Domain.Common.DatabaseDialect.Databricks)
                {
                    cmd.CommandType = CommandType.Text;
                    cmd.CommandText = $"CALL {definition.ProcedureName}({string.Join(", ", paramPlaceholders)})";
                }
                else
                {
                    cmd.CommandType = CommandType.StoredProcedure;
                    cmd.CommandText = definition.ProcedureName;
                }

                try
                {
                    await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

                    var columns = new List<string>(reader.FieldCount);
                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        columns.Add(reader.GetName(i));
                    }

                    var rows = new List<object?[]>();
                    bool truncated = false;
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        if (rows.Count >= settings.MaxRows)
                        {
                            truncated = true;
                            TryCancel(cmd);
                            break;
                        }

                        var values = new object?[columns.Count];
                        for (int i = 0; i < values.Length; i++)
                        {
                            values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                        }

                        rows.Add(values);
                    }

                    return new RawProcedureResult(columns, rows, truncated);
                }
                catch (DbException ex) when (TryGetBusinessError(ex, out string? message))
                {
                    throw new ProcedureBusinessException(message!);
                }
            }
            finally
            {
                // Read-only call: the transaction (PostgreSQL) is never committed; disposing it rolls back.
                if (transaction != null)
                {
                    await transaction.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>
    /// Review P-2: binds client and context arguments in the declared argument order. Positional calls (TVF, CALL,
    /// PostgreSQL functions) therefore receive every value at the position the declaration (and, in catalog mode, the
    /// validated signature) defines; a context argument can never be shifted onto a client argument or vice versa.
    /// </summary>
    private static List<string> BuildArguments(
        DbCommand cmd,
        ProcedureDefinition definition,
        IReadOnlyDictionary<string, object?> clientValues,
        ProcedureSecurityContext security,
        Autheris.Domain.Common.DatabaseDialect dialect,
        string paramPrefix)
    {
        var parametersByName = definition.Parameters.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        var bindingsByName = definition.ContextBindings.ToDictionary(b => b.ParameterName, StringComparer.OrdinalIgnoreCase);
        var order = definition.ArgumentOrder;

        if (order.Count != parametersByName.Count + bindingsByName.Count ||
            order.Distinct(StringComparer.OrdinalIgnoreCase).Count() != order.Count ||
            order.Any(n => !parametersByName.ContainsKey(n) && !bindingsByName.ContainsKey(n)))
        {
            throw new InvalidOperationException($"The argument order of '{definition.Name}' does not cover exactly the declared parameters and context bindings.");
        }

        var placeholders = new List<string>(order.Count);
        foreach (string argName in order)
        {
            EnsureIdentifier(argName, allowSchema: false);

            if (bindingsByName.TryGetValue(argName, out var binding))
            {
                string? v = binding.Key switch
                {
                    ProcedureContextKey.TenantId => security.TenantId,
                    ProcedureContextKey.UserSid => security.UserSid,
                    ProcedureContextKey.Purpose => security.Purpose,
                    _ => throw new InvalidOperationException($"Unknown context key '{binding.Key}'.")
                };
                AddParameter(cmd, paramPrefix + argName, DbType.String, v);
                placeholders.Add(paramPrefix + argName);
                continue;
            }

            var p = parametersByName[argName];
            if (clientValues.TryGetValue(p.Name, out var value) && value != null)
            {
                var dbParam = AddParameter(cmd, paramPrefix + p.Name, MapDbType(p.SqlType), value);
                ApplyShape(dbParam, p);
                placeholders.Add(paramPrefix + p.Name);
            }
            else if (definition.Kind == ProcedureKind.TableValuedFunction || dialect != Autheris.Domain.Common.DatabaseDialect.SqlServer)
            {
                // Positional call: keep the position. SQL Server functions use DEFAULT, other dialects an explicit NULL.
                if (definition.Kind == ProcedureKind.TableValuedFunction && dialect == Autheris.Domain.Common.DatabaseDialect.SqlServer)
                {
                    placeholders.Add("DEFAULT");
                }
                else
                {
                    var dbParam = AddParameter(cmd, paramPrefix + p.Name, MapDbType(p.SqlType), DBNull.Value);
                    ApplyShape(dbParam, p);
                    placeholders.Add(paramPrefix + p.Name);
                }
            }

            // SQL Server stored procedure (named parameters): an omitted optional parameter uses its default.
        }

        return placeholders;
    }

    private static readonly Regex IdentifierRegex = new(
        @"^[A-Za-z_][A-Za-z0-9_]{0,127}$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    /// <summary>Review P-4: defense in depth, identifiers are written into SQL text for TVF/CALL.</summary>
    private static void EnsureIdentifier(string value, bool allowSchema)
    {
        var parts = allowSchema ? value.Split('.') : new[] { value };
        if ((allowSchema && parts.Length != 2) || parts.Any(p => !IdentifierRegex.IsMatch(p)))
        {
            throw new InvalidOperationException($"Invalid identifier '{value}'.");
        }
    }

    /// <summary>ODP.NET binds positionally by default; named binding keeps parameters at their declared names.</summary>
    private static void TryEnableBindByName(DbCommand cmd)
    {
        var property = cmd.GetType().GetProperty("BindByName");
        if (property != null && property.CanWrite && property.PropertyType == typeof(bool))
        {
            property.SetValue(cmd, true);
        }
    }

    /// <summary>THROW 50000-59999 (MSSQL) or SQLSTATE P0001 (PostgreSQL) raised by the procedure is a business error; its message is returned (sanitized).</summary>
    internal static bool TryGetBusinessError(DbException ex, out string? message)
    {
        message = null;
        var number = ex.GetType().GetProperty("Number")?.GetValue(ex) as int?;
        if (number is >= 50000 and <= 59999)
        {
            message = SanitizeMessage(ex.Message);
            return true;
        }

        if (ex.GetType().GetProperty("SqlState")?.GetValue(ex) is string sqlState && sqlState == "P0001")
        {
            message = SanitizeMessage(ex.Message);
            return true;
        }

        return false;
    }

    internal static string SanitizeMessage(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return "The operation was rejected.";
        }

        var sb = new StringBuilder(Math.Min(message.Length, 500));
        foreach (char c in message)
        {
            if (sb.Length >= 500)
            {
                break;
            }

            sb.Append(char.IsControl(c) ? ' ' : c);
        }

        return sb.ToString().Trim();
    }

    private static void TryCancel(DbCommand cmd)
    {
        try
        {
            cmd.Cancel();
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or NotSupportedException)
        {
            // best effort: the reader is disposed right after
        }
    }

    private static DbParameter AddParameter(DbCommand cmd, string name, DbType type, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.DbType = type;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
        return p;
    }

    private static void ApplyShape(DbParameter dbParam, ProcedureParameter p)
    {
        if (p.ClrType == typeof(string))
        {
            dbParam.Size = p.MaxLength ?? -1;
        }
        else if (p.ClrType == typeof(decimal) && dbParam is IDbDataParameter dp)
        {
            var m = DecimalPrecisionRegex.Match(p.SqlType);
            if (m.Success)
            {
                dp.Precision = byte.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                dp.Scale = m.Groups[2].Success ? byte.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : (byte)0;
            }
        }
    }

    internal static DbType MapDbType(string sqlType)
    {
        string baseType = sqlType.Contains('(') ? sqlType[..sqlType.IndexOf('(')] : sqlType;
        return baseType switch
        {
            "int" => DbType.Int32,
            "bigint" => DbType.Int64,
            "smallint" => DbType.Int16,
            "tinyint" => DbType.Byte,
            "bit" => DbType.Boolean,
            "decimal" or "numeric" => DbType.Decimal,
            "float" => DbType.Double,
            "real" => DbType.Single,
            "nvarchar" or "nchar" => DbType.String,
            "varchar" or "char" => DbType.AnsiString,
            "uniqueidentifier" => DbType.Guid,
            "date" => DbType.Date,
            "datetime" or "smalldatetime" => DbType.DateTime,
            "datetime2" => DbType.DateTime2,
            "datetimeoffset" => DbType.DateTimeOffset,
            _ => throw new ArgumentException($"Unsupported SQL type '{baseType}'.", nameof(sqlType))
        };
    }
}
