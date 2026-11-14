namespace Autheris.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// Persistence helpers for consent write rules (`CONSENT_WRITE_RULES`, `requested_write_rules_json`, and `WRITE_GRANT_CHANGED` audit).
/// </summary>
internal static class ConsentWriteRulesSql
{
    public const string SelectWriteRulesPrefix =
        "SELECT id, consent_id, operation, row_scope, row_filter_json, max_rows_per_statement, columns_json FROM CONSENT_WRITE_RULES WHERE consent_id IN (";

    public const string InsertWriteRuleSql =
        @"INSERT INTO CONSENT_WRITE_RULES (
            id, tenant_id, consent_id, operation, row_scope, row_filter_json, max_rows_per_statement, columns_json
        ) VALUES (
            @id, @tenantId, @consentId, @op, @scope, @rowFilterJson, @maxRows, @colsJson)";

    private sealed record PersistedRequestedWriteRulesEnvelope(
        bool IncludeReadAccess,
        IReadOnlyList<WriteRule> Rules);

    public static string? SerializeRequestedWriteRules(ConsentRequest request)
    {
        if (request.RequestedWriteRules == null || request.RequestedWriteRules.Count == 0)
        {
            return null;
        }

        return request.IncludeReadAccess
            ? JsonSerializer.Serialize(new PersistedRequestedWriteRulesEnvelope(true, request.RequestedWriteRules))
            : JsonSerializer.Serialize(request.RequestedWriteRules);
    }

    public static (IReadOnlyList<WriteRule> Rules, bool IncludeReadAccess) DeserializeRequestedWriteRules(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return (Array.Empty<WriteRule>(), false);
        }

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            var rules = JsonSerializer.Deserialize<List<WriteRule>>(json) ?? new List<WriteRule>();
            return (rules, false);
        }

        if (doc.RootElement.ValueKind == JsonValueKind.Object)
        {
            var envelope = JsonSerializer.Deserialize<PersistedRequestedWriteRulesEnvelope>(json);
            return envelope != null
                ? (envelope.Rules ?? Array.Empty<WriteRule>(), envelope.IncludeReadAccess)
                : (Array.Empty<WriteRule>(), false);
        }

        throw new InvalidOperationException("Malformed requested_write_rules_json payload.");
    }

    public static async Task InsertWriteRulesAsync(DbConnection conn, DbTransaction tx, Consent consent, CancellationToken ct)
    {
        if (consent.WriteRules == null || consent.WriteRules.Count == 0)
        {
            return;
        }

        var tenantIdStr = StoredTenant.RequireWritable(consent.TenantId);
        foreach (var rule in consent.WriteRules)
        {
            var ruleId = rule.Id == Guid.Empty ? Guid.NewGuid() : rule.Id;
            var colsJson = JsonSerializer.Serialize(rule.Columns ?? Array.Empty<string>());
            var rowFilterJson = rule.RowFilter is { Count: > 0 } ? JsonSerializer.Serialize(rule.RowFilter) : null;

            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = InsertWriteRuleSql;
            cmd.AddBound("@id", ruleId.ToString());
            cmd.AddBound("@tenantId", tenantIdStr);
            cmd.AddBound("@consentId", consent.Id.ToString());
            cmd.AddBound("@op", (int)rule.Operation);
            cmd.AddBound("@scope", (int)rule.RowScope);
            cmd.AddBound("@rowFilterJson", (object?)rowFilterJson ?? DBNull.Value);
            cmd.AddBound("@maxRows", (object?)rule.MaxRowsPerStatement ?? DBNull.Value);
            cmd.AddBound("@colsJson", colsJson);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    public static WriteRule ReadWriteRule(DbDataReader reader)
    {
        var id = Guid.Parse(reader.GetString(0));
        var rawOp = Convert.ToInt32(reader.GetValue(2));
        if (!Enum.IsDefined((WriteOperation)rawOp))
        {
            throw new InvalidOperationException($"Unknown WriteOperation ordinal '{rawOp}' in CONSENT_WRITE_RULES.");
        }

        var rawScope = Convert.ToInt32(reader.GetValue(3));
        if (!Enum.IsDefined((WriteRowScope)rawScope))
        {
            throw new InvalidOperationException($"Unknown WriteRowScope ordinal '{rawScope}' in CONSENT_WRITE_RULES.");
        }

        var rowFilterJson = reader.IsDBNull(4) ? null : reader.GetString(4);
        List<GrantRowScopeEntry>? rowFilter = null;
        if (!string.IsNullOrWhiteSpace(rowFilterJson))
        {
            rowFilter = JsonSerializer.Deserialize<List<GrantRowScopeEntry>>(rowFilterJson);
        }

        long? maxRows = reader.IsDBNull(5) ? null : Convert.ToInt64(reader.GetValue(5));
        var colsJson = reader.GetString(6);
        var columns = JsonSerializer.Deserialize<List<string>>(colsJson) ?? new List<string>();

        return new WriteRule(id, (WriteOperation)rawOp, columns, (WriteRowScope)rawScope, rowFilter, maxRows);
    }

    public static AuditLogEntry BuildWriteGrantChangedAudit(Consent consent, Sid approverSid, string decision = "ALLOW")
    {
        return new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            TenantId = consent.TenantId,
            OccurredAt = DateTimeOffset.UtcNow,
            EventType = AuditEventTypes.WriteGrantChanged,
            ActorSid = approverSid,
            TargetTable = consent.TableIdentifier.ToString(),
            Decision = decision,
            TraceId = Guid.NewGuid().ToString("N"),
            DetailsJson = JsonSerializer.Serialize(new
            {
                ConsentId = consent.Id,
                TableIdentifier = consent.TableIdentifier.ToString(),
                Operations = consent.WriteRules.Select(r => r.Operation.ToString()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                Columns = consent.WriteRules.SelectMany(r => r.Columns).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                RowScopes = consent.WriteRules.Select(r => r.RowScope.ToString()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                ApproverSid = approverSid.Value
            })
        };
    }
}
