namespace Autheris.Application.Governance.Writes;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Security;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Catalog;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.DataSources;
using Autheris.Application.Policy;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.Policy.WriteGrants;
using Autheris.Application.Services;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Domain.Tenancy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Security;

/// <summary>
/// Typed single-table row write service (insert, upsert, updateByPk, deleteByPk) enforcing <see cref="TableWritePolicy"/>,
/// reusing the table's existing SELECT row-level security predicates, server-stamped columns, dedicated write connections,
/// idempotency keys, write budgets, and fail-closed audit logging.
/// </summary>
public sealed class BoundedRowWriteService : IBoundedRowWriteService
{
    private const int MaxIdempotencyKeyLength = 256;
    private static readonly TimeSpan IdempotencyTtl = TimeSpan.FromHours(24);

    private readonly ITableMetadataRepository _metadataRepository;
    private readonly ITableWritePolicyRepository _writePolicies;
    private readonly IAccessDecisionPoint _decisionPoint;
    private readonly IAuditWriter _auditWriter;
    private readonly IOptions<GatewayOptions> _options;
    private readonly ISqlConnectionFactory? _connectionFactory;
    private readonly IDbSessionContextInitializer _sessionInitializer;
    private readonly IProviderExecutionGuard? _executionGuard;
    private readonly IIdempotencyStore? _idempotencyStore;
    private readonly IWriteBudgetStore? _writeBudgetStore;
    private readonly IDataSourceTransportGate? _transportGate;
    private readonly TimeProvider _time;
    private readonly ILogger<BoundedRowWriteService>? _logger;

    private sealed record CachedWriteOutcome(string PayloadHash, BoundedRowWriteResult Result);

    public BoundedRowWriteService(
        ITableMetadataRepository metadataRepository,
        ITableWritePolicyRepository writePolicies,
        IAccessDecisionPoint decisionPoint,
        IAuditWriter auditWriter,
        IOptions<GatewayOptions> options,
        ISqlConnectionFactory? connectionFactory = null,
        IDbSessionContextInitializer? sessionInitializer = null,
        IProviderExecutionGuard? executionGuard = null,
        IIdempotencyStore? idempotencyStore = null,
        IWriteBudgetStore? writeBudgetStore = null,
        IDataSourceTransportGate? transportGate = null,
        TimeProvider? timeProvider = null,
        ILogger<BoundedRowWriteService>? logger = null)
    {
        _metadataRepository = metadataRepository ?? throw new ArgumentNullException(nameof(metadataRepository));
        _writePolicies = writePolicies ?? throw new ArgumentNullException(nameof(writePolicies));
        _decisionPoint = decisionPoint ?? throw new ArgumentNullException(nameof(decisionPoint));
        _auditWriter = auditWriter ?? throw new ArgumentNullException(nameof(auditWriter));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _connectionFactory = connectionFactory;
        _sessionInitializer = sessionInitializer ?? new DbSessionContextInitializer();
        _executionGuard = executionGuard;
        _idempotencyStore = idempotencyStore;
        _writeBudgetStore = writeBudgetStore;
        _transportGate = transportGate;
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger;
    }

    public Task<BoundedRowWriteResult> InsertRowAsync(
        ClaimsPrincipal principal,
        TenantId tenantId,
        TableIdentifier table,
        IReadOnlyDictionary<string, object?> row,
        string idempotencyKey,
        CancellationToken ct = default) =>
        ExecuteAsync(BoundedRowWriteOperation.Insert, principal, tenantId, table, key: null, values: row, idempotencyKey, ct);

    public Task<BoundedRowWriteResult> UpsertRowAsync(
        ClaimsPrincipal principal,
        TenantId tenantId,
        TableIdentifier table,
        IReadOnlyDictionary<string, object?> row,
        string idempotencyKey,
        CancellationToken ct = default) =>
        ExecuteAsync(BoundedRowWriteOperation.Upsert, principal, tenantId, table, key: null, values: row, idempotencyKey, ct);

    public Task<BoundedRowWriteResult> UpdateRowByPkAsync(
        ClaimsPrincipal principal,
        TenantId tenantId,
        TableIdentifier table,
        IReadOnlyDictionary<string, object?> key,
        IReadOnlyDictionary<string, object?> patch,
        string idempotencyKey,
        CancellationToken ct = default) =>
        ExecuteAsync(BoundedRowWriteOperation.UpdateByPk, principal, tenantId, table, key: key, values: patch, idempotencyKey, ct);

