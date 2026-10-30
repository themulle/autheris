using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Autheris.Api.Configuration.Validation;

/// <summary>
/// WP-ARC-C-02: Validates core gateway options (row filter strategy, Casbin files, plugin manifest, and OpenMetadata HTTPS).
/// </summary>
public sealed class GatewayCoreOptionsValidator : IValidateOptions<GatewayOptions>
{
    private readonly IHostEnvironment _environment;
    private readonly IFileSystemProbe _fileSystem;

    public GatewayCoreOptionsValidator(IHostEnvironment environment, IFileSystemProbe? fileSystem = null)
    {
        ArgumentNullException.ThrowIfNull(environment);
        _environment = environment;
        _fileSystem = fileSystem ?? PhysicalFileSystemProbe.Instance;
    }

    /// <summary>
    /// DT-A-1 / DT-SC-13: <c>Gateway:Tenancy:DefaultTenantId</c>, when present, must be a valid, non-reserved tenant id that does not
    /// collide case-insensitively with another configured tenant id. Messages never echo the value.
    /// </summary>
    private static void ValidateDefaultTenant(GatewayOptions opts, List<string> failures)
    {
        var raw = opts.Tenancy.DefaultTenantId;
        if (raw is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            failures.Add("Gateway:Tenancy:DefaultTenantId must not be empty; remove the key to configure no default tenant.");
            return;
        }

        if (Autheris.Domain.Common.TenantId.IsReserved(raw))
        {
            failures.Add("Gateway:Tenancy:DefaultTenantId must not use the reserved '_' prefix.");
            return;
        }

        if (!Autheris.Domain.Common.TenantId.TryParse(raw, out _))
        {
            failures.Add("Gateway:Tenancy:DefaultTenantId is not a valid tenant id (alphanumeric, '_' and '-', at most 64 characters).");
            return;
        }

        var others = Autheris.Application.Security.TenantCollisionGuard.ConfiguredTenantIds(opts);
        var collides = Autheris.Application.Security.TenantCollisionCheck.FindCollisions(others.Append(raw))
            .Any(c => c.Spellings.Contains(raw, StringComparer.Ordinal));
        if (collides)
        {
            failures.Add("Gateway:Tenancy:DefaultTenantId collides case-insensitively with another configured tenant id.");
        }
    }

    public ValidateOptionsResult Validate(string? name, GatewayOptions opts)
    {
        ArgumentNullException.ThrowIfNull(opts);

        var failures = new List<string>();

        if (!Enum.IsDefined(opts.RowFilters.SubqueryStrategy))
        {
            failures.Add("Gateway:RowFilters:SubqueryStrategy must be Exists, InCorrelated or In.");
        }

        // REQ-SQL-130 (BC-130-9): a time zone function without the closed zone set must not be admitted by configuration.
        if (opts.WebSql?.AdditionalAllowedFunctions is { Count: > 0 } additional &&
            additional.Any(TrinoSqlEngine.SqlFunctionAllowlists.IsTimeZoneFunctionName))
        {
            failures.Add("Gateway:WebSql:AdditionalAllowedFunctions must not list a time zone function; convert_timezone is the governed one.");
        }

        // Issue #277 (FR-11, SEC-277-05/-07): the recursion bound is validated at start (never taken from SQL text), and with the
        // opt-in on the timeout is the work bound of a recursive statement, so it must be finite and sane.
        if (opts.WebSql is { } webSql)
        {
            if (webSql.MaxRecursionDepth is < 1 or > 10_000)
            {
                failures.Add("Gateway:WebSql:MaxRecursionDepth must be between 1 and 10000.");
            }

            if (webSql.AllowRecursiveCte && webSql.ExecutionTimeoutSeconds is < 1 or > 300)
            {
                failures.Add("Gateway:WebSql:AllowRecursiveCte requires Gateway:WebSql:ExecutionTimeoutSeconds between 1 and 300.");
            }
        }

        ValidateDefaultTenant(opts, failures);

        if (opts.Catalog.MaxTagsPerDataset is < 1 or > Autheris.Domain.Model.DatasetTagRules.HardMaxTagsPerDataset)
        {
            failures.Add("Gateway:Catalog:MaxTagsPerDataset must be between 1 and 256.");
        }

        if (opts.Catalog.DefaultVisibility is not (Autheris.Domain.Model.DatasetVisibility.Hidden or Autheris.Domain.Model.DatasetVisibility.Summary))
        {
            failures.Add("Gateway:Catalog:DefaultVisibility must be Hidden or Summary (Full is not allowed as a default).");
        }

        if (opts.Casbin.Enabled &&
            !string.IsNullOrWhiteSpace(opts.Casbin.ModelPath) &&
            !_fileSystem.FileExists(opts.Casbin.ModelPath))
        {
            failures.Add("Gateway:Casbin is enabled, but the configured ModelPath file was not found.");
        }

        if (opts.Casbin.Enabled && string.IsNullOrWhiteSpace(opts.Casbin.PolicyPath))
        {
            failures.Add("Gateway:Casbin is enabled, but PolicyPath is not configured. Failing closed.");
        }

        if (opts.Casbin.Enabled &&
            !string.IsNullOrWhiteSpace(opts.Casbin.PolicyPath) &&
            !_fileSystem.FileExists(opts.Casbin.PolicyPath))
        {
            failures.Add("Gateway:Casbin is enabled, but the configured PolicyPath file was not found.");
        }

        failures.AddRange(TeamToGroupSidMapRules.Validate(opts.OpenMetadata.TeamToGroupSidMap, "Gateway:OpenMetadata:TeamToGroupSidMap"));

        if (!_environment.IsDevelopment() &&
            opts.OpenMetadata.Enabled &&
            !(Uri.TryCreate(opts.OpenMetadata.ServerUrl, UriKind.Absolute, out var uri) &&
              string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase)) &&
            !opts.IsInsecureTransportAllowed)
        {
            failures.Add("Security violation: Outside Development, OpenMetadata.ServerUrl must use HTTPS.");
        }

