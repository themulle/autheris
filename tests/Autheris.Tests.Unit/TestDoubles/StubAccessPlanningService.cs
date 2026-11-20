using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Interfaces;
using Autheris.Domain.Model;

namespace Autheris.Tests.Unit.TestDoubles;

internal sealed class StubAccessPlanningService : IAccessPlanningService
{
    public Func<string, string, string, Task<AdminConfirmPlanResult>>? OnConfirmPlan { get; init; }
    public Func<AdminApplyAccessRequest, string, TenantId, ClaimsPrincipal?, Task<AdminApplyAccessResult>>? OnApplyAccess { get; init; }
    public Func<AdminSetDatasetStateRequest, string, string?, TenantId?, Task<AdminSetDatasetStateResult>>? OnSetDatasetState { get; init; }

    public Task<AdminPlanAccessResult> PlanAccessAsync(
        AdminPlanAccessRequest request,
        string adminSid,
        string? tenantId = null,
        ClaimsPrincipal? caller = null,
        CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<AdminConfirmPlanResult> ConfirmPlanAsync(
        string planId,
        string totpCode,
        string adminSid,
        CancellationToken ct = default)
        => OnConfirmPlan is not null ? OnConfirmPlan(planId, totpCode, adminSid) : throw new NotSupportedException();

    public Task<AdminApplyAccessResult> ApplyAccessAsync(
        AdminApplyAccessRequest request,
        string adminSid,
        TenantId tenantId,
        ClaimsPrincipal? caller = null,
        CancellationToken ct = default)
        => OnApplyAccess is not null ? OnApplyAccess(request, adminSid, tenantId, caller) : throw new NotSupportedException();

    public Task<AdminPlanRowFilterEvaluationResult> PlanRowFilterEvaluationAsync(
        AdminPlanRowFilterEvaluationRequest request,
        string adminSid,
        string? tenantId = null,
        CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<AdminApplyRowFilterEvaluationResult> ApplyRowFilterEvaluationAsync(
        AdminApplyAccessRequest request,
        string adminSid,
        TenantId tenantId,
        CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<AdminPlanAccessResult> PlanAdminOperationAsync(
        string operation,
        string target,
        string argumentsDigest,
        string adminSid,
        string? tenantId = null,
        CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task ConsumeAdminOperationAsync(
        AdminApplyAccessRequest request,
        string operation,
        string argumentsDigest,
        string adminSid,
        string? tenantId = null,
        CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<AdminRegisterDatasourceResult> RegisterDatasourceAsync(
        AdminRegisterDatasourceRequest request,
        string adminSid,
        string? tenantId = null,
        bool isClusterAdmin = false,
        CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<AdminSetDatasetStateResult> SetDatasetStateAsync(
        AdminSetDatasetStateRequest request,
        string adminSid,
        string? tenantId = null,
        TenantId? restrictToTenant = null,
        CancellationToken ct = default)
        => OnSetDatasetState is not null ? OnSetDatasetState(request, adminSid, tenantId, restrictToTenant) : throw new NotSupportedException();

    public Task<AdminResolvePrincipalResult> ResolvePrincipalAsync(
        AdminResolvePrincipalRequest request,
        string adminSid,
        CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<AdminPlanAccessResult?> GetPlanAsync(string planId, CancellationToken ct = default) => Task.FromResult<AdminPlanAccessResult?>(null);
}