    public Task<BoundedRowWriteResult> DeleteRowByPkAsync(
        ClaimsPrincipal principal,
        TenantId tenantId,
        TableIdentifier table,
        IReadOnlyDictionary<string, object?> key,
        string idempotencyKey,
        CancellationToken ct = default) =>
        ExecuteAsync(BoundedRowWriteOperation.DeleteByPk, principal, tenantId, table, key: key, values: null, idempotencyKey, ct);

    private async Task<BoundedRowWriteResult> ExecuteAsync(
        BoundedRowWriteOperation operation,
        ClaimsPrincipal principal,
        TenantId tenantId,
        TableIdentifier table,
        IReadOnlyDictionary<string, object?>? key,
        IReadOnlyDictionary<string, object?>? values,
        string idempotencyKey,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);

        if (principal.Identity?.IsAuthenticated != true)
        {
            throw new BoundedRowWriteException("UNAUTHORIZED", "Authentication is required.");
        }

        if (principal.IsReadOnly())
        {
            throw new BoundedRowWriteException("READ_ONLY_TOKEN", "Read-only tokens cannot execute writes.");
        }

        var validTenant = ValidateTenant(tenantId);
        var secContext = SecurityPrincipalContext.FromPrincipal(principal, validTenant);
        if (!secContext.IsAuthenticated || secContext.TenantClaimInvalid || string.IsNullOrWhiteSpace(secContext.UserSid.Value))
        {
            throw new BoundedRowWriteException("UNAUTHORIZED", "A valid authenticated subject is required.");
        }

        if (secContext.GroupResolutionStatus != GroupResolutionStatus.Complete)
        {
            throw new BoundedRowWriteException("ACCESS_DENIED", "Group resolution is incomplete; writes are denied fail-closed.");
        }

        var trimmedKey = NormalizeIdempotencyKey(idempotencyKey);
        string payloadHash = ComputePayloadHash(operation, validTenant, secContext.UserSid, table, key, values);
        string scopedIdempotencyKey = $"bounded-write:{validTenant.Value}:{secContext.UserSid.Value}:{table}:{operation}:{trimmedKey}";

        if (_idempotencyStore is not null)
        {
            var cached = await _idempotencyStore.GetAsync<CachedWriteOutcome>(scopedIdempotencyKey, ct).ConfigureAwait(false);
            if (cached is not null)
            {
                if (!string.Equals(cached.PayloadHash, payloadHash, StringComparison.Ordinal))
                {
                    throw new BoundedRowWriteException("IDEMPOTENCY_CONFLICT", "The idempotency key was already used with a different request payload.");
                }

                return cached.Result with { Replayed = true };
            }
        }

        var metadata = await _metadataRepository.GetTableMetadataAsync(table, ct).ConfigureAwait(false);
        if (!CatalogTenantOwnership.IsEligibleTargetInTenant(metadata, validTenant) || metadata is null)
        {
            throw new BoundedRowWriteException("TARGET_NOT_FOUND", "Target dataset was not found.");
        }

        var policy = await _writePolicies.GetAsync(validTenant, metadata.Table.Id, ct).ConfigureAwait(false);
        if (policy is null || !policy.AllowsWrites)
        {
            throw new BoundedRowWriteException("TARGET_NOT_WRITABLE", "Target dataset does not have an active table write policy.");
        }

        EnsurePolicyAllowsOperation(policy, operation);

        var pkColumns = metadata.PrimaryKeyColumns;
        if (operation != BoundedRowWriteOperation.Insert && pkColumns.Count == 0)
        {
            throw new BoundedRowWriteException("PRIMARY_KEY_REQUIRED", "Primary key columns must be declared for this write operation.");
        }

        var catalogByName = metadata.Columns.ToDictionary(c => c.ColumnName, StringComparer.OrdinalIgnoreCase);

        // Validate and normalize client-supplied maps, rejecting unknown or server-stamped columns.
        var clientValues = NormalizeColumnMap(values, catalogByName, "row/patch");
        var clientKeys = NormalizeColumnMap(key, catalogByName, "key");
        RejectClientServerStampedColumns(policy, clientValues);

        // Apply server-stamped columns on Insert/Upsert (and never allow client spoofing).
        var effectiveValues = new Dictionary<string, object?>(clientValues, StringComparer.OrdinalIgnoreCase);
        if (operation is BoundedRowWriteOperation.Insert or BoundedRowWriteOperation.Upsert)
        {
            ApplyServerStampedColumns(policy, catalogByName, secContext, validTenant, effectiveValues);
        }

