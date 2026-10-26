using System;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.ComponentModel.DataAnnotations;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Api.Hosting;
using Autheris.Api.Middleware;
using Autheris.Application.Interfaces;
using Autheris.Application.OpenMetadata.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Security;
using Autheris.Application.Services;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Options;
using Autheris.GraphQL.Catalog;
using Autheris.GraphQL.Federation;
using Autheris.GraphQL.Types;
using Autheris.Infrastructure.Cache;
using Autheris.Infrastructure.Health;
using Autheris.Infrastructure.Idempotency;
using Autheris.Infrastructure.Messaging;
using Autheris.Extensions;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.RateLimiting;
using Autheris.Infrastructure.Security;
using Autheris.Api.Security;
using Autheris.Application.Plugins;
using Autheris.Application.Governance;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Governance.Services;
using Autheris.Application.Lineage;
using Autheris.Application.Workflows;
using Autheris.Application.Dbt.Interfaces;
using Autheris.Application.Dbt.Services;
using Autheris.Application.DataCatalog.Interfaces;
using Autheris.Application.DataCatalog.Services;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.ResourceGroups;
using Autheris.Application.Observability;
using Autheris.Application.OData.Interfaces;
using Autheris.Application.OData.Services;
using Autheris.Infrastructure.Itsm;
using Autheris.Infrastructure.Lineage;
using Autheris.Infrastructure.Plugins;
using Autheris.Domain.Diagnostics;
using Autheris.Application.Caching.Interfaces;
using Autheris.Infrastructure.Garnet;
using Autheris.Infrastructure.Serialization;
using Autheris.Application.Caching.Services;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Application.Streaming.Services;
using Autheris.Infrastructure.Streaming;
using Autheris.GraphQL.Subscriptions;
using Autheris.Infrastructure.Cdn;
using Autheris.Application.SchemaRegistry;
using Autheris.Application.Extensibility;
using Autheris.Application.Extensibility.Interceptors;
using Autheris.Application.Sql;
using Autheris.Application.Serialization;
using Autheris.Application.SchemaRegistry.Validation;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Trace;
using OpenTelemetry.Metrics;
using StackExchange.Redis;
using HotChocolate.Execution.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Autheris.Api.Configuration.Validation;

namespace Autheris.Api.Extensions;

