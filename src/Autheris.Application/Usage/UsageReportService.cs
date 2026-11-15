namespace Autheris.Application.Usage;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Options.DataSources;
using Microsoft.Extensions.Options;

/// <summary>
/// Queries usage breakdown pages, applies deterministic historical tariff pricing in C#
/// with single-rounding at output, enforces date-range/row caps, and emits fail-closed audit records.
/// </summary>
public sealed class UsageReportService : IUsageReportService
{
    public const string InvalidRangeErrorCode = "USAGE_REPORT_INVALID_RANGE";
    public const string InvalidRequestErrorCode = "INVALID_QUERY";
    private const decimal MicrosPerUnit = 1_000_000m;
    private const int MaxPageLimit = 500;
    private const string MultiCountedMessage = "multi-counted: a user in N groups is counted in each group";

    private readonly IUsageRepository _usageRepository;
    private readonly IUsageGroupNameResolver _groupNameResolver;
    private readonly IAuditWriter _auditWriter;
    private readonly IOptions<UsageAccountingOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly byte[]? _hmacKey;

    public UsageReportService(
        IUsageRepository usageRepository,
        IUsageGroupNameResolver groupNameResolver,
        IAuditWriter auditWriter,
        IOptions<UsageAccountingOptions> options,
        TimeProvider? timeProvider = null,
        byte[]? hmacKey = null)
    {
        _usageRepository = usageRepository ?? throw new ArgumentNullException(nameof(usageRepository));
        _groupNameResolver = groupNameResolver ?? throw new ArgumentNullException(nameof(groupNameResolver));
        _auditWriter = auditWriter ?? throw new ArgumentNullException(nameof(auditWriter));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _hmacKey = hmacKey;
    }

    public (DateOnly FromUtc, DateOnly ToUtc) ResolveAndValidateDateRange(string? fromRaw, string? toRaw)
    {
        int maxDays = GetEffectiveMaxRangeDays();
        var todayUtc = DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime);

        DateOnly toUtc;
        if (string.IsNullOrWhiteSpace(toRaw))
        {
            toUtc = todayUtc;
        }
        else if (!DateOnly.TryParseExact(toRaw.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out toUtc))
        {
            throw new UsageReportValidationException(InvalidRangeErrorCode, "Parameter 'to' must be a valid YYYY-MM-DD UTC date.");
        }

        DateOnly fromUtc;
        if (string.IsNullOrWhiteSpace(fromRaw))
        {
            int defaultLookback = Math.Min(29, maxDays);
            fromUtc = toUtc.AddDays(-defaultLookback);
        }
        else if (!DateOnly.TryParseExact(fromRaw.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out fromUtc))
        {
            throw new UsageReportValidationException(InvalidRangeErrorCode, "Parameter 'from' must be a valid YYYY-MM-DD UTC date.");
        }