        // Enforce tenant column if present on table.
        string? tenantCol = metadata.TenantColumnName;
        if (!string.IsNullOrWhiteSpace(tenantCol))
        {
            if (effectiveValues.TryGetValue(tenantCol, out var suppliedTenant) &&
                suppliedTenant is not null &&
                !string.Equals(Convert.ToString(suppliedTenant, CultureInfo.InvariantCulture), validTenant.Value, StringComparison.Ordinal))
            {
                throw new BoundedRowWriteException("ACCESS_DENIED", "Tenant column value does not match the caller tenant.");
            }

            if (operation is BoundedRowWriteOperation.Insert or BoundedRowWriteOperation.Upsert)
            {
                effectiveValues[tenantCol] = validTenant.Value;
            }
            else if (operation == BoundedRowWriteOperation.UpdateByPk && effectiveValues.ContainsKey(tenantCol))
            {
                throw new BoundedRowWriteException("VALIDATION_ERROR", "The tenant column cannot be modified by an update.");
            }
        }

        // Extract and validate PK map for UpdateByPk / DeleteByPk / Upsert.
        var orderedPkValues = ExtractPrimaryKeys(operation, pkColumns, clientKeys, effectiveValues);

        // Determine written columns for PDP authorization.
        string[]? writtenColumnNames = operation switch
        {
            BoundedRowWriteOperation.Insert => effectiveValues.Keys.ToArray(),
            BoundedRowWriteOperation.Upsert => effectiveValues.Keys.ToArray(),
            BoundedRowWriteOperation.UpdateByPk => effectiveValues.Keys.ToArray(),
            BoundedRowWriteOperation.DeleteByPk => null,
            _ => null
        };

        if (operation is BoundedRowWriteOperation.Insert or BoundedRowWriteOperation.Upsert or BoundedRowWriteOperation.UpdateByPk &&
            (writtenColumnNames is null || writtenColumnNames.Length == 0))
        {
            throw new BoundedRowWriteException("VALIDATION_ERROR", "At least one column value is required.");
        }

        // Authorize via AccessDecisionPoint (reusing the table's exact SELECT RLS and TableWritePolicy).
        var (decision, updateDecision) = await AuthorizeOperationsAsync(
            secContext, metadata, operation, writtenColumnNames, pkColumns, policy, ct).ConfigureAwait(false);

        // Reject mutating PK, write-protected, server-stamped, or RLS-referenced columns on UpdateByPk.
        var primaryPredicate = ExtractPolicyPredicate(decision);
        var rlsPolicyColumns = primaryPredicate?.ReferencedColumns
            ?? ImmutableHashSet<string>.Empty;
        var updateProtected = BuildUpdateProtectedColumns(metadata, policy, pkColumns, rlsPolicyColumns);

        if (operation == BoundedRowWriteOperation.UpdateByPk)
        {
            foreach (var col in effectiveValues.Keys)
            {
                if (updateProtected.Contains(col))
                {
                    throw new BoundedRowWriteException("ACCESS_DENIED", "Primary key, write-protected, server-stamped, or row-policy columns cannot be updated.");
                }
            }
        }

        // Resolve data source connection and dialect (must have dedicated WriteConnectionString).
        string dsName = metadata.Table.SourceName;
        if (_transportGate is not null)
        {
            await _transportGate.EnsureCatalogSourceAsync(dsName, ct).ConfigureAwait(false);
        }

        var connOptions = SqlDataSourceResolver.ResolveConnectionOptions(_options.Value, dsName);
        if (connOptions is null || string.IsNullOrWhiteSpace(connOptions.WriteConnectionString))
        {
            throw new BoundedRowWriteException("DML_WRITE_CONNECTION_MISSING", "The target data source does not have a dedicated write connection configured.");
        }

        if (!GovernedSqlExecutionService.TryMapProviderToDialect(connOptions.Provider, out var dbDialect))
        {
            throw new BoundedRowWriteException("NOT_IMPLEMENTED", "The target data source dialect is not supported for bounded writes.");
        }

        var targetDialect = MapToEngineDialect(dbDialect);

