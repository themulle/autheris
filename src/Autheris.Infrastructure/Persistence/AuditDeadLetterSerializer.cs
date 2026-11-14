namespace Autheris.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Serialization;
using Autheris.Application.Security;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Diagnostics;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence.Dialects;
using Microsoft.Extensions.Logging;

internal static class AuditDeadLetterSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = CreateOptions();

    public static string FormatSafeReason(Exception? ex, string? fallbackReason = null)
    {
        if (ex == null)
        {
            return string.IsNullOrWhiteSpace(fallbackReason) ? "UnknownError" : fallbackReason;
        }

        string? sqlState = (ex as DbException ?? ex.InnerException as DbException)?.SqlState;
        return !string.IsNullOrWhiteSpace(sqlState)
            ? string.Concat(ex.GetType().Name, " (SqlState=", sqlState, ")")
            : ex.GetType().Name;
    }

    public static string SerializeBatch(IReadOnlyList<AuditLogEntry> batch)
    {
        var redacted = new List<AuditLogEntry>(batch.Count);
        for (int i = 0; i < batch.Count; i++)
        {
            redacted.Add(SanitizeEntry(batch[i], clearChainFields: false));
        }

        return JsonSerializer.Serialize(redacted, JsonOptions);
    }

    public static IReadOnlyList<AuditLogEntry> DeserializeBatch(string batchJson) =>
        JsonSerializer.Deserialize<List<AuditLogEntry>>(batchJson, JsonOptions) ?? [];

    public static AuditLogEntry PrepareReplayedEntry(AuditLogEntry entry) =>
        SanitizeEntry(entry, clearChainFields: true);

    public static List<AuditLogEntry> PrepareReplayBatch(IReadOnlyList<AuditLogEntry> batch)
    {
        var normalized = new List<AuditLogEntry>(batch.Count);
        for (int i = 0; i < batch.Count; i++)
        {
            normalized.Add(PrepareReplayedEntry(batch[i]));
        }
        return normalized;
    }

    public static async Task TryDeadLetterBatchAsync(
        Func<CancellationToken, System.Threading.Tasks.ValueTask<DbConnection>> openConnectionAsync,
        IGovernanceSqlDialect dialect,
        IReadOnlyList<AuditLogEntry> batch,
        Exception failure,
        string fallbackReason,
        int timeoutSeconds,
        ILogger? logger)
    {
        AuditSqlCore.LogFatalBatchFailure(failure, batch.Count, logger);
        string safeReason = FormatSafeReason(failure, fallbackReason);
        bool deadLettered = false;
        using var dlTimeoutCts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            await using var conn = await openConnectionAsync(dlTimeoutCts.Token).ConfigureAwait(false);
            deadLettered = await AuditSqlCore.WriteToDeadLetterAsync(conn, dialect, batch, safeReason, logger, dlTimeoutCts.Token).ConfigureAwait(false);
        }
        catch (Exception dlEx)
        {
            AuditSqlCore.LogDeadLetterFailure(dlEx, batch.Count, logger);
        }

        if (deadLettered) GatewayDiagnostics.AuditDeadLetterCounter.Add(batch.Count);
        else GatewayDiagnostics.AuditLostOnShutdownCounter.Add(batch.Count);
    }

    private static AuditLogEntry SanitizeEntry(AuditLogEntry e, bool clearChainFields) => new()
    {
        Id = e.Id,
        TenantId = e.TenantId,
        OccurredAt = e.OccurredAt,
        EventType = e.EventType,
        ActorSid = e.ActorSid,
        TargetTable = e.TargetTable,
        TargetColumn = e.TargetColumn,
        Query = e.Query != null ? SecretScrubber.Redact(e.Query) : null,
        Decision = e.Decision,
        TraceId = e.TraceId,
        DetailsJson = AuditDetailsBuilder.RedactJson(e.EffectiveDetailsJson, SecretScrubber.Redact),
        Caller = clearChainFields ? e.Caller : null,
        ChainSequence = clearChainFields ? null : e.ChainSequence,
        PrevHash = clearChainFields ? string.Empty : e.PrevHash,
        EntryHash = clearChainFields ? string.Empty : e.EntryHash
    };

    private static JsonSerializerOptions CreateOptions()
    {
        var opts = new JsonSerializerOptions();
        opts.Converters.Add(new TenantIdJsonConverter());
        return opts;
    }

    private sealed class TenantIdJsonConverter : JsonConverter<TenantId>
    {
        public override TenantId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                return StoredTenant.ParseAudit(reader.GetString());
            }

            if (reader.TokenType == JsonTokenType.StartObject)
            {
                string? value = null;
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    if (reader.TokenType != JsonTokenType.PropertyName) continue;
                    var propName = reader.GetString();
                    reader.Read();
                    if (string.Equals(propName, nameof(TenantId.Value), StringComparison.OrdinalIgnoreCase))
                    {
                        value = reader.GetString();
                    }
                    else
                    {
                        reader.Skip();
                    }
                }

                return StoredTenant.ParseAudit(value);
            }

            throw new JsonException("Unexpected token while reading TenantId from dead-letter batch.");
        }

        public override void Write(Utf8JsonWriter writer, TenantId value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString(nameof(TenantId.Value), value.Value);
            writer.WriteEndObject();
        }
    }
}
