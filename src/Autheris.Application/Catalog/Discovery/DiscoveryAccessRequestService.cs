namespace Autheris.Application.Catalog.Discovery;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.ConsentRequests;
using Autheris.Application.ConsentRequests.ClassificationClear;
using Autheris.Application.Governance.Grants;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.Extensions.Options;

/// <summary>Input of a discovery access request (GraphQL and MCP adapters).</summary>
/// <param name="ClearColumns">Column names the requester wants Clear access for; mapped to per-classification Clear requests.</param>
public sealed record DiscoveryAccessRequest(
    string Domain,
    string Schema,
    string TableName,
    string Justification,
    int? DurationDays,
    string? RequestedDuration,
    IReadOnlyList<string>? ClearColumns,
    string? IdempotencyKey,
    AccessRequestContextInput? Context = null);

/// <summary>Requests table access from a discovery channel; every channel ends in <see cref="IConsentRequestService.SubmitAsync"/>.</summary>
public interface IDiscoveryAccessRequestService
{
    Task<SubmitConsentRequestResult> RequestAsync(DiscoveryAccessRequest request, CatalogCaller caller, CancellationToken ct = default);
}

/// <summary>
/// Gate (authentication, optional role/scope; the hourly rate limit lives in the consent request service), discoverability check, clear-column mapping, idempotency of identical
/// pending requests and audit in front of <see cref="IConsentRequestService"/>. A table that is hidden, foreign or unknown answers
/// <c>NOT_FOUND</c>; an unknown, hidden, denied, already clear or non-clearable column answers <c>INVALID_COLUMNS</c>.
/// </summary>
public sealed class DiscoveryAccessRequestService : IDiscoveryAccessRequestService
{
    public const string InvalidColumnsCode = "INVALID_COLUMNS";
    public const string NotFoundCode = "NOT_FOUND";
    public const int MaxClearColumns = 50;
    public const int MaxColumnNameLength = 256;

    private readonly IDiscoveryService _discovery;
    private readonly IConsentRequestService _consents;
    private readonly IConsentApprovalRepository? _approvals;
    private readonly IOptions<GatewayOptions> _gatewayOptions;
    private readonly IOptions<CatalogDiscoveryOptions>? _discoveryOptions;
    private readonly IAuditWriter? _audit;
    private readonly IGrantValidityPolicy _validity;

    public DiscoveryAccessRequestService(
        IDiscoveryService discovery,
        IConsentRequestService consents,
        IOptions<GatewayOptions> gatewayOptions,
        IConsentApprovalRepository? approvals = null,
        IOptions<CatalogDiscoveryOptions>? discoveryOptions = null,
        IAuditWriter? audit = null,
        IGrantValidityPolicy? validity = null)
    {
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        _consents = consents ?? throw new ArgumentNullException(nameof(consents));
        _gatewayOptions = gatewayOptions ?? throw new ArgumentNullException(nameof(gatewayOptions));
        _approvals = approvals;
        _discoveryOptions = discoveryOptions;
        _audit = audit;
        _validity = validity ?? GrantValidityPolicy.CreateDefault();
    }

    public async Task<SubmitConsentRequestResult> RequestAsync(DiscoveryAccessRequest request, CatalogCaller caller, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(caller);

        var sid = await DiscoveryCallerGate.EnsureAccessRequestAllowedAsync(
            caller, _discoveryOptions?.Value ?? new CatalogDiscoveryOptions(), _gatewayOptions.Value, rateLimiter: null, ct).ConfigureAwait(false);

        TableIdentifier? audited = null;
        try
        {
            return await RequestCoreAsync(request, caller, sid, t => audited = t, ct).ConfigureAwait(false);
        }
        catch (ConsentRequestException ex) when (IsAuditedDenial(ex.Code))
        {
            await AuditDenialAsync(caller, sid, audited, ex.Code, ct).ConfigureAwait(false);
            throw;
        }
    }

    private static bool IsAuditedDenial(string code) =>
        code is NotFoundCode or InvalidColumnsCode or "RATE_LIMIT_EXCEEDED";

    private async Task<SubmitConsentRequestResult> RequestCoreAsync(
        DiscoveryAccessRequest request, CatalogCaller caller, Sid sid, Action<TableIdentifier> onTable, CancellationToken ct)
    {
        if (!TableIdentifier.TryCreate(request.Domain, request.Schema, request.TableName, out var tableId))
        {
            throw new ConsentRequestException("VALIDATION_ERROR", "Domain, Schema and TableName must be valid, non-empty values.");
        }

        onTable(tableId);
        var discovered = await _discovery.GetAsync(tableId, caller, ct).ConfigureAwait(false);
        if (discovered is null || discovered.IsHidden || discovered.Kind == DiscoveryObjectKind.Procedure)
        {
            throw new ConsentRequestException(NotFoundCode, "The table was not found.");
        }

        var requestedClear = MapClearColumns(discovered, request.ClearColumns);

        var existing = await FindPendingDuplicateAsync(sid, caller.Tenant, tableId, requestedClear, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            return new SubmitConsentRequestResult
            {
                RequestId = existing.Id,
                Status = existing.Status,
                Message = "An identical access request is already pending."
            };
        }

        var durationDays = request.DurationDays ?? Math.Max(1, (int)Math.Ceiling(_validity.DefaultDuration.TotalDays));
        var result = await _consents.SubmitAsync(
            new ConsentRequestSubmission(
                tableId.Domain, tableId.Schema, tableId.TableName, request.Justification, durationDays, request.IdempotencyKey,
                sid, caller.Principal, caller.Tenant, request.Context,
                RequestedDuration: request.RequestedDuration,
                RequestedClear: requestedClear.Count > 0 ? requestedClear : null),
            ct).ConfigureAwait(false);

        await AuditAsync(caller, sid, tableId, requestedClear, result, ct).ConfigureAwait(false);
        return result;
    }