        // Pre-execution SELECT-RLS check for Insert and Upsert BEFORE touching the database!
        if (operation is BoundedRowWriteOperation.Insert or BoundedRowWriteOperation.Upsert &&
            decision.HasRowFilter)
        {
            if (primaryPredicate is null)
            {
                await TryAuditDenialAsync(validTenant, secContext.UserSid, table, operation, "ROW_POLICY_EXPRESSION_NOT_ALLOWED", trimmedKey, ct).ConfigureAwait(false);
                throw new BoundedRowWriteException("ROW_POLICY_EXPRESSION_NOT_ALLOWED", "The row-level security policy must be a typed predicate for pre-execution verification.");
            }

            try
            {
                PreExecutionRowCheck.ValidateRowMap(
                    primaryPredicate.Expression,
                    primaryPredicate.ReferencedColumns,
                    primaryPredicate.Parameters,
                    effectiveValues,
                    targetDialect);
            }
            catch (DmlCheckOptionViolationException ex)
            {
                await TryAuditDenialAsync(validTenant, secContext.UserSid, table, operation, "ROW_CHECK_VIOLATION", trimmedKey, ct).ConfigureAwait(false);
                throw new BoundedRowWriteException("ROW_CHECK_VIOLATION", "The written row does not satisfy the row-level security policy.", ex);
            }
            catch (SqlCompileNotSupportedException ex)
            {
                await TryAuditDenialAsync(validTenant, secContext.UserSid, table, operation, "ROW_POLICY_EXPRESSION_NOT_ALLOWED", trimmedKey, ct).ConfigureAwait(false);
                throw new BoundedRowWriteException("ROW_POLICY_EXPRESSION_NOT_ALLOWED", "The row-level security policy cannot be evaluated pre-execution for this write.", ex);
            }
            catch (SecurityException ex)
            {
                await TryAuditDenialAsync(validTenant, secContext.UserSid, table, operation, "ROW_CHECK_VIOLATION", trimmedKey, ct).ConfigureAwait(false);
                throw new BoundedRowWriteException("ROW_CHECK_VIOLATION", "The written row does not satisfy the row-level security policy.", ex);
            }
        }

        // Enforce write rate/row budget.
        await AcquireWriteBudgetAsync(validTenant, secContext.UserSid.Value, decision.WriteGrant?.MaxRowsPerStatement ?? policy.MaxRowsPerStatement, ct).ConfigureAwait(false);

        // Build parameterized SQL strictly from catalog identifiers & SELECT RLS predicate AST.
        var orderedEffectiveValues = effectiveValues
            .OrderBy(static kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var effectiveDecision = updateDecision ?? decision;
        var effectiveRowPolicy = ExtractPolicyPredicate(effectiveDecision);
        if (effectiveDecision.HasRowFilter && effectiveRowPolicy is null)
        {
            throw new BoundedRowWriteException("ROW_POLICY_EXPRESSION_NOT_ALLOWED", "A typed row-level security predicate is required for bounded writes.");
        }

        BuiltBoundedWriteCommand builtCommand;
        try
        {
            builtCommand = operation switch
            {
                BoundedRowWriteOperation.Insert => BoundedWriteSqlBuilder.BuildInsert(targetDialect, metadata, orderedEffectiveValues),
                BoundedRowWriteOperation.Upsert => BoundedWriteSqlBuilder.BuildUpsert(
                    targetDialect, metadata, orderedEffectiveValues, pkColumns, updateProtected, tenantCol, validTenant, effectiveRowPolicy),
                BoundedRowWriteOperation.UpdateByPk => BoundedWriteSqlBuilder.BuildUpdateByPk(
                    targetDialect, metadata, orderedPkValues, orderedEffectiveValues, tenantCol, validTenant, effectiveRowPolicy),
                BoundedRowWriteOperation.DeleteByPk => BoundedWriteSqlBuilder.BuildDeleteByPk(
                    targetDialect, metadata, orderedPkValues, tenantCol, validTenant, effectiveRowPolicy),
                _ => throw new BoundedRowWriteException("VALIDATION_ERROR", "Unsupported write operation.")
            };
        }
        catch (SqlCompileNotSupportedException ex)
        {
            throw new BoundedRowWriteException("NOT_IMPLEMENTED", "The write operation is not supported for the target dialect.", ex);
        }
        catch (SecurityException ex)
        {
            throw new BoundedRowWriteException("ACCESS_DENIED", "The write operation violates a security constraint.", ex);
        }

        if (_connectionFactory is null)
        {
            throw new BoundedRowWriteException("UNAVAILABLE", "SQL connection factory is unavailable.");
        }

        var writeConnOptions = connOptions.ForWriteExecution(connOptions.WriteConnectionString);
        int affectedRows = await ExecuteBuiltCommandAsync(
            writeConnOptions, dbDialect, targetDialect, builtCommand, validTenant, secContext.UserSid, table, operation, trimmedKey, ct).ConfigureAwait(false);

        var result = new BoundedRowWriteResult(
            table.ToString(),
            operation.ToString(),
            affectedRows,
            Replayed: false,
            trimmedKey);

        if (_idempotencyStore is not null)
        {
            await _idempotencyStore.SetIfNotExistsAsync(
                scopedIdempotencyKey,
                new CachedWriteOutcome(payloadHash, result),
                IdempotencyTtl,
                ct).ConfigureAwait(false);
        }

        return result;
    }

