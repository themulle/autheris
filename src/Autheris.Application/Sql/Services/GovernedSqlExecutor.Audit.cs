namespace Autheris.Application.Sql.Services;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Common;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Sql.Internal;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TrinoSqlEngine.Ast.Emit;

public sealed partial class GovernedSqlExecutor
{
    private async Task RecordDmlAuditAsync(
        TenantId tenantId,
        ClaimsPrincipal user,
        string dsName,
        DmlAuditContext dmlContext,
        string eventType,
        string decision,
        CompiledSql? compiled,
        int? affectedRows,
        string? reason,
        bool synthetic,
        string? originalSql = null,
        IReadOnlyDictionary<string, object?>? clientValues = null)
    {
        var fields = new Dictionary<string, object?>
        {
            ["statementType"] = dmlContext.StatementType?.ToString(),
            ["tables"] = dmlContext.Tables,
            ["dataSource"] = dsName,
            ["affectedRows"] = affectedRows,
            ["maxAffectedRows"] = _options.Value.WebSql.MaxAffectedRows,
            ["reason"] = reason,
            ["synthetic"] = synthetic
        };
        if (compiled is null && dmlContext.HasRecursiveCte)
        {
            fields["recursiveCte"] = true;   // a rejection before the compile still carries the flag
        }

        if (compiled is not null)
        {
            AddCompiledAuditFields(fields, compiled);
            if (dmlContext.IsDml)
            {
                // The keyed digest of the new values: it ties the record to the exact values written without storing any of them.
                fields["sql.bound_values_digest"] = _digester.ComputeBoundValues(compiled, clientValues);
            }
        }
        else if (originalSql is not null)
        {
            // A statement rejected before it compiled has no digest; the hash of its (literal-free) original text identifies it.
            fields["originalSqlHash"] = _digester.Anonymize(originalSql).OriginalSqlHash;
        }

        await _auditWriter.RecordRequiredAsync(new AuditLogEntry
        {
            TenantId = tenantId,
            EventType = eventType,
            ActorSid = user.GetUserSid() ?? new Sid("anonymous"),
            TargetTable = string.Join(",", dmlContext.Tables),
            Decision = decision,
            TraceId = TraceContextResolver.GetCurrentTraceId(),
            DetailsJson = System.Text.Json.JsonSerializer.Serialize(fields)
            // M-1: a DML audit never takes the request token. The statement may already be committed; a client that disconnects must not
            // leave it without its audit record.
        }, CancellationToken.None).ConfigureAwait(false);
    }

    private static void AddListAuditField(Dictionary<string, object?> fields, CompiledSql compiled, IReadOnlyDictionary<string, object?>? clientValues)
    {
        SortedDictionary<string, int>? lists = null;
        foreach (var slot in compiled.Parameters)
        {
            if (slot is { Origin: ParameterOrigin.ClientNamed, ListCount: { } count, SourceName: { } name })
            {
                (lists ??= new SortedDictionary<string, int>(StringComparer.Ordinal))[name] = count;
            }
        }

        if (lists is not null)
        {
            fields["list_params"] = lists;
        }

        if (clientValues is not null)
        {
            SortedSet<string>? empty = null;
            foreach (var (name, value) in clientValues)
            {
                if (value is ClientListValue { Count: 0 } && SqlIdentifierGuard.IsValidIdentifier(name, maxLength: 64))
                {
                    (empty ??= new SortedSet<string>(StringComparer.Ordinal)).Add(name.ToLowerInvariant());
                }
            }

            if (empty is not null)
            {
                fields["list_params_empty"] = empty.ToArray();
            }
        }
    }

    private void AddCompiledAuditFields(Dictionary<string, object?> fields, CompiledSql compiled, IReadOnlyDictionary<string, object?>? clientValues = null)
    {
        fields["compiler_version"] = compiled.CompilerVersion;
        fields["dialect"] = compiled.Dialect.ToString();
        fields["bind_count"] = compiled.Parameters.Length;
        if (compiled.HasRecursiveCte)
        {
            fields["recursiveCte"] = true;
        }

        AddListAuditField(fields, compiled, clientValues);
        fields["sql.compiled_digest"] = _digester.Compute(compiled);
        if (_options.Value.Audit.StoreCompiledSql)
        {
            fields["compiledSql"] = compiled.Sql;
        }
    }

    internal static void AddDatabricksAuditFields(
        Dictionary<string, object?> fields,
        Autheris.Domain.Options.DatabricksDataSourceOptions db,
        TenantId tenantId,
        IKeyVaultSecretProvider? secrets = null,
        IHostEnvironment? environment = null,
        ILogger? logger = null)
    {
        fields["auth.mode"] = db.Auth.Mode.ToString();
        if (db.Auth.Mode == Autheris.Domain.Options.DatabricksAuthMode.OAuthM2M)
        {
            var clientId = db.Auth.Tenants.TryGetValue(tenantId.Value, out var tp) && !string.IsNullOrWhiteSpace(tp.ClientId)
                ? tp.ClientId
                : db.Auth.ClientId;
            if (!string.IsNullOrWhiteSpace(clientId))
            {
                fields["auth.clientId"] = clientId;
            }
        }
        else if (db.Auth.Mode == Autheris.Domain.Options.DatabricksAuthMode.Pat)
        {
            var patRef = db.Auth.Tenants.TryGetValue(tenantId.Value, out var tp) && !string.IsNullOrWhiteSpace(tp.PatRef)
                ? tp.PatRef
                : db.Auth.PatRef;
            if (!string.IsNullOrWhiteSpace(patRef))
            {
                var pat = Autheris.Application.Security.SecretReferenceResolver.Resolve(secrets, patRef, environment, allowPlaintextInDevelopment: false, logger, "Databricks PAT");
                if (!string.IsNullOrWhiteSpace(pat))
                {
                    var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(pat)));
                    fields["auth.tokenFingerprint"] = hash[..16].ToLowerInvariant();
                }
            }
        }
    }
}
