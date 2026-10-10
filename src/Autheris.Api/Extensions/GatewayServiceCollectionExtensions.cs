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
using Autheris.Infrastructure.Diagnostics;
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

namespace Autheris.Api.Extensions;

public static class GatewayServiceCollectionExtensions
{
    public static GatewayOptions AddGatewayOptions(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        services.AddOptions<GatewayOptions>()
            .Bind(configuration.GetSection(GatewayOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(opts =>
                opts.Audit.Retention.SecurityDays >= 365,
                "Security violation: Audit:Retention:SecurityDays must be at least 365 days.")
            .Validate(opts =>
                opts.Audit.AuditLogRetentionDays >= 365,
                "Security violation: Audit:AuditLogRetentionDays must be at least 365 days.")
            .Validate(opts =>
                Enum.IsDefined(opts.RowFilters.SubqueryStrategy),
                "Gateway:RowFilters:SubqueryStrategy must be Exists, InCorrelated or In.")
            .Validate(opts =>
                !opts.Casbin.Enabled || string.IsNullOrWhiteSpace(opts.Casbin.ModelPath) || System.IO.File.Exists(System.IO.Path.GetFullPath(opts.Casbin.ModelPath)),
                "Gateway:Casbin is enabled, but the configured ModelPath file was not found.")
            .Validate(opts =>
                !opts.Casbin.Enabled || !string.IsNullOrWhiteSpace(opts.Casbin.PolicyPath),
                "Gateway:Casbin is enabled, but PolicyPath is not configured. Failing closed.")
            .Validate(opts =>
                !opts.Casbin.Enabled || string.IsNullOrWhiteSpace(opts.Casbin.PolicyPath) || System.IO.File.Exists(System.IO.Path.GetFullPath(opts.Casbin.PolicyPath)),
                "Gateway:Casbin is enabled, but the configured PolicyPath file was not found.")
            .Validate(opts =>
                opts.HighAvailability.ShutdownTimeoutSeconds >= opts.HighAvailability.QueryTimeoutSeconds + 10,
                "NF-HA-01 violation: ShutdownTimeoutSeconds must be at least 10s greater than QueryTimeoutSeconds.")
            .Validate(opts =>
                opts.HighAvailability.TerminationGracePeriodSeconds >= opts.HighAvailability.DrainDelaySeconds + opts.HighAvailability.ShutdownTimeoutSeconds + 10,
                "NF-HA-01 violation: TerminationGracePeriodSeconds must be greater than DrainDelay + ShutdownTimeout + 10s.")
            .Validate(opts =>
                !(opts.Authentication.RequireKerberosOnly && opts.Authentication.BasicAuth.Enabled),
                "Security conflict: BasicAuth must not be enabled when RequireKerberosOnly is set to true.")
            .Validate(opts =>
                environment.IsDevelopment() || !opts.Authentication.ForwardAuth.Enabled ||
                (!string.IsNullOrWhiteSpace(opts.Authentication.ForwardAuth.SharedSecret) || !string.IsNullOrWhiteSpace(opts.Authentication.ForwardAuth.SharedSecretKeyVaultRef)),
                "Security violation: Outside Development, ForwardAuth requires a configured SharedSecret or SharedSecretKeyVaultRef.")
            .Validate(opts =>
                environment.IsDevelopment() || !opts.Authentication.ForwardAuth.Enabled || opts.Authentication.ForwardAuth.RequireTrustedProxy,
                "Security violation: RequireTrustedProxy must not be set to false when ForwardAuth is enabled outside Development.")
            .Validate(opts =>
#if AUTHERIS_TEST_AUTH
                environment.IsDevelopment() || !opts.Authentication.EnableTestAuthHandler,
                "Security violation: EnableTestAuthHandler may be true ONLY in the Development environment.")
#else
                !opts.Authentication.EnableTestAuthHandler,
                "Security violation: EnableTestAuthHandler may be true ONLY in the Development environment.")
#endif
            .Validate(opts =>
                environment.IsDevelopment() || !opts.IsAnonymousAccessAllowed,
                "Security violation: danger_allow_anonymous_access may be true ONLY in the Development environment.")
            .Validate(opts =>
                environment.IsDevelopment() || !opts.AreUntrustedCertificatesAllowed,
                "Security violation: danger_allow_untrusted_certificates may be true ONLY in the Development environment.")
            .Validate(opts =>
                environment.IsDevelopment() || !opts.GraphQL.TrustedOrigins.Contains("*"),
                "Security violation: TrustedOrigins '*' (wildcard CORS) is prohibited outside the Development environment for security reasons (CSRF protection).")
            .Validate(opts =>
                environment.IsDevelopment() || opts.GraphQL.TrustedOrigins.All(o => o == "*" || (Uri.TryCreate(o, UriKind.Absolute, out var u) && string.Equals(u.Scheme, "https", StringComparison.OrdinalIgnoreCase))),
                "Security violation: Outside Development, TrustedOrigins must contain only HTTPS URLs.")
            .Validate(opts =>
                environment.IsDevelopment() || (
                    !string.IsNullOrWhiteSpace(opts.DataMasking.HmacSecretKeyVaultRef) &&
                    opts.DataMasking.HmacSecretKeyVaultRef != "DEV_INSECURE_TEST_KEY_ONLY" &&
                    opts.DataMasking.HmacSecretKeyVaultRef != "dev-only-hmac-salt-secure-fallback"
                ) || opts.IsInsecureTransportAllowed || opts.IsColumnMaskingDisabled,
                "NF-SEC-03 violation: Outside Development, HmacSecretKeyVaultRef must be a valid Key Vault secret reference.")
            .Validate(opts =>
                IsSupportedGovernanceDbProvider(opts.GovernanceDb.Provider),
                "The GovernanceDb provider currently supports only 'Sqlite', 'PostgreSql' or 'SqlServer'.")
            .Validate(opts =>
                !(opts.HighAvailability.MultiNodeClusterMode || opts.HighAvailability.Replicas > 1) ||
                !DataSourceProvider.Is(opts.GovernanceDb.Provider, DatabaseDialect.Sqlite),
                "Security violation (E-2): Multi-node cluster mode and more than 1 replica are not allowed with SQLite, because SQLite uses local database files per instance. Configure GovernanceDb.Provider = 'PostgreSql' or 'SqlServer' for cluster operation.")
            .Validate(opts =>
                environment.IsDevelopment() || !opts.OpenMetadata.Enabled ||
                (Uri.TryCreate(opts.OpenMetadata.ServerUrl, UriKind.Absolute, out var uri) && string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase)) ||
                opts.IsInsecureTransportAllowed,
                "Security violation: Outside Development, OpenMetadata.ServerUrl must use HTTPS.")
            .Validate(opts =>
                environment.IsDevelopment() || !(opts.HighAvailability.MultiNodeClusterMode || opts.HighAvailability.Replicas > 1) || opts.Caching.Redis.Enabled,
                "NF-HA-02 violation: In MultiNodeClusterMode or with more than one replica (PG-6), cluster-wide cache and epoch invalidation requires Caching.Redis.Enabled = true.")
            .Validate(opts =>
                environment.IsDevelopment() ||
                string.IsNullOrWhiteSpace(opts.Plugins.Directory) ||
                !Directory.Exists(System.IO.Path.GetFullPath(opts.Plugins.Directory)) ||
                opts.Plugins.RequireIntegrityManifest,
                "Security violation: Outside Development, a configured plugin directory requires Plugins.RequireIntegrityManifest = true.")
            .Validate(opts =>
                environment.IsDevelopment() || opts.Rebac.SeedTuples.Count == 0 ||
                !opts.Rebac.SeedTuples.Any(t => t.User.Contains("david", StringComparison.OrdinalIgnoreCase) || t.Object.Contains("prod", StringComparison.OrdinalIgnoreCase)),
                "Security critical: Demo or production-targeted ReBAC seed tuples are not permitted outside Development.")
            .ValidateOnStart();

        var gatewayOptions = configuration.GetSection(GatewayOptions.SectionName).Get<GatewayOptions>() ?? new GatewayOptions();
        ValidateGatewayOptions(gatewayOptions, environment);

        services.Configure<HostOptions>(o =>
        {
            var drainBuffer = gatewayOptions.HighAvailability.DrainDelaySeconds + gatewayOptions.HighAvailability.ShutdownTimeoutSeconds + 5;
            o.ShutdownTimeout = TimeSpan.FromSeconds(Math.Min(drainBuffer, gatewayOptions.HighAvailability.TerminationGracePeriodSeconds));
        });

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;

            if (gatewayOptions.ReverseProxy.Enabled)
            {
                // Preserve safe loopback defaults against spoofing
                options.KnownProxies.Add(System.Net.IPAddress.Loopback);
                options.KnownProxies.Add(System.Net.IPAddress.IPv6Loopback);

                foreach (var netStr in gatewayOptions.ReverseProxy.KnownNetworks)
                {
                    if (System.Net.IPNetwork.TryParse(netStr, out var network))
                    {
                        options.KnownIPNetworks.Add(network);
                    }
                }

                foreach (var proxyStr in gatewayOptions.ReverseProxy.KnownProxies)
                {
                    if (System.Net.IPAddress.TryParse(proxyStr, out var ip))
                    {
                        options.KnownProxies.Add(ip);
                    }
                }
            }
            else
            {
                options.KnownIPNetworks.Clear();
                options.KnownProxies.Clear();
            }
        });

        return gatewayOptions;
    }

    public static IServiceCollection AddGatewayInfrastructure(
        this IServiceCollection services,
        GatewayOptions gatewayOptions,
        IHostEnvironment? environment = null)
    {
        var hostEnv = environment ?? (services.FirstOrDefault(d => d.ServiceType == typeof(IHostEnvironment))?.ImplementationInstance as IHostEnvironment);

        services.AddAutherisStorage(gatewayOptions, hostEnv);
        services.AddAutherisGovernance(gatewayOptions);
        services.AddAutherisSecurity(gatewayOptions, hostEnv);
        services.AddAutherisSqlEngine(gatewayOptions);
        services.AddAutherisExecution(gatewayOptions, hostEnv);
        services.AddAutherisDataApi();
        services.AddAutherisMcp(gatewayOptions);
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
                if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    return GatewayAuthSchemes.SelectJwtScheme(authHeader["Bearer ".Length..], gatewayOptions);
                }

                if (authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
                {
                    if (gatewayOptions.Authentication.RequireKerberosOnly)
                    {
                        return NegotiateDefaults.AuthenticationScheme;
                    }
                    if (gatewayOptions.Authentication.BasicAuth.Enabled)
                    {
                        return GatewayAuthSchemes.Basic;
                    }
                    return NegotiateDefaults.AuthenticationScheme;
                }

                if (authHeader.StartsWith("Negotiate ", StringComparison.OrdinalIgnoreCase))
                {
                    return NegotiateDefaults.AuthenticationScheme;
                }

                if (!gatewayOptions.Authentication.RequireKerberosOnly &&
                    authHeader.StartsWith("NTLM ", StringComparison.OrdinalIgnoreCase))
                {
                    return NegotiateDefaults.AuthenticationScheme;
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

                return NegotiateDefaults.AuthenticationScheme;
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

    public static IServiceCollection AddGatewayGraphQL(
        this IServiceCollection services,
        GatewayOptions gatewayOptions,
        bool demoDataEnabled = true)
    {
        // RR-L3-05: In production without explicit opt-in, relaxed limits are capped to moderate thresholds
        var maxDepth = gatewayOptions.AreQueryLimitsRelaxed
            ? (gatewayOptions.AllowInsecureWarnFlagsInProduction ? 100 : 15)
            : gatewayOptions.GraphQL.MaxAllowedExecutionDepth;
        var maxCost = gatewayOptions.AreQueryLimitsRelaxed
            ? (gatewayOptions.AllowInsecureWarnFlagsInProduction ? 100000 : 5000)
            : gatewayOptions.GraphQL.MaxAllowedComplexity;

        // SEC M-16: Singleton, damit registrierte API-Keys und der Key-Cache über Requests hinweg bestehen.
        services.AddSingleton<IClientTierResolver, ClientTierResolver>();
        services.AddHttpClient<CloudflareCdnPurgeService>().AddSecureOutboundHandlers(EgressIntegrations.Cdn);
        services.AddHttpClient<FastlyCdnPurgeService>().AddSecureOutboundHandlers(EgressIntegrations.Cdn);
        services.AddTransient<ICdnCachePurgeService, CloudflareCdnPurgeService>();

        services.AddFusionFederationServices(gatewayOptions);

        services.AddSingleton<ErrorSanitizingFilter>();
        // R-GQL-12: one module instance (and change-detection timer) for the application, also across schema rebuilds.
        services.AddSingleton(sp => new CatalogGraphQlTypeModule(
            sp.GetRequiredService<ITableMetadataRepository>(),
            sp.GetRequiredService<ITableRelationRepository>(),
            sp.GetService<ILogger<CatalogGraphQlTypeModule>>(),
            TimeSpan.FromSeconds(gatewayOptions.GraphQL.CatalogSchemaRefreshSeconds)));
        services.AddSingleton<ISocketTokenValidator, JwtSocketTokenValidator>();
        services.AddSingleton<WebSocketAuthInterceptor>();

        var gqlBuilder = services
            .AddGraphQLServer()
            .UseInstrumentation()
            .UseExceptions()
            // R-GQL-6: wraps validation and execution so unknown and denied fields yield the same result.
            .UseRequest<GraphQlEnumerationShieldMiddleware>()
            .UseTimeout()
            .UseDocumentCache();

        // SEC H-08: Trusted-document enforcement must run BEFORE parsing/validation/execution.
        // HotChocolate's UseOnlyPersistedOperationAllowed() was previously appended after UseOperationExecution
        // without a document store and without OnlyAllowPersistedDocuments, i.e. it never took effect.
        // We enforce an allowlist of trusted documents (normalized SHA-256) directly after the document cache.
        if (gatewayOptions.GraphQL.PersistedQueriesOnly)
        {
            var trustedDocuments = TrustedDocumentStore.LoadFromDirectory(gatewayOptions.GraphQL.TrustedDocumentsDirectory);
            services.AddSingleton(trustedDocuments);
            gqlBuilder.UseRequest<TrustedDocumentsOnlyMiddleware>();
        }

        if (demoDataEnabled)
        {
            // Sample content (finance/hr root fields, InvoiceRecord): only with demo data enabled
            gqlBuilder
                .AddTypeExtension<DemoQueryExtensions>()
                .AddTypeExtension<InvoiceRecordExtensions>();
        }

        gqlBuilder
            .UseDocumentParser()
            .UseDocumentValidation()
            .UseRequest<Autheris.GraphQL.Interceptors.ReadOnlyOperationMiddleware>()
            .UseRequest<Autheris.GraphQL.Interceptors.DbtHealthExecutionMiddleware>()
            .UseRequest<Autheris.GraphQL.Interceptors.SchemaSunsettingExecutionMiddleware>()
            .UseRequest<Autheris.GraphQL.Interceptors.CdnCacheTagMiddleware>()
            .UseRequest<Autheris.GraphQL.Federation.SubgraphResultMaskingMiddleware>()
            .UseRequest<Autheris.GraphQL.Catalog.CatalogOperationCleanupMiddleware>()
            .UseOperationCache()
            .UseOperationResolver()
            .UseOperationVariableCoercion()
            .UseRequest<Autheris.GraphQL.Interceptors.CostAndQuotaMiddleware>()
            .UseOperationExecution()
            .AddApplicationService<IHostEnvironment>()
            .AddApplicationService<ErrorSanitizingFilter>()
            .AddApplicationService<WebSocketAuthInterceptor>()
            .AddApplicationService<ITableMetadataRepository>()
            .AddApplicationService<ITableRelationRepository>()
            .AddApplicationService<CatalogGraphQlTypeModule>()
            .AddTypeModule(sp => sp.GetRequiredService<CatalogGraphQlTypeModule>())
            .AddErrorFilter(sp => sp.GetRequiredService<ErrorSanitizingFilter>())
            .AddQueryType<Query>()
            .AddMutationType<Mutation>()
            .AddSubscriptionType<Subscription>()
            .AddInMemorySubscriptions()
            .AddSocketSessionInterceptor(sp => sp.GetRequiredService<WebSocketAuthInterceptor>())
            .AddDirectiveType<Autheris.GraphQL.Directives.McpToolDirectiveType>()
            .AddDirectiveType<Autheris.GraphQL.Directives.RebacDirectiveType>()
            .AddMaxExecutionDepthRule(maxDepth)
            .AddValidationRule<Autheris.GraphQL.Interceptors.QueryCostAnalyzerRule>((sp, _) =>
                new Autheris.GraphQL.Interceptors.QueryCostAnalyzerRule(
                    maxAllowedCost: maxCost,
                    maxResponseRows: gatewayOptions.GraphQL.MaxResponseRows,
                    onQueryTooComplex: () => GatewayDiagnostics.QueryTooComplexCounter.Add(1),
                    maxRootFields: gatewayOptions.AreQueryLimitsRelaxed
                        ? (gatewayOptions.AllowInsecureWarnFlagsInProduction ? 200 : 25)
                        : gatewayOptions.GraphQL.MaxRootFieldsPerOperation))
            // HotChocolate 16 adds the types __SchemaDefinition/__SearchResult (semantic introspection) to the schema.
            // Their names start with "__", which GraphQL reserves for introspection; graphql-js based clients (GraphiQL,
            // Apollo, codegen) reject the whole schema because of them. Autheris does not use the semantic search.
            .ModifyOptions(opt => opt.EnableSemanticIntrospection = false)
            .ModifyRequestOptions(opt =>
            {
                opt.ExecutionTimeout = TimeSpan.FromSeconds(gatewayOptions.HighAvailability.QueryTimeoutSeconds);
            });

        // SEC H-02: OpenSchema only opens catalog/OpenAPI documentation routes; it no longer enables introspection.
        if (!gatewayOptions.GraphQL.EnableIntrospection && !gatewayOptions.IsIntrospectionForced)
        {
            gqlBuilder.DisableIntrospection();

            // R-GQL-6: GET /graphql?sdl and /graphql/schema.graphql serve the SDL independently of introspection.
            gqlBuilder.ModifyServerOptions(opt => opt.EnableSchemaRequests = false);
        }

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