    private async Task<int> ExecuteBuiltCommandAsync(
        DataSourceConnectionOptions writeConnOptions,
        DatabaseDialect dbDialect,
        TargetSqlDialect targetDialect,
        BuiltBoundedWriteCommand builtCommand,
        TenantId tenantId,
        Sid userSid,
        TableIdentifier table,
        BoundedRowWriteOperation operation,
        string idempotencyKey,
        CancellationToken ct)
    {
        int timeoutSeconds = Math.Max(1, _options.Value.WebSql.ExecutionTimeoutSeconds);
        bool isDatabricks = targetDialect == TargetSqlDialect.Databricks;

        if (isDatabricks)
        {
            // Non-transactional Delta: record fail-closed pre-write audit intent before touching Databricks.
            await RecordWriteAuditAsync(tenantId, userSid, table, operation, "ALLOW_INTENT", null, idempotencyKey, ct).ConfigureAwait(false);
        }

        await using var connection = await _connectionFactory!.CreateOpenConnectionAsync(writeConnOptions, ct).ConfigureAwait(false);
        DbTransaction? tx = null;
        try
        {
            tx = await _sessionInitializer.InitializeSessionAsync(
                connection,
                dbDialect,
                tenantId,
                userSid: userSid.Value,
                purpose: "bounded_row_write",
                requireTransaction: !isDatabricks,
                ct: ct).ConfigureAwait(false);

            if (!isDatabricks && tx is null)
            {
                tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            }

            async Task<int> RunCommandAsync()
            {
                await using var command = connection.CreateCommand();
                if (tx is not null)
                {
                    command.Transaction = tx;
                }

                command.CommandTimeout = timeoutSeconds;
                command.CommandText = builtCommand.Sql;
                foreach (var p in builtCommand.Parameters)
                {
                    var dbParam = command.CreateParameter();
                    if (!string.IsNullOrEmpty(p.ParameterName))
                    {
                        dbParam.ParameterName = p.ParameterName;
                    }

                    dbParam.Value = p.Value ?? DBNull.Value;
                    command.Parameters.Add(dbParam);
                }

                return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            int rawAffected = _executionGuard is not null
                ? await _executionGuard.RunAsync(connection, TimeSpan.FromSeconds(timeoutSeconds), RunCommandAsync, ct).ConfigureAwait(false)
                : await RunCommandAsync().ConfigureAwait(false);

            int accountedRows = rawAffected >= 0 ? rawAffected : 1;

            if (_writeBudgetStore is not null)
            {
                bool withinBudget = await _writeBudgetStore
                    .TryRecordAffectedRowsAsync(tenantId, userSid.Value, accountedRows, _options.Value.WebSql.WriteRowsPerHour, ct)
                    .ConfigureAwait(false);
                if (!withinBudget)
                {
                    throw new BoundedRowWriteException("WRITE_BUDGET_EXHAUSTED", "Hourly write row budget exceeded.");
                }
            }

            // Fail-closed outcome audit BEFORE commit on transactional engines (rolls back if audit fails).
            await RecordWriteAuditAsync(tenantId, userSid, table, operation, "ALLOW", rawAffected, idempotencyKey, ct).ConfigureAwait(false);

            if (tx is not null)
            {
                await tx.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            }

            return rawAffected;
        }
        catch
        {
            if (tx is not null)
            {
                try
                {
                    await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception rbEx)
                {
                    _logger?.LogWarning(rbEx, "Rollback failed after bounded write error on {Table}.", table);
                }
            }

            throw;
        }
        finally
        {
            if (tx is not null)
            {
                await tx.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<(TableAccessDecision Primary, TableAccessDecision? Update)> AuthorizeOperationsAsync(
        SecurityPrincipalContext secContext,
        TableMetadata metadata,
        BoundedRowWriteOperation operation,
        string[]? writtenColumns,
        IReadOnlyList<string> pkColumns,
        TableWritePolicy policy,
        CancellationToken ct)
    {
        if (operation == BoundedRowWriteOperation.Upsert)
        {
            var insertDecision = await _decisionPoint.DecideAsync(
                new AccessRequest(secContext, metadata, AccessOperation.Insert, AccessTransport.GraphQl, Columns: writtenColumns), ct).ConfigureAwait(false);
            EnsureAllowed(insertDecision);

            // For the UPDATE branch of Upsert, check only non-key, non-server-stamped, non-RLS columns.
            var pkSet = new HashSet<string>(pkColumns, StringComparer.OrdinalIgnoreCase);
            var stampedSet = policy.ServerStampedColumns is { Count: > 0 }
                ? new HashSet<string>(policy.ServerStampedColumns.Keys, StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var rlsCols = ExtractPolicyPredicate(insertDecision)?.ReferencedColumns ?? ImmutableHashSet<string>.Empty;
            var updateCols = (writtenColumns ?? [])
                .Where(c => !pkSet.Contains(c)
                    && !stampedSet.Contains(c)
                    && !rlsCols.Contains(c.ToLowerInvariant())
                    && (string.IsNullOrWhiteSpace(metadata.TenantColumnName) || !string.Equals(c, metadata.TenantColumnName, StringComparison.OrdinalIgnoreCase)))
                .ToArray();

            var updateDecision = await _decisionPoint.DecideAsync(
                new AccessRequest(secContext, metadata, AccessOperation.Update, AccessTransport.GraphQl, Columns: updateCols.Length > 0 ? updateCols : null), ct).ConfigureAwait(false);
            EnsureAllowed(updateDecision);

            return (insertDecision, updateDecision);
        }

        var accessOp = operation switch
        {
            BoundedRowWriteOperation.Insert => AccessOperation.Insert,
            BoundedRowWriteOperation.UpdateByPk => AccessOperation.Update,
            BoundedRowWriteOperation.DeleteByPk => AccessOperation.Delete,
            _ => throw new BoundedRowWriteException("VALIDATION_ERROR", "Unsupported operation.")
        };

        var decision = await _decisionPoint.DecideAsync(
            new AccessRequest(secContext, metadata, accessOp, AccessTransport.GraphQl, Columns: writtenColumns), ct).ConfigureAwait(false);
        EnsureAllowed(decision);
        return (decision, null);
    }

    private static TrinoSqlEngine.Governance.PolicyPredicate? ExtractPolicyPredicate(TableAccessDecision decision) =>
        (decision.RowPolicy as PolicyPredicateRowPolicy)?.Predicate;

    private static void EnsureAllowed(TableAccessDecision decision)
    {
        if (decision.IsAllowed && decision.WriteGrant is not null)
        {
            return;
        }

        if (decision.Reasons.Any(r => string.Equals(r.Code, "REQUIRED_ROW_FILTER_MISSING", StringComparison.OrdinalIgnoreCase)))
        {
            throw new BoundedRowWriteException("REQUIRED_ROW_FILTER_MISSING", "This table requires an active row-level security filter before writes are permitted.");
        }

        throw new BoundedRowWriteException("ACCESS_DENIED", "Write access to the target dataset is denied.");
    }

    private async Task AcquireWriteBudgetAsync(TenantId tenantId, string subjectSid, long? policyMaxRows, CancellationToken ct)
    {
        if (_writeBudgetStore is null) return;
        var webSql = _options.Value.WebSql;
        var limit = new WriteBudgetLimit(
            webSql.WriteStatementsPerMinute,
            webSql.WriteRowsPerHour,
            policyMaxRows,
            webSql.MaxAffectedRows);

        var acq = await _writeBudgetStore.TryAcquireStatementAsync(tenantId, subjectSid, limit, ct).ConfigureAwait(false);
        if (!acq.Allowed)
        {
            throw new BoundedRowWriteException("WRITE_BUDGET_EXHAUSTED", "Write statement rate limit exceeded.");
        }
    }

    private static void EnsurePolicyAllowsOperation(TableWritePolicy policy, BoundedRowWriteOperation operation)
    {
        bool allowed = operation switch
        {
            BoundedRowWriteOperation.Insert => policy.AllowsOperation(WriteOperation.Insert),
            BoundedRowWriteOperation.Upsert => policy.AllowsOperation(WriteOperation.Insert) && policy.AllowsOperation(WriteOperation.Update),
            BoundedRowWriteOperation.UpdateByPk => policy.AllowsOperation(WriteOperation.Update),
            BoundedRowWriteOperation.DeleteByPk => policy.AllowsOperation(WriteOperation.Delete),
            _ => false
        };

        if (!allowed)
        {
            throw new BoundedRowWriteException("TARGET_NOT_WRITABLE", "The requested write operation is not permitted by the table write policy.");
        }
    }

    private static Dictionary<string, object?> NormalizeColumnMap(
        IReadOnlyDictionary<string, object?>? source,
        Dictionary<string, TableColumn> catalogByName,
        string label)
    {
        var normalized = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (source is null) return normalized;

        foreach (var (rawKey, val) in source)
        {
            if (string.IsNullOrWhiteSpace(rawKey))
            {
                throw new BoundedRowWriteException("VALIDATION_ERROR", $"Empty column name in {label}.");
            }

            if (!catalogByName.TryGetValue(rawKey.Trim(), out var col))
            {
                throw new BoundedRowWriteException("INVALID_COLUMNS", $"Column '{rawKey}' is not a cataloged column of the target table.");
            }

            normalized[col.ColumnName] = val;
        }

        return normalized;
    }

    private static void RejectClientServerStampedColumns(TableWritePolicy policy, Dictionary<string, object?> clientValues)
    {
        if (policy.ServerStampedColumns is null || policy.ServerStampedColumns.Count == 0)
        {
            return;
        }

        foreach (var stampedCol in policy.ServerStampedColumns.Keys)
        {
            if (clientValues.ContainsKey(stampedCol))
            {
                throw new BoundedRowWriteException("SERVER_STAMPED_COLUMN_REJECTED", "Server-stamped columns cannot be supplied by the client.");
            }
        }
    }

    private void ApplyServerStampedColumns(
        TableWritePolicy policy,
        Dictionary<string, TableColumn> catalogByName,
        SecurityPrincipalContext secContext,
        TenantId tenantId,
        Dictionary<string, object?> targetValues)
    {
        if (policy.ServerStampedColumns is null || policy.ServerStampedColumns.Count == 0)
        {
            return;
        }

        foreach (var (colName, stampKind) in policy.ServerStampedColumns)
        {
            if (!catalogByName.TryGetValue(colName, out var col))
            {
                throw new BoundedRowWriteException("WRITE_POLICY_INVALID", "Configured server-stamped column does not exist on the target table.");
            }

            object? stampedValue = TableWriteServerStampKinds.Normalize(stampKind) switch
            {
                TableWriteServerStampKinds.CallerSid => secContext.UserSid.Value,
                TableWriteServerStampKinds.TenantId => tenantId.Value,
                TableWriteServerStampKinds.UtcNow => _time.GetUtcNow().UtcDateTime,
                TableWriteServerStampKinds.PrimaryGroupSid => secContext.GroupSids.Count > 0
                    ? secContext.GroupSids.OrderBy(static s => s.Value, StringComparer.Ordinal).First().Value
                    : throw new BoundedRowWriteException("ACCESS_DENIED", "Caller has no primary group SID for server-stamped ownership column."),
                _ => throw new BoundedRowWriteException("WRITE_POLICY_INVALID", "Unsupported server stamp kind.")
            };

            targetValues[col.ColumnName] = stampedValue;
        }
    }

    private static IReadOnlyList<KeyValuePair<string, object?>> ExtractPrimaryKeys(
        BoundedRowWriteOperation operation,
        IReadOnlyList<string> pkColumns,
        Dictionary<string, object?> clientKeys,
        Dictionary<string, object?> effectiveValues)
    {
        if (operation == BoundedRowWriteOperation.Insert)
        {
            return Array.Empty<KeyValuePair<string, object?>>();
        }

        var source = operation == BoundedRowWriteOperation.Upsert ? effectiveValues : clientKeys;
        var pkList = new List<KeyValuePair<string, object?>>(pkColumns.Count);
        foreach (var pk in pkColumns)
        {
            if (!source.TryGetValue(pk, out var val) || val is null)
            {
                throw new BoundedRowWriteException("PRIMARY_KEY_REQUIRED", "Every declared primary key column must be provided with a non-null value.");
            }

            pkList.Add(new KeyValuePair<string, object?>(pk, val));
        }

        return pkList;
    }

    private static HashSet<string> BuildUpdateProtectedColumns(
        TableMetadata metadata,
        TableWritePolicy policy,
        IReadOnlyList<string> pkColumns,
        IReadOnlySet<string> rlsPolicyColumns)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pk in pkColumns) set.Add(pk);
        foreach (var col in metadata.Columns)
        {
            if (col.WriteProtected) set.Add(col.ColumnName);
        }

        if (policy.ServerStampedColumns is { Count: > 0 })
        {
            foreach (var col in policy.ServerStampedColumns.Keys) set.Add(col);
        }

        foreach (var rlsCol in rlsPolicyColumns) set.Add(rlsCol);
        if (!string.IsNullOrWhiteSpace(metadata.TenantColumnName)) set.Add(metadata.TenantColumnName);
        return set;
    }

    private static TenantId ValidateTenant(TenantId tenant)
    {
        if (string.IsNullOrWhiteSpace(tenant.Value) || TenantId.IsReserved(tenant))
        {
            throw new BoundedRowWriteException("INVALID_TENANT", "A valid tenant identifier is required.");
        }

        try
        {
            return new TenantId(StoredTenant.RequireWritable(tenant));
        }
        catch (ArgumentException)
        {
            throw new BoundedRowWriteException("INVALID_TENANT", "A valid writable tenant identifier is required.");
        }
    }

    private static string NormalizeIdempotencyKey(string? key)
    {
        var trimmed = key?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) || trimmed.Length > MaxIdempotencyKeyLength)
        {
            throw new BoundedRowWriteException("IDEMPOTENCY_KEY_REQUIRED", "A non-empty idempotency key (max 256 characters) is required.");
        }

        return trimmed;
    }

    private static string ComputePayloadHash(
        BoundedRowWriteOperation operation,
        TenantId tenantId,
        Sid userSid,
        TableIdentifier table,
        IReadOnlyDictionary<string, object?>? key,
        IReadOnlyDictionary<string, object?>? values)
    {
        var canonical = new
        {
            op = operation.ToString(),
            tenant = tenantId.Value,
            user = userSid.Value,
            table = table.ToString(),
            key = key?.OrderBy(static k => k.Key, StringComparer.OrdinalIgnoreCase).Select(static k => new { k.Key, Value = Convert.ToString(k.Value, CultureInfo.InvariantCulture) }),
            values = values?.OrderBy(static v => v.Key, StringComparer.OrdinalIgnoreCase).Select(static v => new { v.Key, Value = Convert.ToString(v.Value, CultureInfo.InvariantCulture) })
        };

        var json = JsonSerializer.Serialize(canonical);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    private static TargetSqlDialect MapToEngineDialect(DatabaseDialect dialect) => dialect switch
    {
        DatabaseDialect.PostgreSql => TargetSqlDialect.PostgreSql,
        DatabaseDialect.SqlServer => TargetSqlDialect.SqlServer,
        DatabaseDialect.Oracle => TargetSqlDialect.Oracle,
        DatabaseDialect.Databricks => TargetSqlDialect.Databricks,
        _ => throw new BoundedRowWriteException("NOT_IMPLEMENTED", "Unsupported target database dialect.")
    };

    private async Task RecordWriteAuditAsync(
        TenantId tenantId,
        Sid actorSid,
        TableIdentifier table,
        BoundedRowWriteOperation operation,
        string decision,
        int? affectedRows,
        string idempotencyKey,
        CancellationToken ct)
    {
        string keyDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey)))[..16];
        await _auditWriter.RecordRequiredAsync(new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            OccurredAt = _time.GetUtcNow().ToUniversalTime(),
            EventType = AuditEventTypes.WebSqlDmlExecuted,
            ActorSid = actorSid,
            TargetTable = table.ToString(),
            Decision = decision,
            TraceId = System.Diagnostics.Activity.Current?.Id ?? Guid.NewGuid().ToString("N"),
            TenantId = tenantId,
            DetailsJson = JsonSerializer.Serialize(new
            {
                transport = "GraphQL",
                operation = operation.ToString(),
                affectedRows,
                idempotencyKeyHash = keyDigest
            })
        }, ct).ConfigureAwait(false);
    }

    private async Task TryAuditDenialAsync(
        TenantId tenantId,
        Sid actorSid,
        TableIdentifier table,
        BoundedRowWriteOperation operation,
        string reasonCode,
        string idempotencyKey,
        CancellationToken ct)
    {
        try
        {
            string keyDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey)))[..16];
            await _auditWriter.RecordRequiredAsync(new AuditLogEntry
            {
                Id = Guid.NewGuid(),
                OccurredAt = _time.GetUtcNow().ToUniversalTime(),
                EventType = AuditEventTypes.WebSqlDmlRejected,
                ActorSid = actorSid,
                TargetTable = table.ToString(),
                Decision = "DENY",
                TraceId = System.Diagnostics.Activity.Current?.Id ?? Guid.NewGuid().ToString("N"),
                TenantId = tenantId,
                DetailsJson = JsonSerializer.Serialize(new
                {
                    transport = "GraphQL",
                    operation = operation.ToString(),
                    reason = reasonCode,
                    idempotencyKeyHash = keyDigest
                })
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to record bounded write denial audit for {Table}.", table);
        }
    }
}