        ValidateDateSpan(fromUtc, toUtc, maxDays);
        return (fromUtc, toUtc);
    }

    public string ComputeUserKey(Sid userSid) =>
        UsageAttribution.ComputeUserKey(userSid, _options.Value.PseudonymizeUsers, _hmacKey);

    public async Task<UsageReportResponse> GetReportAsync(
        UsageReportFilter filter,
        UsageReportAuditContext? auditContext = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ValidateDateSpan(filter.FromUtc, filter.ToUtc, GetEffectiveMaxRangeDays());
        int effectiveLimit = ClampLimit(filter.Limit);

        var query = new UsageReportQuery(
            Tenant: filter.Tenant,
            FromUtc: filter.FromUtc,
            ToUtc: filter.ToUtc,
            GroupBy: filter.GroupBy,
            UserKey: filter.UserKey,
            GroupSid: filter.GroupSid,
            BillingGroup: filter.BillingGroup,
            DataSource: filter.DataSource,
            TableId: filter.TableId,
            Transport: filter.Transport,
            Application: filter.Application,
            ClientId: filter.ClientId,
            GrantId: filter.GrantId,
            RestrictedTableIds: filter.RestrictedTableIds,
            IncludeTariffBreakdown: true,
            Limit: effectiveLimit,
            Cursor: filter.Cursor);

        UsageReportPage page;
        try
        {
            page = await _usageRepository.QueryAsync(query, ct).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            throw new UsageReportValidationException(InvalidRequestErrorCode, ex.Message, ex);
        }

        var tariffs = await _usageRepository.GetTariffsAsync(filter.Tenant, filter.FromUtc, filter.ToUtc, ct).ConfigureAwait(false);
        var grouped = GroupByDimension(page.Rows, filter.GroupBy);
        var dtoRows = new List<UsageReportRowDto>(grouped.Count);

        foreach (var (key, bucketRows) in grouped)
        {
            dtoRows.Add(await RollUpRowsAsync(filter.Tenant, key, filter.GroupBy, bucketRows, tariffs, ct).ConfigureAwait(false));
        }

        UsageReportRowDto? tenantTotal = null;
        if (filter.IncludeTenantTotal)
        {
            tenantTotal = await ComputeTenantTotalAsync(filter, tariffs, ct).ConfigureAwait(false);
        }

        if (auditContext is not null)
        {
            await RecordRequiredAuditAsync(
                filter.Tenant,
                AuditEventTypes.UsageReportRead,
                auditContext,
                filter.FromUtc,
                filter.ToUtc,
                dtoRows.Count,
                ct).ConfigureAwait(false);
        }

        bool multiCounted = filter.GroupBy == UsageGroupBy.Group;
        bool truncated = page.NextCursor is not null;
        return new UsageReportResponse(
            ReportType: auditContext?.ReportType ?? filter.GroupBy.ToString(),
            TenantId: filter.Tenant.Value,
            From: filter.FromUtc,
            To: filter.ToUtc,
            GroupBy: filter.GroupBy,
            MultiCounted: multiCounted,
            MultiCountedNote: multiCounted ? MultiCountedMessage : null,
            TenantTotal: tenantTotal,
            Rows: dtoRows,
            NextCursor: page.NextCursor,
            Truncated: truncated);
    }

    public async Task<int> EraseUserAsync(
        TenantId tenant,
        string userSid,
        string actorIdentity,
        string? traceId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userSid) || !SidFormat.IsCanonical(userSid.Trim()))
        {
            throw new UsageReportValidationException(InvalidRequestErrorCode, "Parameter 'userSid' must be a canonical SID.");
        }

        string trimmedSid = userSid.Trim();
        string userKey = ComputeUserKey(new Sid(trimmedSid));
        var auditContext = new UsageReportAuditContext(actorIdentity, "erase-user", userKey, traceId);

        // Required audit BEFORE mutating USAGE_DAILY so audit outages fail closed without deleting rows.
        await RecordRequiredAuditAsync(
            tenant,
            AuditEventTypes.UsageDataErased,
            auditContext,
            null,
            null,
            0,
            ct).ConfigureAwait(false);

        return await _usageRepository.EraseUserAsync(tenant, userKey, ct).ConfigureAwait(false);
    }

    private async Task<UsageReportRowDto> RollUpRowsAsync(
        TenantId tenant,
        string dimensionKey,
        UsageGroupBy? groupBy,
        IReadOnlyList<UsageReportRow> rows,
        IReadOnlyList<UsageTariffVersion> tariffs,
        CancellationToken ct)
    {
        long requests = 0, queries = 0, rowsReturned = 0, rowsMeasured = 0, bytes = 0, durationMs = 0;
        long analysisCost = 0, sourceComputeMs = 0, sourceComputeMeasured = 0, sourceCostMicros = 0;
        long allowCount = 0, denyAccess = 0, denyRateLimit = 0, denyCost = 0, denyQuota = 0, denyOther = 0, errors = 0;

        var tariffInputs = new List<UsageTariffRowInput>(rows.Count);
        var distinctSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sourceCostUnits = new HashSet<string>(StringComparer.Ordinal);
        DateOnly? day = null;
        string? transport = null;

        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            requests += r.Requests;
            queries += r.Queries;
            rowsReturned += r.RowsReturned;
            rowsMeasured += r.RowsMeasured;
            bytes += r.Bytes;
            durationMs += r.DurationMs;
            analysisCost += r.AnalysisCost;
            sourceComputeMs += r.SourceComputeMs;
            sourceComputeMeasured += r.SourceComputeMeasured;
            sourceCostMicros += r.SourceCostMicros;
            allowCount += r.AllowCount;
            denyAccess += r.DenyAccess;
            denyRateLimit += r.DenyRateLimit;
            denyCost += r.DenyCost;
            denyQuota += r.DenyQuota;
            denyOther += r.DenyOther;
            errors += r.Errors;

            day ??= r.DayUtc;
            if (r.Transport.HasValue && transport is null)
            {
                transport = r.Transport.Value.ToString();
            }

            if (!string.IsNullOrWhiteSpace(r.DataSource))
            {
                distinctSources.Add(r.DataSource);
            }

            DateOnly effectiveDay = r.DayUtc ?? default;
            var tariffInput = UsageTariffRowInput.FromReportRow(tenant.Value, effectiveDay, r);
            tariffInputs.Add(tariffInput);

            if (tariffInput.HasGrant && !string.IsNullOrWhiteSpace(r.DataSource))
            {
                var matched = UsageTariffCalculator.ResolveTariff(tenant.Value, effectiveDay, r.DataSource, tariffs);
                if (matched is not null && !string.IsNullOrWhiteSpace(matched.SourceCostUnit))
                {
                    sourceCostUnits.Add(matched.SourceCostUnit);
                }
            }
        }

        var costSummary = UsageTariffCalculator.CalculateSummary(tariffInputs, tariffs);
        var costDtos = new List<UsageCostBucketDto>(costSummary.Buckets.Count);
        for (int i = 0; i < costSummary.Buckets.Count; i++)
        {
            var b = costSummary.Buckets[i];
            costDtos.Add(new UsageCostBucketDto(b.CostUnits, b.UnitLabel));
        }

        decimal? sourceCost = null;
        string? sourceCostUnit = null;
        if (distinctSources.Count == 1 && sourceCostUnits.Count == 1)
        {
            foreach (string unit in sourceCostUnits)
            {
                sourceCostUnit = unit;
                break;
            }

            sourceCost = UsageTariffCalculator.RoundCost(sourceCostMicros / MicrosPerUnit);
        }

        string? groupSid = groupBy == UsageGroupBy.Group ? dimensionKey : null;
        string? groupName = groupSid is not null
            ? await _groupNameResolver.ResolveGroupNameAsync(tenant, groupSid, ct).ConfigureAwait(false)
            : null;

        return new UsageReportRowDto(
            Key: dimensionKey,
            Day: groupBy == UsageGroupBy.Day ? day : null,
            UserKey: groupBy == UsageGroupBy.User ? dimensionKey : null,
            GroupSid: groupSid,
            GroupName: groupName,
            BillingGroup: groupBy == UsageGroupBy.BillingGroup ? dimensionKey : null,
            DataSource: groupBy == UsageGroupBy.DataSource ? dimensionKey : null,
            TableId: groupBy == UsageGroupBy.Table ? dimensionKey : null,
            Transport: groupBy == UsageGroupBy.Transport ? transport : null,
            Application: groupBy == UsageGroupBy.Application ? dimensionKey : null,
            ClientId: groupBy == UsageGroupBy.ClientId ? dimensionKey : null,
            GrantId: groupBy == UsageGroupBy.Grant ? dimensionKey : null,
            Requests: requests,
            Queries: queries,
            RowsReturned: rowsReturned,
            RowsMeasured: rowsMeasured,
            Bytes: bytes,
            DurationMs: durationMs,
            AnalysisCost: analysisCost,
            SourceComputeMs: sourceComputeMs,
            SourceComputeMeasured: sourceComputeMeasured,
            SourceCost: sourceCost,
            SourceCostUnit: sourceCostUnit,
            AllowCount: allowCount,
            DenyAccess: denyAccess,
            DenyRateLimit: denyRateLimit,
            DenyCost: denyCost,
            DenyQuota: denyQuota,
            DenyOther: denyOther,
            Errors: errors,
            Cost: costDtos,
            TariffMissing: costSummary.TariffMissing);
    }

    private async Task<UsageReportRowDto> ComputeTenantTotalAsync(
        UsageReportFilter filter,
        IReadOnlyList<UsageTariffVersion> tariffs,
        CancellationToken ct)
    {
        var allRows = new List<UsageReportRow>();
        string? cursor = null;

        do
        {
            var page = await _usageRepository.QueryAsync(new UsageReportQuery(
                Tenant: filter.Tenant,
                FromUtc: filter.FromUtc,
                ToUtc: filter.ToUtc,
                GroupBy: UsageGroupBy.DataSource,
                UserKey: filter.UserKey,
                BillingGroup: filter.BillingGroup,
                DataSource: filter.DataSource,
                TableId: filter.TableId,
                Transport: filter.Transport,
                Application: filter.Application,
                ClientId: filter.ClientId,
                RestrictedTableIds: filter.RestrictedTableIds,
                IncludeTariffBreakdown: true,
                Limit: MaxPageLimit,
                Cursor: cursor), ct).ConfigureAwait(false);

            allRows.AddRange(page.Rows);
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        return await RollUpRowsAsync(filter.Tenant, filter.Tenant.Value, null, allRows, tariffs, ct).ConfigureAwait(false);
    }

    private async Task RecordRequiredAuditAsync(
        TenantId tenant,
        string eventType,
        UsageReportAuditContext auditContext,
        DateOnly? fromUtc,
        DateOnly? toUtc,
        int rowCount,
        CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow();
        var detailsJson = JsonSerializer.Serialize(new
        {
            tenantId = tenant.Value,
            reportType = auditContext.ReportType,
            targetResource = auditContext.TargetResource,
            from = fromUtc?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            to = toUtc?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            rowCount,
            caller = auditContext.ActorIdentity
        });

        var entry = new AuditLogEntry
        {
            TenantId = tenant,
            OccurredAt = now,
            EventType = eventType,
            ActorSid = new Sid(auditContext.ActorIdentity),
            TargetTable = string.IsNullOrWhiteSpace(auditContext.TargetResource)
                ? $"USAGE/{auditContext.ReportType}"
                : $"USAGE/{auditContext.ReportType}/{auditContext.TargetResource}",
            Decision = "ALLOW",
            TraceId = string.IsNullOrWhiteSpace(auditContext.TraceId) ? Guid.NewGuid().ToString("N") : auditContext.TraceId,
            DetailsJson = detailsJson
        };

        try
        {
            await _auditWriter.RecordRequiredAsync(entry, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (AuditUnavailableException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new AuditUnavailableException("The audit store is temporarily unavailable (fail-closed).", ex);
        }
    }

    private int GetEffectiveMaxRangeDays() =>
        _options.Value.MaxReportRangeDays > 0 ? _options.Value.MaxReportRangeDays : 30;

    private int ClampLimit(int requestedLimit)
    {
        if (requestedLimit <= 0)
        {
            throw new UsageReportValidationException(InvalidRequestErrorCode, "Parameter 'limit' must be positive.");
        }

        int maxConfigured = _options.Value.MaxReportRows > 0 ? Math.Min(_options.Value.MaxReportRows, MaxPageLimit) : MaxPageLimit;
        return Math.Min(requestedLimit, maxConfigured);
    }

    private static void ValidateDateSpan(DateOnly fromUtc, DateOnly toUtc, int maxDays)
    {
        if (toUtc < fromUtc)
        {
            throw new UsageReportValidationException(InvalidRangeErrorCode, "Parameter 'to' must be on or after 'from'.");
        }

        if ((toUtc.DayNumber - fromUtc.DayNumber) > maxDays)
        {
            throw new UsageReportValidationException(InvalidRangeErrorCode, $"Requested date range exceeds maximum allowed {maxDays} days.");
        }
    }

    private static List<(string Key, List<UsageReportRow> Rows)> GroupByDimension(
        IReadOnlyList<UsageReportRow> rows,
        UsageGroupBy groupBy)
    {
        var result = new List<(string Key, List<UsageReportRow> Rows)>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);

        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            string key = ExtractDimensionKey(row, groupBy);
            if (!index.TryGetValue(key, out int slot))
            {
                slot = result.Count;
                index[key] = slot;
                result.Add((key, new List<UsageReportRow>()));
            }

            result[slot].Rows.Add(row);
        }

        return result;
    }

    private static string ExtractDimensionKey(UsageReportRow row, UsageGroupBy groupBy) => groupBy switch
    {
        UsageGroupBy.Day => row.DayUtc?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
        UsageGroupBy.User => row.UserKey ?? string.Empty,
        UsageGroupBy.Group => row.GroupSid ?? string.Empty,
        UsageGroupBy.BillingGroup => row.BillingGroup ?? string.Empty,
        UsageGroupBy.DataSource => row.DataSource ?? string.Empty,
        UsageGroupBy.Table => row.TableId ?? string.Empty,
        UsageGroupBy.Transport => row.Transport?.ToString() ?? string.Empty,
        UsageGroupBy.Application => row.Application ?? string.Empty,
        UsageGroupBy.ClientId => row.ClientId ?? string.Empty,
        UsageGroupBy.Grant => row.GrantId ?? string.Empty,
        _ => string.Empty
    };
}
