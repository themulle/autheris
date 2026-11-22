namespace Autheris.Application.Runtime.Jobs.Services;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

public sealed partial class AsyncQueryJobManager
{
    public async Task<Stream?> GetJobResultStreamAsync(
        string jobId,
        TenantId tenantId,
        ClaimsPrincipal user,
        Autheris.Application.Policy.Pdp.IAccessDecisionPoint decisionPoint,
        ITableMetadataRepository metadataRepository,
        TrinoSqlEngine.ISqlEngine? sqlEngine = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(decisionPoint);
        ArgumentNullException.ThrowIfNull(metadataRepository);

        var userSid = AsyncJobOwner.Resolve(user) ?? string.Empty;
        var resolved = await ResolveAsync(jobId, tenantId, userSid, ct).ConfigureAwait(false);
        var job = resolved?.Descriptor;
        if (job == null || job.State != AsyncJobState.Completed)
        {
            return null;
        }

        await ReauthorizeJobFetchAsync(job, user, tenantId, userSid, decisionPoint, metadataRepository, sqlEngine ?? TrinoSqlEngine.FastSqlEngine.Default, ct)
            .ConfigureAwait(false);

        return await GetJobResultStreamAsync(jobId, tenantId, userSid, ct).ConfigureAwait(false);
    }

    private async Task ReauthorizeJobFetchAsync(
        AsyncJobDescriptor job,
        ClaimsPrincipal user,
        TenantId tenantId,
        string userSid,
        Autheris.Application.Policy.Pdp.IAccessDecisionPoint decisionPoint,
        ITableMetadataRepository metadataRepository,
        TrinoSqlEngine.ISqlEngine sqlEngine,
        CancellationToken ct)
    {
        IReadOnlyList<TableIdentifier> tables;
        try
        {
            tables = ResolveTablesForReauthorization(job, sqlEngine);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await DenyResultAsync(job, tenantId, userSid, "Async job referenced tables could not be validated at fetch.", ct).ConfigureAwait(false);
            throw new Autheris.Domain.Exceptions.GatewayForbiddenException("Access to async job result is denied.", "JOB_RESULT_FORBIDDEN");
        }

        var secCtx = Autheris.Domain.Security.SecurityPrincipalContext.FromPrincipal(user, tenantId);
        foreach (var tableId in tables)
        {
            TableMetadata? meta;
            try
            {
                meta = await metadataRepository.GetTableMetadataAsync(tableId, ct).ConfigureAwait(false);
            }
            catch (Autheris.Domain.Exceptions.TableMetadataUnavailableException)
            {
                meta = null;
            }

            if (meta is null || !meta.Table.IsActive)
            {
                await DenyResultAsync(job, tenantId, userSid, $"Async job result download denied: referenced table '{tableId}' is unavailable.", ct).ConfigureAwait(false);
                throw new Autheris.Domain.Exceptions.GatewayForbiddenException("Access to async job result is denied.", "JOB_RESULT_FORBIDDEN");
            }

            var decision = await decisionPoint.DecideAsync(
                new Autheris.Application.Policy.Pdp.AccessRequest(
                    secCtx,
                    meta,
                    Autheris.Application.Policy.Pdp.AccessOperation.Read,
                    Autheris.Application.Policy.Pdp.AccessTransport.WebSql),
                ct).ConfigureAwait(false);

            if (!decision.IsAllowed)
            {
                var denyReason = decision.Reasons.Count > 0
                    ? decision.Reasons[0].Code
                    : decision.DeniedReasons.FirstOrDefault() ?? "POLICY_DENIED";
                await DenyResultAsync(job, tenantId, userSid, $"Async job result download denied by governance policy ({denyReason}).", ct).ConfigureAwait(false);
                throw new Autheris.Domain.Exceptions.GatewayForbiddenException("Access to async job result is denied by governance policy.", "JOB_RESULT_FORBIDDEN");
            }
        }
    }

    private IReadOnlyList<TableIdentifier> ResolveTablesForReauthorization(AsyncJobDescriptor job, TrinoSqlEngine.ISqlEngine sqlEngine)
    {
        if (job.TargetTables is { Count: > 0 })
        {
            var list = new List<TableIdentifier>(job.TargetTables.Count);
            foreach (var raw in job.TargetTables)
            {
                list.Add(ParseTargetTable(raw));
            }

            return list;
        }

        if (string.IsNullOrWhiteSpace(job.Query))
        {
            return [];
        }

        var parsed = sqlEngine.Analyze(job.Query.AsMemory());
        if (parsed.ReferencedTables.Count == 0)
        {
            return [];
        }

        var result = new List<TableIdentifier>(parsed.ReferencedTables.Count);
        foreach (var t in parsed.ReferencedTables)
        {
            result.Add(ParseTargetTable(t.FullName));
        }

        return result;
    }

    private string[]? TryExtractQueryTables(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        try
        {
            var parsed = TrinoSqlEngine.FastSqlEngine.Default.Analyze(query.AsMemory());
            if (parsed.ReferencedTables.Count == 0)
            {
                return null;
            }

            return parsed.ReferencedTables
                .Select(t => ParseTargetTable(t.FullName).ToQualifiedName())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return null;
        }
    }

    private TableIdentifier ParseTargetTable(string rawName)
    {
        var defaultDs = _options?.Value?.WebSql?.DefaultDataSourceName;
        var defaultDomain = string.IsNullOrWhiteSpace(defaultDs) ? "default" : defaultDs;
        var parts = rawName.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length switch
        {
            3 => new TableIdentifier(parts[0], parts[1], parts[2]),
            2 => new TableIdentifier(defaultDomain, parts[0], parts[1]),
            1 => new TableIdentifier(defaultDomain, "dbo", parts[0]),
            _ => throw new InvalidOperationException($"Unsupported table identifier '{rawName}'.")
        };
    }
}
