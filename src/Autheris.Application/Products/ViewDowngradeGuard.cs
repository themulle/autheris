namespace Autheris.Application.Products;

using System;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Governance.Requests;
using Autheris.Application.Security;
using Autheris.Domain.Audit;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

/// <summary>
/// Enforces authorization, justification, and fail-closed auditing whenever a data product view column classification
/// is downgraded (for example <c>PiiMasked</c> to <c>PiiClearEligible</c> or <c>NonPii</c>). Only Data Governance or
/// members of the product's owner group may authorize a downgrade; IT administrators alone are refused.
/// </summary>
public sealed class ViewDowngradeGuard(
    IGovernanceCapabilities capabilities,
    IAuditWriter audit,
    TimeProvider? timeProvider = null)
{
    public const int MaxJustificationLength = ProductColumnClassifier.MaxReasonLength;

    private readonly IGovernanceCapabilities _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
    private readonly IAuditWriter _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public static bool IsDowngrade(ProductColumnClass from, ProductColumnClass to) => (int)to > (int)from;

    public async Task<DataProductColumn> GuardAndAuditAsync(
        SecurityPrincipalContext caller,
        DataProduct product,
        DataProductColumn column,
        ProductColumnClass targetClass,
        string? rawJustification,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(product);
        ArgumentNullException.ThrowIfNull(column);

        if (!IsDowngrade(column.Class, targetClass))
        {
            var reset = ProductColumnClassifier.Declassify(column, targetClass, rawJustification, caller.UserSid);
            return reset.Column ?? throw new ProductException(ProductFailure.Invalid, reset.ErrorCode ?? ProductErrorCodes.ReasonRequired);
        }

        if (!await CanDowngradeAsync(caller, product, ct).ConfigureAwait(false))
        {
            await _audit.RecordAsync(new AuditLogEntry
            {
                TenantId = product.Tenant,
                OccurredAt = _time.GetUtcNow().ToUniversalTime(),
                EventType = AuditEventTypes.ProductAccessDenied,
                ActorSid = caller.UserSid,
                TargetTable = $"products.{product.Schema}.{product.Name}",
                TargetColumn = column.Name,
                Decision = "DENY",
                TraceId = Activity.Current?.TraceId.ToString() ?? string.Empty,
                DetailsJson = JsonSerializer.Serialize(new { code = ProductErrorCodes.CreationNotAuthorized, operation = "view_downgrade" })
            }, ct).ConfigureAwait(false);
            throw new ProductException(ProductFailure.Forbidden, ProductErrorCodes.CreationNotAuthorized);
        }

        if (!TryNormalizeJustification(rawJustification, out var normalized))
        {
            throw new ProductException(ProductFailure.Invalid, ProductErrorCodes.ReasonRequired);
        }

        var result = ProductColumnClassifier.Declassify(column, targetClass, normalized, caller.UserSid);
        if (result.Column is null)
        {
            throw new ProductException(ProductFailure.Invalid, result.ErrorCode ?? ProductErrorCodes.ReasonRequired);
        }

        await _audit.RecordRequiredAsync(new AuditLogEntry
        {
            TenantId = product.Tenant,
            OccurredAt = _time.GetUtcNow().ToUniversalTime(),
            EventType = AuditEventTypes.ViewDowngraded,
            ActorSid = caller.UserSid,
            TargetTable = $"products.{product.Schema}.{product.Name}",
            TargetColumn = result.Column.Name,
            Decision = "ALLOW",
            TraceId = Activity.Current?.TraceId.ToString() ?? string.Empty,
            DetailsJson = JsonSerializer.Serialize(new
            {
                productId = product.Id,
                schema = product.Schema,
                name = product.Name,
                column = result.Column.Name,
                from = column.Class.ToString(),
                to = targetClass.ToString(),
                justification = normalized
            })
        }, ct).ConfigureAwait(false);

        return result.Column;
    }

    private async Task<bool> CanDowngradeAsync(SecurityPrincipalContext caller, DataProduct product, CancellationToken ct)
    {
        if (caller.TenantId != product.Tenant)
        {
            return false;
        }

        if (_capabilities.IsGovernance(caller.Principal, product.Tenant))
        {
            return true;
        }

        return await _capabilities.IsOwnerMemberAsync(
            caller,
            new ContainerRef(ContainerKind.DataProduct, product.Id),
            ct).ConfigureAwait(false);
    }

    public static bool TryNormalizeJustification(string? raw, out string? normalized)
    {
        if (!JustificationPolicy.TryValidate(raw, out var validated, out _) || validated.Length > MaxJustificationLength)
        {
            normalized = null;
            return false;
        }

        normalized = validated;
        return true;
    }
}