public static partial class GatewayServiceCollectionExtensions
{
    public static IServiceCollection AddGatewayInfrastructure(
        this IServiceCollection services,
        GatewayOptions gatewayOptions,
        IHostEnvironment? environment = null)
    {
        services.TryAddSingleton<IConfiguration>(static _ => new ConfigurationBuilder().Build());
        var hostEnv = environment ?? (services.FirstOrDefault(d => d.ServiceType == typeof(IHostEnvironment))?.ImplementationInstance as IHostEnvironment);

        // WP-ARC-B-12: must stay the first hosted service so the governance schema is migrated before anything else starts.
        services.AddAutherisGovernanceMigrations();
        services.AddAutherisEvents(gatewayOptions);
        services.AddAutherisStorage(gatewayOptions, hostEnv);
        services.AddAutherisGovernance(gatewayOptions);
        services.AddAutherisSecurity(gatewayOptions, hostEnv);
        services.AddAutherisSqlEngine(gatewayOptions);
        services.AddAutherisExecution(gatewayOptions, hostEnv);
        services.AddAutherisDataApi();
        services.AddAutherisMcp(gatewayOptions);
        services.AddAutherisStartupWarmup();
        services.AddAutherisCore(gatewayOptions, hostEnv);

        return services;
    }
    public static IServiceCollection AddGatewayAuth(
        this IServiceCollection services,
        GatewayOptions gatewayOptions,
        IHostEnvironment environment)
    {
        services.AddSingleton<ITrustedProxyValidator, TrustedProxyValidator>();
        services.AddTransient<IClaimsTransformation, EnterpriseClaimsTransformation>();

        var authBuilder = services.AddAuthentication(options =>
        {
            options.DefaultScheme = GatewayAuthSchemes.DefaultScheme;
            options.DefaultChallengeScheme = GatewayAuthSchemes.DefaultScheme;
        });

        // 1. Basic Authentication
        authBuilder.AddScheme<AuthenticationSchemeOptions, BasicAuthenticationHandler>(
            GatewayAuthSchemes.Basic, _ => { });

        // 1a. RejectScheme and Scim Authentication (Wave 2)
        authBuilder.AddScheme<AuthenticationSchemeOptions, RejectAuthenticationHandler>(
            GatewayAuthSchemes.RejectScheme, _ => { });
        authBuilder.AddScheme<AuthenticationSchemeOptions, ScimAuthenticationHandler>(
            GatewayAuthSchemes.Scim, _ => { });

        // 1b. F-AUTH-DX: cookie session after a successful Basic login (allowlisted non-production environments only)
        var isBasicSessionAllowed = BasicAuthSession.IsAllowed(gatewayOptions, environment);
        var basicSessionCookieName = gatewayOptions.Authentication.BasicAuth.Session.CookieName;
        if (isBasicSessionAllowed)
        {
            var dataProtection = services.AddDataProtection().SetApplicationName("Autheris.Gateway");
            if (!string.IsNullOrWhiteSpace(gatewayOptions.Authentication.BasicAuth.Session.KeyDirectory))
            {
                dataProtection.PersistKeysToFileSystem(new System.IO.DirectoryInfo(gatewayOptions.Authentication.BasicAuth.Session.KeyDirectory));
            }

            services.AddSingleton<BasicAuthSessionCookieEvents>();
            authBuilder.AddCookie(BasicAuthSession.SchemeName, cookie =>
                BasicAuthSession.ConfigureCookie(cookie, gatewayOptions.Authentication.BasicAuth.Session));
            Console.WriteLine(
                "[Autheris] WARNING: BasicAuth.Session is active (developer login cookie). Never enable it in Production.");
        }
        else if (BasicAuthSession.GetInactiveReason(gatewayOptions, environment) is { } inactiveReason)
        {
            Console.WriteLine($"[Autheris] WARNING: BasicAuth.Session is configured but inactive: {inactiveReason}.");
        }

        // 2. Traefik / Kubernetes Ingress ForwardAuth
        authBuilder.AddScheme<AuthenticationSchemeOptions, ForwardAuthAuthenticationHandler>(
            GatewayAuthSchemes.ForwardAuth, _ => { });
        services.AddHostedService<ForwardAuthSecretStartupValidator>();

        // 3. Windows Negotiate (Kerberos / NTLM) or TestAuthHandler
#if AUTHERIS_TEST_AUTH
        bool isTestAuthAllowed = environment.IsDevelopment() &&
            (gatewayOptions.Authentication.EnableTestAuthHandler || gatewayOptions.IsAnonymousAccessAllowed);

        if (isTestAuthAllowed)
        {
            authBuilder.AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                TestAuthHandler.SchemeName, _ => { });
        }
        else
#endif
        if (gatewayOptions.Authentication.IsKerberosConfigured)
        {
            // Review E-2: never persist credentials on the (possibly shared, reverse-proxied) upstream connection, and
            // with RequireKerberosOnly reject every Negotiate identity that is not Kerberos (e.g. NTLM).
            authBuilder.AddNegotiate(NegotiateDefaults.AuthenticationScheme, negotiate =>
                NegotiateHardening.Configure(negotiate, gatewayOptions.Authentication.RequireKerberosOnly));
        }

        // 4. Microsoft Entra ID (Azure AD) and/or AD FS JWT Bearer
        var entraConfig = gatewayOptions.Authentication.EntraId;
        var adfsConfig = gatewayOptions.Authentication.Adfs;

        // API-14: with Entra ID and AD FS both enabled a single JwtBearer scheme could only load the signing keys of one
        // authority (Entra), so every AD FS token failed. Each IdP then gets its own scheme with its own metadata;
        // Bearer tokens are routed by their (unverified) issuer and fully validated by the selected scheme.
        bool splitJwtSchemes = entraConfig.Enabled && adfsConfig.Enabled;
        authBuilder.AddJwtBearer(GatewayAuthSchemes.JwtBearer, options => ConfigureJwtBearer(options, useEntra: entraConfig.Enabled, useAdfs: adfsConfig.Enabled && !splitJwtSchemes));
        // MCP OAuth discovery (RFC 9728 protected resource metadata) for the configured token issuers.
        Autheris.Api.Mcp.GatewayMcpOAuth.AddGatewayMcpOAuth(authBuilder, gatewayOptions);
        if (splitJwtSchemes)
        {
            authBuilder.AddJwtBearer(GatewayAuthSchemes.JwtBearerAdfs, options => ConfigureJwtBearer(options, useEntra: false, useAdfs: true));
        }

