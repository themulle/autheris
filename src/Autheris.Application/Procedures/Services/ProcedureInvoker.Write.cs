namespace Autheris.Application.Procedures.Services;

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Procedures.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;

public sealed partial class ProcedureInvoker
{
    /// <summary>
    /// Runs a write procedure (<see cref="ProcedureKind.Procedure"/> only) as a stored procedure call with typed parameters. The security
    /// context goes into the session first. On PostgreSQL the session transaction exists for the session settings only and is committed
    /// after a successful call; on SQL Server the procedure commits itself.
    /// </summary>
    public async Task<ProcedureWriteOutcome> ExecuteWriteAsync(
        ProcedureDefinition definition,
        IReadOnlyDictionary<string, object?> clientValues,
        ProcedureSecurityContext security,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(clientValues);
        ArgumentNullException.ThrowIfNull(security);
        if (definition.Kind != ProcedureKind.Procedure)
        {
            throw new InvalidOperationException("Only stored procedures can be executed as write procedures.");
        }

        var settings = _options.Value.SqlEndpoints.Procedures;
        var (connection, connOptions) = await _connections.OpenAsync(definition, ct).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            if (!DataSourceProvider.TryResolveDialect(connOptions.Provider, out var dialect) || !ProcedureConnectionProvider.IsAdmittedDialect(dialect))
            {
                throw new InvalidOperationException(ProcedureConnectionProvider.UnsupportedDialectMessage(_connections.ResolveName(definition)));
            }

            if (!TenantId.TryParse(security.TenantId, out var tenant))
            {
                throw new GatewayForbiddenException("Invalid tenant identity.");
            }

            DbTransaction? transaction = null;
            try
            {
                if (dialect == DatabaseDialect.SqlServer)
                {
                    await using var init = connection.CreateCommand();
                    init.CommandType = CommandType.Text;
                    init.CommandTimeout = 30;
                    init.CommandText = "SET XACT_ABORT ON; SET LOCK_TIMEOUT " + settings.LockTimeoutMs.ToString(CultureInfo.InvariantCulture) + ";";
                    await init.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    await _sessionInitializer.InitializeSessionAsync(connection, tx: null, dialect, tenant, userSid: security.UserSid, purpose: security.Purpose, ct: ct).ConfigureAwait(false);
                }
                else if (dialect == DatabaseDialect.PostgreSql)
                {
                    transaction = await _sessionInitializer.InitializeSessionAsync(
                        connection, dialect, tenant, userSid: security.UserSid, purpose: security.Purpose, requireTransaction: true, ct: ct).ConfigureAwait(false);
                }

                await using var cmd = connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandTimeout = Math.Max(1, Math.Min(definition.TimeoutSeconds, settings.MaxTimeoutSeconds));
                TryEnableBindByName(cmd);
                BuildArguments(cmd, definition, clientValues, security, dialect, "@");
                EnsureIdentifier(definition.ProcedureName, allowSchema: true);
                cmd.CommandType = CommandType.StoredProcedure;
                cmd.CommandText = definition.ProcedureName;

                try
                {
                    var affected = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    if (transaction != null)
                    {
                        await transaction.CommitAsync(ct).ConfigureAwait(false);
                    }

                    return new ProcedureWriteOutcome(affected >= 0 ? affected : null);
                }
                catch (DbException ex) when (TryGetBusinessError(ex, definition, out int? errorNumber, out string? sqlState))
                {
                    throw new ProcedureBusinessException(errorNumber, sqlState);
                }
            }
            finally
            {
                if (transaction != null)
                {
                    await transaction.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }
}
