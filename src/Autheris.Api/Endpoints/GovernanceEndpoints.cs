namespace Autheris.Api.Endpoints;

using System.Collections.Generic;
using System.Text.Json;
using Autheris.Api.Extensions;
using Autheris.Application.DataCatalog.Interfaces;
using Autheris.Application.Governance;
using Autheris.Domain.Audit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

public static partial class GovernanceEndpoints
{
    private static readonly JsonSerializerOptions GitOpsJsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static IEndpointRouteBuilder MapGovernanceEndpoints(this IEndpointRouteBuilder app)
    {
        // F-API-04: Declarative Web API OpenAPI/Swagger Schema & Doc Ingestion
        app.MapPost("/api/governance/catalog/ingest-openapi", (HttpContext context, IOpenApiIngestionUseCase ingestionUseCase) =>
            IngestOpenApiAsync(context, ingestionUseCase))
          .RequireAuthorization()
          .WithRequestBodyLimit(20 * 1024 * 1024) // SEC M-01: explicit large-body exception to the global Kestrel limit
          .WithAudit(AuditLevel.Full, AuditEventTypes.AuditConfigChanged);

        MapAuditAndComplianceEndpoints(app);
        MapRowFilterEvaluationEndpoints(app);

        // =========================================================================
        // Declarative Access Profiles & Subject Cleartext Exceptions (R-52 & R-50)
        // =========================================================================

        app.MapPost("/api/v1/consents/bulk", CreateBulkConsentAsync).RequireAuthorization().WithAudit(AuditLevel.Full, AuditEventTypes.ConsentApproved);
        app.MapGet("/api/v1/profiles", GetProfilesAsync).RequireAuthorization().WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);
        app.MapGet("/api/v1/profiles/{id}", GetProfileByIdAsync).RequireAuthorization().WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);
        app.MapPost("/api/v1/profiles/{id}/assignments/approve", ApproveProfileAssignmentAsync).RequireAuthorization().WithAudit(AuditLevel.Full, AuditEventTypes.ConsentApproved);
        app.MapDelete("/api/v1/profiles/{id}", DeleteProfileAsync).RequireAuthorization().WithAudit(AuditLevel.Full, AuditEventTypes.AuditConfigChanged);

        // Issue #40: GitOps / Declarative Consent & Row Filter Import API
        app.MapPost("/api/v1/governance/consents/apply", ApplyConsentManifestAsync)
            .RequireAuthorization().WithAudit(AuditLevel.Full, AuditEventTypes.ConsentApplied);

        app.MapPost("/api/governance/consents/apply", ApplyConsentManifestAsync)
            .RequireAuthorization().WithAudit(AuditLevel.Full, AuditEventTypes.ConsentApplied);

        return app;
    }
}

public sealed class ApproveProfileAssignmentRequest
{
    public string? Subject { get; set; }
    public string? SubjectType { get; set; }
}

public sealed class BulkConsentRequest
{
    public string? Subject { get; set; }
    public string? SubjectType { get; set; }
    public string? MaskingMode { get; set; }
    public List<string>? Tables { get; set; }
    public string? RowFilter { get; set; }
    public int ValidDays { get; set; }
    public string? Justification { get; set; }
}