        void ConfigureJwtBearer(Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerOptions options, bool useEntra, bool useAdfs)
        {
            // Review E-1: JwtBearer keeps the default inbound claim mapping (sub -> NameIdentifier, oid -> objectidentifier
            // URI); revocation lookups (GetLookupKeys) accept both spellings.
            // SEC SG-37: RequireHttpsMetadata must always be true outside Development.
            options.RequireHttpsMetadata = !environment.IsDevelopment() ||
                                           (useEntra && entraConfig.RequireHttpsMetadata) ||
                                           (useAdfs && adfsConfig.RequireHttpsMetadata);

            if (useEntra && !string.IsNullOrWhiteSpace(entraConfig.TenantId))
            {
                var instance = string.IsNullOrWhiteSpace(entraConfig.Instance)
                    ? "https://login.microsoftonline.com/"
                    : entraConfig.Instance.TrimEnd('/') + "/";
                options.Authority = $"{instance}{entraConfig.TenantId}/v2.0";
            }
            else if (useAdfs && !string.IsNullOrWhiteSpace(adfsConfig.Authority))
            {
                options.Authority = adfsConfig.Authority.TrimEnd('/');
                if (!string.IsNullOrWhiteSpace(adfsConfig.MetadataAddress))
                {
                    options.MetadataAddress = adfsConfig.MetadataAddress;
                }
            }

            var validIssuers = new List<string>();
            var validAudiences = new List<string>();
            var entraIssuers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (useEntra)
            {
                if (!string.IsNullOrWhiteSpace(entraConfig.TenantId))
                {
                    var instance = string.IsNullOrWhiteSpace(entraConfig.Instance)
                        ? "https://login.microsoftonline.com/"
                        : entraConfig.Instance.TrimEnd('/') + "/";
                    entraIssuers.Add($"{instance}{entraConfig.TenantId}/v2.0");
                    entraIssuers.Add($"https://sts.windows.net/{entraConfig.TenantId}/");
                    validIssuers.AddRange(entraIssuers);
                }
                if (!string.IsNullOrWhiteSpace(entraConfig.Audience)) validAudiences.Add(entraConfig.Audience);
                if (!string.IsNullOrWhiteSpace(entraConfig.ClientId)) validAudiences.Add(entraConfig.ClientId);
            }

            if (useAdfs)
            {
                if (!string.IsNullOrWhiteSpace(adfsConfig.Authority))
                {
                    validIssuers.Add(adfsConfig.Authority.TrimEnd('/'));
                    validIssuers.Add($"{adfsConfig.Authority.TrimEnd('/')}/services/trust");
                }
                if (!string.IsNullOrWhiteSpace(adfsConfig.Audience)) validAudiences.Add(adfsConfig.Audience);
            }

            // SEC M-02: Issuer and audience are ALWAYS validated (fail-closed). If no issuer/audience is
            // configured, no token can pass validation; outside Development startup is aborted beforehand.
            options.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuers = validIssuers.Count > 0 ? validIssuers : null,
                ValidateAudience = true,
                ValidAudiences = validAudiences.Count > 0 ? validAudiences : null,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ClockSkew = TimeSpan.FromMinutes(2)
            };
            options.IncludeErrorDetails = false;

