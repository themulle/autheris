using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Analytics;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using HotChocolate;
using Microsoft.AspNetCore.Http;

namespace Autheris.GraphQL.Types;

public sealed partial class Query
{
    public async Task<GdprDisclosureReport> GetGdprDataDisclosureReportAsync(
        string? domain = null,
        string? schema = null,
        string? tableName = null,
        string? subjectSid = null,
        int timeWindowDays = 365,
        [Service] ILineageImpactAnalyzerService lineageService = null!,
        [Service] IHttpContextAccessor httpContextAccessor = null!,
        CancellationToken ct = default)
    {
        var callerContext = GetCallerSecurityContext(httpContextAccessor, "Authentication is required for GDPR access reports.");

        TableIdentifier? tableId = null;
        if (!string.IsNullOrWhiteSpace(domain) && !string.IsNullOrWhiteSpace(schema) && !string.IsNullOrWhiteSpace(tableName))
        {
            if (!TableIdentifier.TryCreate(domain, schema, tableName, out var parsedTableId))
            {
                throw InvalidTableArgument();
            }

            tableId = parsedTableId;
        }

        Sid? sid = !string.IsNullOrWhiteSpace(subjectSid) ? new Sid(subjectSid) : (Sid?)null;

        bool isClusterAdmin = callerContext.IsClusterAdmin;
        bool canAccessForeignReports = isClusterAdmin || callerContext.Roles.Any(r =>
            r.Equals("PrivacyAdmin", StringComparison.OrdinalIgnoreCase) ||
            r.Equals("DataProtectionOfficer", StringComparison.OrdinalIgnoreCase) ||
            r.Equals("GovernanceAdmin", StringComparison.OrdinalIgnoreCase));

        // Effective SID: If subjectSid is omitted, default to callerContext.UserSid unless caller is a privacy officer
        var effectiveSid = sid ?? (canAccessForeignReports ? (Sid?)null : callerContext.UserSid);

        if (effectiveSid.HasValue && !effectiveSid.Value.Equals(callerContext.UserSid) && !canAccessForeignReports)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("FORBIDDEN")
                .SetMessage("GDPR access reports for other identities require PrivacyAdmin or GovernanceAdmin rights.")
                .Build());
        }

        try
        {
            return await lineageService.GetGdprDataDisclosureReportAsync(tableId, effectiveSid, timeWindowDays, callerContext, ct);
        }
        catch (Autheris.Application.Lineage.GdprSubjectScopeUnresolvedException ex)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode(Autheris.Application.Lineage.GdprSubjectScopeUnresolvedException.ErrorCode)
                .SetMessage(ex.Message)
                .Build());
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("FORBIDDEN")
                .SetMessage(ex.Message)
                .Build());
        }
    }

    public async Task<string> ExportGdprDataDisclosureReportPdfBase64Async(
        string? domain = null,
        string? schema = null,
        string? tableName = null,
        string? subjectSid = null,
        int timeWindowDays = 365,
        [Service] ILineageImpactAnalyzerService lineageService = null!,
        [Service] IGdprAuditReportExporter pdfExporter = null!,
        [Service] IHttpContextAccessor httpContextAccessor = null!,
        CancellationToken ct = default)
    {
        var report = await GetGdprDataDisclosureReportAsync(domain, schema, tableName, subjectSid, timeWindowDays, lineageService, httpContextAccessor, ct);
        var exportResult = pdfExporter.ExportReportToPdf(report);
        return Convert.ToBase64String(exportResult.DocumentBytes);
    }
}