    /// <summary>Groups the requested columns by the Clear-requestable classifications of each column.</summary>
    internal static IReadOnlyList<RequestedClearItem> MapClearColumns(DiscoveryObject discovered, IReadOnlyList<string>? clearColumns)
    {
        if (clearColumns is null || clearColumns.Count == 0)
        {
            return [];
        }

        if (clearColumns.Count > MaxClearColumns)
        {
            throw InvalidColumns();
        }

        var byCode = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in clearColumns)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > MaxColumnNameLength)
            {
                throw InvalidColumns();
            }

            var column = discovered.Columns.FirstOrDefault(c => string.Equals(c.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (column is null ||
                column.State != DiscoveryColumnState.Masked ||
                column.ClearRequestable.Count == 0)
            {
                throw InvalidColumns();
            }

            if (!seen.Add(column.Name))
            {
                continue;
            }

            foreach (var code in column.ClearRequestable)
            {
                if (!byCode.TryGetValue(code, out var list))
                {
                    byCode[code] = list = [];
                }

                list.Add(column.Name);
            }
        }

        return byCode.Select(static kv => new RequestedClearItem(kv.Key, kv.Value)).ToList();
    }

    private static ConsentRequestException InvalidColumns() =>
        new(InvalidColumnsCode, "One or more clear columns are invalid.");

    private async Task<ConsentRequest?> FindPendingDuplicateAsync(
        Sid sid, TenantId tenant, TableIdentifier table, IReadOnlyList<RequestedClearItem> wanted, CancellationToken ct)
    {
        if (_approvals is null)
        {
            return null;
        }

        var wantedKey = ClearKey(wanted);
        var requests = await _approvals.GetRequestsByRequesterSidAsync(sid, tenant, status: null, ct).ConfigureAwait(false);
        foreach (var r in requests)
        {
            if (r.TenantId == tenant &&
                r.TableIdentifier.Equals(table) &&
                IsPending(r.Status) &&
                r.RequestedWriteRules.Count == 0 &&
                string.Equals(ClearKey(RequestedClearCodec.Parse(r.ContextJson)), wantedKey, StringComparison.Ordinal))
            {
                return r;
            }
        }

        return null;
    }

    private static bool IsPending(string status) =>
        status.StartsWith("PENDING", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "ESCALATED", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "SUBMITTED", StringComparison.OrdinalIgnoreCase);

    private static string ClearKey(IReadOnlyList<RequestedClearItem> items) =>
        string.Join(
            ';',
            items.SelectMany(i => i.Columns.Select(c => i.Classification + "|" + c.ToLowerInvariant()))
                .OrderBy(x => x, StringComparer.Ordinal));

    private async Task AuditDenialAsync(CatalogCaller caller, Sid sid, TableIdentifier? table, string reasonCode, CancellationToken ct)
    {
        if (_audit is null)
        {
            return;
        }

        // Reason code only: neither column names nor the justification are recorded for a denial.
        await _audit.RecordBestEffortAsync(new AuditLogEntry
        {
            TenantId = caller.Tenant,
            EventType = AuditEventTypes.ConsentRequested,
            ActorSid = sid,
            TargetTable = table?.ToString() ?? string.Empty,
            Decision = "DENY",
            DetailsJson = JsonSerializer.Serialize(new
            {
                reason = reasonCode,
                surface = caller.Channel.ToString()
            })
        }, ct).ConfigureAwait(false);
    }

    private async Task AuditAsync(
        CatalogCaller caller, Sid sid, TableIdentifier table, IReadOnlyList<RequestedClearItem> clear, SubmitConsentRequestResult result, CancellationToken ct)
    {
        if (_audit is null)
        {
            return;
        }

        await _audit.RecordRequiredAsync(new AuditLogEntry
        {
            TenantId = caller.Tenant,
            EventType = AuditEventTypes.ConsentRequested,
            ActorSid = sid,
            TargetTable = table.ToString(),
            Decision = "ALLOW",
            DetailsJson = JsonSerializer.Serialize(new
            {
                requestId = result.RequestId,
                surface = caller.Channel.ToString(),
                clearColumns = clear.SelectMany(static i => i.Columns).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                classifications = clear.Select(static i => i.Classification).ToArray()
            })
        }, ct).ConfigureAwait(false);
    }
}