        if (!_environment.IsDevelopment() &&
            !string.IsNullOrWhiteSpace(opts.Plugins.Directory) &&
            _fileSystem.DirectoryExists(opts.Plugins.Directory) &&
            !opts.Plugins.RequireIntegrityManifest)
        {
            failures.Add("Security violation: Outside Development, a configured plugin directory requires Plugins.RequireIntegrityManifest = true.");
        }

        ValidateCallerAttributes(opts, failures);

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }

    /// <summary>
    /// REQ-GOV-129 (PLAN-GOV-129 section 7, BC-129-04): ranges of the TTL family and the trusted claim issuers. Every trusted issuer must be an
    /// issuer the bearer scheme validates, so a claim of another issuer can never be trusted by configuration.
    /// </summary>
    private static void ValidateCallerAttributes(GatewayOptions opts, List<string> failures)
    {
        var ca = opts.CallerAttributes;
        if (!ca.Enabled)
        {
            return;
        }

        if (ca.DefaultTtlSeconds > ca.MaxTtlSeconds)
        {
            failures.Add("Gateway:CallerAttributes:DefaultTtlSeconds must not exceed MaxTtlSeconds.");
        }

        if (ca.MaxTtlSeconds > CallerAttributeOptions.TtlCeilingSeconds)
        {
            failures.Add("Gateway:CallerAttributes:MaxTtlSeconds must not exceed 300.");
        }

        if (ca.NegativeTtlSeconds > ca.DefaultTtlSeconds)
        {
            failures.Add("Gateway:CallerAttributes:NegativeTtlSeconds must not exceed DefaultTtlSeconds.");
        }

        if (ca.Claims.TrustedIssuers.Count > 0)
        {
            var valid = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var entra = opts.Authentication.EntraId;
            if (!string.IsNullOrWhiteSpace(entra.TenantId))
            {
                var instance = string.IsNullOrWhiteSpace(entra.Instance) ? "https://login.microsoftonline.com/" : entra.Instance.TrimEnd('/') + "/";
                valid.Add($"{instance}{entra.TenantId}/v2.0");
                valid.Add($"https://sts.windows.net/{entra.TenantId}/");
            }

            var adfs = opts.Authentication.Adfs;
            if (!string.IsNullOrWhiteSpace(adfs.Authority))
            {
                valid.Add(adfs.Authority.TrimEnd('/'));
                valid.Add($"{adfs.Authority.TrimEnd('/')}/services/trust");
            }

            foreach (var issuer in ca.Claims.TrustedIssuers)
            {
                if (string.IsNullOrWhiteSpace(issuer.Issuer) || !valid.Contains(issuer.Issuer))
                {
                    failures.Add("Gateway:CallerAttributes:Claims:TrustedIssuers must list only issuers of the configured bearer scheme (ValidIssuers).");
                    break;
                }
            }
        }
    }
}