            if (useEntra)
            {
                // Finding 3.3 / R13: scope and token-kind rules apply to Entra tokens only (ADFS may share this scheme).
                options.Events ??= new Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerEvents();
                options.Events.OnTokenValidated = ctx =>
                {
                    if (ctx.Principal != null && entraIssuers.Contains(ctx.SecurityToken?.Issuer ?? string.Empty))
                    {
                        var resolver = ctx.HttpContext.RequestServices.GetRequiredService<IIdentitySubjectResolver>();
                        var failure = EntraTokenPolicy.Apply(ctx.Principal, entraConfig, resolver);
                        if (failure != null)
                        {
                            ctx.Fail(failure);
                        }
                    }

                    return Task.CompletedTask;
                };
            }
        }

        // 5. Smart Dynamic Policy Scheme: Route requests based on Authorization header or ForwardAuth
        authBuilder.AddPolicyScheme(GatewayAuthSchemes.DefaultScheme, "Gateway Smart Authentication", options =>
        {
            options.ForwardDefaultSelector = context =>
            {
                var authHeader = context.Request.Headers.Authorization.ToString();

                // 1. Explicit Authorization headers have top priority (prevents ForwardAuth Header-Preemption DoS)
                // API-07: the opaque SCIM secret is not a JWT; route it to the Scim scheme so valid provisioning calls are authenticated
                // before the unauthenticated-bearer IP budget and do not produce JwtBearer failures.
                if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && GatewayAuthSchemes.IsScimPath(context.Request.Path))
                {
                    return GatewayAuthSchemes.Scim;
                }

                if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    return GatewayAuthSchemes.SelectJwtScheme(authHeader["Bearer ".Length..], gatewayOptions);
                }

                if (authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
                {
                    if (gatewayOptions.Authentication.RequireKerberosOnly)
                    {
                        return gatewayOptions.Authentication.IsKerberosConfigured
                            ? NegotiateDefaults.AuthenticationScheme
                            : GatewayAuthSchemes.JwtBearer;
                    }
                    if (gatewayOptions.Authentication.BasicAuth.Enabled)
                    {
                        return GatewayAuthSchemes.Basic;
                    }
                    return gatewayOptions.Authentication.IsKerberosConfigured
                        ? NegotiateDefaults.AuthenticationScheme
                        : GatewayAuthSchemes.JwtBearer;
                }

                if (authHeader.StartsWith("Negotiate ", StringComparison.OrdinalIgnoreCase))
                {
                    return gatewayOptions.Authentication.IsKerberosConfigured
                        ? NegotiateDefaults.AuthenticationScheme
                        : GatewayAuthSchemes.JwtBearer;
                }

                if (!gatewayOptions.Authentication.RequireKerberosOnly &&
                    authHeader.StartsWith("NTLM ", StringComparison.OrdinalIgnoreCase))
                {
                    return gatewayOptions.Authentication.IsKerberosConfigured
                        ? NegotiateDefaults.AuthenticationScheme
                        : GatewayAuthSchemes.JwtBearer;
                }

                // 2. ForwardAuth (Traefik / Kubernetes Ingress) when enabled and proxy headers are present
                if (gatewayOptions.Authentication.ForwardAuth.Enabled)
                {
                    var fwdUserHeader = string.IsNullOrWhiteSpace(gatewayOptions.Authentication.ForwardAuth.UserHeader)
                        ? "X-Forwarded-User"
                        : gatewayOptions.Authentication.ForwardAuth.UserHeader;

                    if (context.Request.Headers.ContainsKey(fwdUserHeader) ||
                        context.Request.Headers.ContainsKey("X-Forwarded-User") ||
                        context.Request.Headers.ContainsKey("X-Auth-Request-User") ||
                        context.Request.Headers.ContainsKey("X-Forwarded-Preferred-Username"))
                    {
                        return GatewayAuthSchemes.ForwardAuth;
                    }
                }

                // 2b. F-AUTH-DX: session cookie (explicit Authorization headers above always win)
                if (isBasicSessionAllowed && context.Request.Cookies.ContainsKey(basicSessionCookieName))
                {
                    return BasicAuthSession.SchemeName;
                }

                // 3. Development Test Auth Simulation or Insecure Anonymous Access
#if AUTHERIS_TEST_AUTH
                if (isTestAuthAllowed)
                {
                    if (context.Request.Headers.ContainsKey("X-Test-User-Sid") ||
                        context.Request.Headers.ContainsKey("X-Test-AppId") ||
                        gatewayOptions.IsAnonymousAccessAllowed)
                    {
                        return TestAuthHandler.SchemeName;
                    }
                }
#endif

                var isHtml = context.Request.Headers.Accept.Any(a => a != null && a.Contains("text/html", StringComparison.OrdinalIgnoreCase));
                if (isHtml && gatewayOptions.Authentication.BasicAuth.Enabled)
                {
                    // Browser request: Prefer Basic challenge when enabled so browser opens native dialog
                    return GatewayAuthSchemes.Basic;
                }

                // 4. Fallback challenge when unauthenticated
                if (gatewayOptions.Authentication.BasicAuth.Enabled &&
                    !gatewayOptions.Authentication.RequireKerberosOnly &&
                    string.IsNullOrWhiteSpace(authHeader))
                {
                    return GatewayAuthSchemes.Basic;
                }

                if (gatewayOptions.Authentication.IsKerberosConfigured)
                {
                    return NegotiateDefaults.AuthenticationScheme;
                }

                return GatewayAuthSchemes.JwtBearer;
            };
        });

        // SEC M-03: Authenticated-user fallback policy and named role policies.
        services.AddAuthorization(GatewayPolicies.Configure);
        if (DevFeatures.Resolve(gatewayOptions, environment.IsDevelopment()).VerboseErrors)
        {
            // F-AUTH-DX: explain policy 403s and give JSON clients structured exception details (traceId)
            services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationMiddlewareResultHandler, DevAuthorizationResultHandler>();
            services.AddProblemDetails();
        }
        // O9: every unhandled exception becomes application/problem+json without details (all environments)
        services.AddProblemDetails();
        services.AddExceptionHandler<Autheris.Api.Middleware.GatewayExceptionHandler>();
        services.AddSingleton<Autheris.Application.Interfaces.IGatewayRoleEvaluator, Autheris.Application.Security.GatewayRoleEvaluator>();
        services.AddHttpContextAccessor();

        return services;
    }

    internal static void ValidateGatewayOptions(GatewayOptions options, IHostEnvironment environment)
        => ValidateGatewayOptions(options, environment, System.Environment.GetEnvironmentVariable);

    internal static void ValidateGatewayOptions(GatewayOptions options, IHostEnvironment environment, Func<string, string?> getEnvironmentVariable)
    {
        GatewayStartupValidator.ValidateStartup(options, environment, getEnvironmentVariable);
    }

    internal static bool IsSupportedGovernanceDbProvider(string? provider)
        => GatewayStartupValidator.IsSupportedGovernanceDbProvider(provider);
}
