namespace Autheris.Application.DataSources;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Domain.Audit;
using Autheris.Domain.Diagnostics;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Options.DataSources;

/// <summary>A refused transport: the caller-visible logical name, the canonical (mapped) name used for the lookup, and the reason.</summary>
public sealed record TransportDenial(
    string? LogicalName,
    string? CanonicalName,
    DataSourceTransportFlags Transport,
    TransportDecisionReason Reason);

/// <summary>Issue #447 (REQ-ATR-05, SEC-ATR-09): writes one audit event and one metric increment per denial; never throws.</summary>
public interface ITransportDenialRecorder
{
    Task RecordDenialAsync(TransportDenial denial, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class TransportDenialRecorder : ITransportDenialRecorder
{
    internal const string UnknownDataSourceTag = "unknown";

    private readonly IAuditWriter _auditWriter;
    private readonly AuditContext _auditContext;
    private readonly IDataSourceOptionsResolver _resolver;

    public TransportDenialRecorder(IAuditWriter auditWriter, AuditContext auditContext, IDataSourceOptionsResolver resolver)
    {
        _auditWriter = auditWriter ?? throw new ArgumentNullException(nameof(auditWriter));
        _auditContext = auditContext ?? throw new ArgumentNullException(nameof(auditContext));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    /// <inheritdoc />
    public async Task RecordDenialAsync(TransportDenial denial, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(denial);

        var transportTag = denial.Transport.ToString();
        GatewayDiagnostics.TransportDenied.Add(
            1,
            new KeyValuePair<string, object?>("transport", transportTag),
            new KeyValuePair<string, object?>("datasource", ResolveMetricSourceTag(denial.CanonicalName)));

        try
        {
            var logical = TransportNotAllowedException.SanitizeName(denial.LogicalName);
            var entry = new AuditLogEntry
            {
                TenantId = _auditContext.TenantId,
                EventType = AuditEventTypes.TransportNotAllowed,
                ActorSid = _auditContext.ActorSid,
                TargetTable = logical,
                Decision = "DENY",
                TraceId = TransportNotAllowedException.SanitizeName(_auditContext.TraceId),
                DetailsJson = JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["transport"] = transportTag,
                    ["dataSource"] = logical,
                    ["reason"] = "TransportNotAllowed",
                    ["subReason"] = denial.Reason.ToString(),
                    ["outcome"] = "DENY"
                })
            };

            await _auditWriter.RecordAsync(entry, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // SEC-ATR-09: the denial still throws. A failed write is counted by IAuditWriter (autheris_audit_write_failed_total);
            // no exception text is recorded here.
        }
    }

    private string ResolveMetricSourceTag(string? canonicalName)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(canonicalName) && _resolver.TryResolve(canonicalName, out var profile) && profile is not null
                ? profile.DataSourceName
                : UnknownDataSourceTag;
        }
        catch (Exception)
        {
            return UnknownDataSourceTag;
        }
    }
}
