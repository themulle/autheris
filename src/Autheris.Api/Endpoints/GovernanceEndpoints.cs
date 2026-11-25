namespace Autheris.Api.Endpoints;

using System.Collections.Generic;
using System.Text.Json;
using Autheris.Api.Extensions;
using Autheris.Api.Security;
using Autheris.Application.Catalog.Interfaces;
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
        // F-Declarative Web API OpenAPI/Swagger Schema & Doc Ingestion
        app.MapPost("/api/governance/catalog/ingest-openapi", (HttpContext context, IOpenApiIngestionUseCase ingestionUseCase) =>
            IngestOpenApiAsync(context, ingestionUseCase))
          .RequireAuthorization(GatewayPolicies.OpenApiPublish)
          .WithRequestBodyLimit(20 * 1024 * 1024) // explicit large-body exception to the global Kestrel limit
          .WithAudit(AuditLevel.Full, AuditEventTypes.AuditConfigChanged);

        MapAuditAndComplianceEndpoints(app);

        // =========================================================================
        // Declarative Access Profiles & Subject Cleartext Exceptions (R-52 & R-50)
        // =========================================================================

        app.MapPost("/api/v1/consents/bulk", CreateBulkConsentAsync).RequireAuthorization(GatewayPolicies.ProfileAdmin).WithAudit(AuditLevel.Full, AuditEventTypes.ConsentApproved);
        app.MapGet("/api/v1/profiles", GetProfilesAsync).RequireAuthorization(GatewayPolicies.ProfileRead).WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);
        app.MapGet("/api/v1/profiles/{id}", GetProfileByIdAsync).RequireAuthorization(GatewayPolicies.ProfileRead).WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);
        app.MapPost("/api/v1/profiles/{id}/assignments/approve", ApproveProfileAssignmentAsync).RequireAuthorization(GatewayPolicies.GovernanceAdmin).WithAudit(AuditLevel.Full, AuditEventTypes.ConsentApproved);
        app.MapDelete("/api/v1/profiles/{id}", DeleteProfileAsync).RequireAuthorization(GatewayPolicies.ProfileAdmin).WithAudit(AuditLevel.Full, AuditEventTypes.AuditConfigChanged);

        // Issue #40: GitOps / Declarative Consent & Row Filter Import API. Amendment 6: Data Governance applies; IT administration submits a proposal.
        app.MapPost("/api/v1/governance/consents/apply", ApplyOrProposeConsentManifestAsync)
            .RequireAuthorization(GatewayPolicies.AdminOnly).WithAudit(AuditLevel.Full, AuditEventTypes.ConsentApplied);

        app.MapPost("/api/governance/consents/apply", ApplyOrProposeConsentManifestAsync)
            .RequireAuthorization(GatewayPolicies.AdminOnly).WithAudit(AuditLevel.Full, AuditEventTypes.ConsentApplied);

        // Amendment 6: manifest proposals are reviewed by IT administration or Data Governance and decided by one Data Governance member.
        app.MapGet("/api/v1/governance/consents/proposals/{id:guid}", GetManifestProposalAsync)
            .RequireAuthorization(GatewayPolicies.AdminOnly).WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);
        app.MapPost("/api/v1/governance/consents/proposals/{id:guid}/confirm", ConfirmManifestProposalAsync)
            .RequireAuthorization(GatewayPolicies.GovernanceAdmin).WithAudit(AuditLevel.Full, AuditEventTypes.ManifestProposalConfirmed);
        app.MapPost("/api/v1/governance/consents/proposals/{id:guid}/reject", RejectManifestProposalAsync)
            .RequireAuthorization(GatewayPolicies.GovernanceAdmin).WithAudit(AuditLevel.Full, AuditEventTypes.ManifestProposalRejected);

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
    public IReadOnlyList<Autheris.Domain.Model.GrantRowScopeEntry>? RowScope { get; set; }
    public int ValidDays { get; set; }
    public string? Justification { get; set; }

    /// <summary>O-5d: members the contract does not know. Only used to refuse the removed <c>assignedSubjects</c> member explicitly.</summary>
    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? ExtensionData { get; set; }
}
