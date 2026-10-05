namespace Autheris.Application.Procedures.Services;

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
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
            // 1. Session settings + read-only security context on the very same connection (pool reuse safe:
            //    the context is set on every call and sp_reset_connection clears it on checkout).
            ProcedureConnectionProvider.TryResolveDialect(connOptions.Provider, out var dialect);

            if (dialect == Autheris.Domain.Common.DatabaseDialect.SqlServer)
            {
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
                await using var init = connection.CreateCommand();
                init.CommandType = CommandType.Text;
                init.CommandTimeout = 30;
                init.CommandText = "SELECT set_config('autheris.tenant_id', @tenant, false), set_config('autheris.user_sid', @sid, false), set_config('autheris.purpose', @purpose, false);";
                AddParameter(init, "@tenant", DbType.String, security.TenantId);
                AddParameter(init, "@sid", DbType.String, security.UserSid);
                AddParameter(init, "@purpose", DbType.String, security.Purpose);
                await init.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            // 2. The procedure or table-valued function call.
            await using var cmd = connection.CreateCommand();
            cmd.CommandTimeout = Math.Max(1, Math.Min(definition.TimeoutSeconds, settings.MaxTimeoutSeconds));

            string paramPrefix = dialect == Autheris.Domain.Common.DatabaseDialect.Oracle ? ":" : "@";
            var paramPlaceholders = new List<string>();

            foreach (var p in definition.Parameters)
            {
                if (clientValues.TryGetValue(p.Name, out var value) && value != null)
                {
                    var dbParam = AddParameter(cmd, paramPrefix + p.Name, MapDbType(p.SqlType), value);
                    ApplyShape(dbParam, p);
                    paramPlaceholders.Add(paramPrefix + p.Name);
                }
                else if (definition.Kind == ProcedureKind.TableValuedFunction)
                {
                    if (dialect == Autheris.Domain.Common.DatabaseDialect.SqlServer)
                    {
                        paramPlaceholders.Add("DEFAULT");
                    }
                    else
                    {
                        var dbParam = AddParameter(cmd, paramPrefix + p.Name, MapDbType(p.SqlType), DBNull.Value);
                        ApplyShape(dbParam, p);
                        paramPlaceholders.Add(paramPrefix + p.Name);
                    }
                }
            }

            foreach (var binding in definition.ContextBindings)
            {
                string? v = binding.Key switch
                {
                    ProcedureContextKey.TenantId => security.TenantId,
                    ProcedureContextKey.UserSid => security.UserSid,
                    _ => security.Purpose
                };
                AddParameter(cmd, paramPrefix + binding.ParameterName, DbType.String, v);
                paramPlaceholders.Add(paramPrefix + binding.ParameterName);
            }

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
