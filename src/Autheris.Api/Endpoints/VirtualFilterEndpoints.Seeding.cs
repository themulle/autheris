namespace Autheris.Api.Endpoints;

using Autheris.Api.Errors;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Autheris.Api.Extensions;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

public static partial class VirtualFilterEndpoints
{
    internal static async Task<IResult> PlanSyncAsync(HttpContext context, VirtualFilterAdministrationService service)
    {
        var security = EndpointSecurity.GetSecurityContext(context);
        if (!security.HasRole(GatewayRole.FilterSync) && !security.HasRole(GatewayRole.FilterAdmin))
        {
            return Forbidden();
        }

        return await MapErrorsAsync(async () =>
        {
            var request = await ReadSyncRequestAsync(context, security).ConfigureAwait(false);
            return request == null ? Forbidden() : Results.Ok(await service.PlanSyncAsync(request, context.RequestAborted).ConfigureAwait(false));
        }, context).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies the file repository state. FilterSync applies; a sync that removes many bindings (see
    /// <c>VirtualFilters:MaxRemovals</c>) only goes through with <c>?force=true</c>, which needs FilterAdmin.
    /// </summary>
    internal static async Task<IResult> ApplySyncAsync(HttpContext context, VirtualFilterAdministrationService service)
    {
        var security = EndpointSecurity.GetSecurityContext(context);
        bool force = string.Equals(context.Request.Query["force"], "true", StringComparison.OrdinalIgnoreCase);
        // SR15-07: Sync requires FilterSync role; force=true requires BOTH FilterSync AND FilterAdmin roles
        if (!security.HasRole(GatewayRole.FilterSync) || (force && !security.HasRole(GatewayRole.FilterAdmin)))
        {
            return Forbidden();
        }

        return await MapErrorsAsync(async () =>
        {
            var request = await ReadSyncRequestAsync(context, security).ConfigureAwait(false);
            if (request == null)
            {
                return Forbidden();
            }

            var actor = new VirtualFilterActor(security.UserSid, IsSync: true, context.User?.GetUserIdentifiers());
            return Results.Ok(await service.ApplySyncAsync(request, actor, force, context.RequestAborted).ConfigureAwait(false));
        }).ConfigureAwait(false);
    }

    internal static async Task<IResult> ConfigSyncStatusAsync(
        HttpContext context,
        VirtualFilterAdministrationService service,
        IOptions<GatewayOptions>? options = null)
    {
        var security = EndpointSecurity.GetSecurityContext(context);
        if (!CanRead(security))
        {
            return Forbidden();
        }

        var snapshot = await service.GetSnapshotAsync(context.RequestAborted).ConfigureAwait(false);
        var opt = options?.Value?.VirtualFilters ?? new VirtualFilterOptions();

        var filters = snapshot.Filters.Where(f => security.IsClusterAdmin || f.TenantId == security.TenantId).ToList();
        var profiles = snapshot.Profiles.Where(p => security.IsClusterAdmin || p.TenantId == security.TenantId).ToList();

        var managedFilters = filters.Where(f => f.ManagedBy != null).ToList();
        var managedProfiles = profiles.Where(p => p.ManagedBy != null).ToList();

        var latestCommit = managedFilters.Select(f => f.ManagedBy!.Commit)
            .Concat(managedProfiles.Select(p => p.ManagedBy!.Commit))
            .LastOrDefault() ?? "none";

        var latestSyncAt = managedFilters.Select(f => (DateTimeOffset?)f.UpdatedAt)
            .Concat(managedProfiles.Select(p => (DateTimeOffset?)p.UpdatedAt))
            .DefaultIfEmpty(null)
            .Max();

        string syncStatus;
        if (filters.Any(f => f.Status == FilterApprovalStatus.PendingApproval) ||
            profiles.Any(p => p.Status == FilterApprovalStatus.PendingApproval))
        {
            syncStatus = "PendingApproval";
        }
        else if (managedFilters.Any(f => f.Status != FilterApprovalStatus.Active) ||
                 managedProfiles.Any(p => p.Status != FilterApprovalStatus.Active))
        {
            syncStatus = "DriftDetected";
        }
        else
        {
            syncStatus = "InSync";
        }

        return Results.Ok(new
        {
            generation = snapshot.Generation,
            status = syncStatus,
            lastSyncAt = latestSyncAt,
            lastCommit = latestCommit,
            lastWebhookTriggerAt = service.LastWebhookTriggerAt,
            lastWebhookDeliveryId = service.LastWebhookDeliveryId,
            filterCount = filters.Count,
            profileCount = profiles.Count,
            managedFilterCount = managedFilters.Count,
            managedProfileCount = managedProfiles.Count,
            maxRemovals = opt.MaxRemovals,
            requireApproval = opt.RequireApproval
        });
    }

    public static async Task<IResult> HandleConfigSyncWebhookAsync(
        HttpContext context,
        VirtualFilterAdministrationService service,
        IOptions<GatewayOptions>? options = null)
    {
        var (payload, tooLarge) = await EndpointSecurity.TryReadBodyAsync(
            context.Request,
            2 * 1024 * 1024,
            "Payload size exceeds maximum allowed size (2 MB).",
            context.RequestAborted).ConfigureAwait(false);

        if (payload == null)
        {
            return tooLarge!;
        }

        var deliveryId = context.Request.Headers["X-GitHub-Delivery"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(deliveryId))
        {
            return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "Header X-GitHub-Delivery is required.");
        }

        var opt = options?.Value?.VirtualFilters ?? new VirtualFilterOptions();
        var secret = !string.IsNullOrWhiteSpace(opt.WebhookSecret)
            ? opt.WebhookSecret
            : Environment.GetEnvironmentVariable("AUTHERIS_CONFIG_SYNC_WEBHOOK_SECRET");

        var signatureHeader = context.Request.Headers["X-Hub-Signature-256"].FirstOrDefault() ??
                              context.Request.Headers["X-Signature-256"].FirstOrDefault();

        // Timing-safe HMAC verification. Notice: Insecure.danger_bypass_webhook_signature does NOT apply here.
        if (string.IsNullOrWhiteSpace(secret) || !ValidateHubSignature(payload, signatureHeader, secret))
        {
            return Results.Unauthorized();
        }

        // Validate Git Ref if configured
        if (!string.IsNullOrWhiteSpace(opt.GitRef))
        {
            try
            {
                using var doc = JsonDocument.Parse(payload);
                if (doc.RootElement.TryGetProperty("ref", out var refProp))
                {
                    var pushRef = refProp.GetString();
                    if (!string.Equals(pushRef, opt.GitRef, StringComparison.OrdinalIgnoreCase))
                    {
                        return Results.Ok(new { status = "Ignored", reason = $"Push ref '{pushRef}' does not match configured ref '{opt.GitRef}'." });
                    }
                }
            }
            catch (JsonException)
            {
                // Invalid JSON body -> ignore ref matching
            }
        }

        var recorded = service.RecordWebhookTrigger(deliveryId, DateTimeOffset.UtcNow);

        return Results.Json(new
        {
            status = "Accepted",
            delivery = deliveryId,
            message = recorded ? "Sync triggered" : "Delivery already processed"
        }, statusCode: StatusCodes.Status202Accepted);
    }

    private static bool ValidateHubSignature(string payload, string? signatureHeader, string secret)
    {
        if (string.IsNullOrWhiteSpace(signatureHeader) || string.IsNullOrWhiteSpace(secret))
        {
            return false;
        }

        var clean = signatureHeader.Trim();
        if (clean.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase))
        {
            clean = clean["sha256=".Length..];
        }

        byte[] expectedBytes;
        try
        {
            expectedBytes = Convert.FromHexString(clean);
        }
        catch
        {
            return false;
        }

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var computed = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));

        return CryptographicOperations.FixedTimeEquals(computed, expectedBytes);
    }

    private static async Task<VirtualFilterSyncRequest?> ReadSyncRequestAsync(HttpContext context, SecurityPrincipalContext security)
    {
        var body = await ReadAsync<SyncBody>(context).ConfigureAwait(false);
        var tenant = ResolveTenant(security, body.Tenant);
        if (tenant == null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(body.Path) || string.IsNullOrWhiteSpace(body.Commit))
        {
            throw new ArgumentException("A sync needs the path and commit of the file repository.");
        }

        var profiles = (body.Profiles ?? []).Select(p => p.Value.ToModel(p.Key, tenant.Value)).ToList();
        return new VirtualFilterSyncRequest(
            tenant.Value,
            new ManagedBy(body.Path, body.Commit),
            (body.Filters ?? []).Select(p => p.Value.ToModel(p.Key, tenant.Value)).ToList(),
            profiles)
        {
            ExplicitKeyBudgets = ExplicitKeyBudgets((body.Profiles ?? []).Select(p => p.Value), profiles)
        };
    }

    private static HashSet<FilterBinding> ExplicitKeyBudgets(IEnumerable<ProfileBody> bodies, IEnumerable<VirtualFilterAccessProfile> models)
    {
        var set = new HashSet<FilterBinding>(ReferenceEqualityComparer.Instance);
        foreach (var (body, model) in bodies.Zip(models))
        {
            set.UnionWith(body.ExplicitKeyBudgets(model));
        }

        return set;
    }

    internal sealed class SyncBody
    {
        [JsonPropertyName("tenant")] public string? Tenant { get; set; }
        [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;
        [JsonPropertyName("commit")] public string Commit { get; set; } = string.Empty;
        [JsonPropertyName("filters")] public Dictionary<string, FilterBody>? Filters { get; set; }
        [JsonPropertyName("profiles")] public Dictionary<string, ProfileBody>? Profiles { get; set; }
    }
}
