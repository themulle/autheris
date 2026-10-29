using System.Security.Claims;
using Autheris.Application.Audit;
using Autheris.Application.Governance.ConsentRequests;
using Autheris.Application.Governance.Grants;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Autheris.Tests.Unit.ConsentRequests;

/// <summary>WP-0 (PLAN-GOV-113): claim shapes of every shipped authentication scheme, built the way the scheme delivers them.</summary>
internal static class DecisionPrincipals
{
    private const string ScopeUri = "http://schemas.microsoft.com/identity/claims/scope";
    private const string ObjectIdUri = "http://schemas.microsoft.com/identity/claims/objectidentifier";

    private static ClaimsPrincipal Build(string authType, IEnumerable<Claim> claims, string[] roles)
    {
        var all = claims.Append(DefaultTenantCaller.Claim).Concat(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        return new ClaimsPrincipal(new ClaimsIdentity(all, authType, ClaimTypes.Name, ClaimTypes.Role));
    }

    public static ClaimsPrincipal Basic(string sid, params string[] roles) =>
        Build("Basic", [new(ClaimTypes.NameIdentifier, sid), new(ClaimTypes.Name, "alice")], roles);

    public static ClaimsPrincipal Kerberos(string sid, params string[] roles) =>
        Build("Negotiate", [new(ClaimTypes.PrimarySid, sid), new(ClaimTypes.Name, "CORP\\alice")], roles);

    public static ClaimsPrincipal ForwardAuth(string sid, params string[] roles) =>
        Build("ForwardAuth", [new(ClaimTypes.NameIdentifier, sid), new(ClaimTypes.PrimarySid, sid), new(ClaimTypes.Name, "alice"), new(ClaimTypes.Email, "alice@corp.example")], roles);

    /// <summary>Entra v2 delegated token with raw claim names (no inbound mapping), including a human name.</summary>
    public static ClaimsPrincipal EntraDelegatedRaw(string oid, params string[] roles) =>
        Build("Bearer", [new("oid", oid), new("upn", "alice@corp.example"), new("preferred_username", "alice@corp.example"), new("azp", "app-client"), new("scp", "Autheris.Access data.access.review")], roles);

    /// <summary>Entra delegated token after JwtBearer claim mapping (oid and scp as URIs, upn as ClaimTypes.Upn).</summary>
    public static ClaimsPrincipal EntraDelegatedMapped(string oid, params string[] roles) =>
        Build("AuthenticationTypes.Federation", [new(ObjectIdUri, oid), new(ClaimTypes.Upn, "alice@corp.example"), new("azp", "app-client"), new(ScopeUri, "Autheris.Access data.access.review"), new(ClaimTypes.NameIdentifier, "sub-1")], roles);

    /// <summary>Delegated Entra token with scp, oid and azp but NO upn, preferred_username or email (W-11).</summary>
    public static ClaimsPrincipal EntraDelegatedNoHumanName(string oid, params string[] roles) =>
        Build("Bearer", [new("oid", oid), new("azp", "app-client"), new("scp", "Autheris.Access data.access.review")], roles);

    /// <summary>The same token after JwtBearer mapping: oid and scp as URIs, unique_name as ClaimTypes.Name, no human claim the resolver tests.</summary>
    public static ClaimsPrincipal EntraDelegatedMappedNoHumanName(string oid, params string[] roles) =>
        Build("AuthenticationTypes.Federation", [new(ObjectIdUri, oid), new(ClaimTypes.Name, "Alice Example"), new("azp", "app-client"), new(ScopeUri, "Autheris.Access data.access.review")], roles);

    /// <summary>ADFS access token after JwtBearer mapping: upn and unique_name mapped, sub as NameIdentifier, appid and the scope URI.</summary>
    public static ClaimsPrincipal AdfsMapped(string sid, params string[] roles) =>
        Build("AuthenticationTypes.Federation", [new(ClaimTypes.NameIdentifier, sid), new(ClaimTypes.Upn, "alice@corp.example"), new(ClaimTypes.Name, "CORP\\alice"), new("appid", "adfs-client"), new(ScopeUri, "openid profile data.access.review")], roles);

    /// <summary>ADFS token whose only name claim is unique_name (mapped to ClaimTypes.Name), carrying a scope.</summary>
    public static ClaimsPrincipal AdfsMappedUniqueNameOnly(string sid, params string[] roles) =>
        Build("AuthenticationTypes.Federation", [new(ClaimTypes.NameIdentifier, sid), new(ClaimTypes.PrimarySid, sid), new(ClaimTypes.Name, "CORP\\alice"), new("appid", "adfs-client"), new(ScopeUri, "openid data.access.review")], roles);

    public static ClaimsPrincipal AppOnlyIdtyp(string oid, params string[] roles) =>
        Build("Bearer", [new("idtyp", "app"), new("oid", oid), new("sub", oid), new("appid", "app-client")], roles);

    public static ClaimsPrincipal AppIdOnly(string appId, params string[] roles) =>
        Build("Bearer", [new("appid", appId)], roles);

    public static ClaimsPrincipal AzpOnly(string appId, params string[] roles) =>
        Build("Bearer", [new("azp", appId)], roles);

    public static ClaimsPrincipal ClientIdOnly(string appId, params string[] roles) =>
        Build("Bearer", [new("client_id", appId)], roles);

    /// <summary>Client-credential principal of a non-Entra IdP (ServiceAccount provider).</summary>
    public static ClaimsPrincipal ServiceAccount(string clientId, params string[] roles) =>
        Build("Bearer", [new("client_id", clientId), new(ClaimTypes.NameIdentifier, clientId)], roles);

    public static ClaimsPrincipal Certificate(string subject, params string[] roles) =>
        Build("Certificate", [new(ClaimTypes.NameIdentifier, subject), new(ClaimTypes.Name, subject)], roles);

    public static ClaimsPrincipal ReadOnly(string sid, params string[] roles)
    {
        var principal = Basic(sid, roles);
        TokenAccessScope.MarkReadOnly((ClaimsIdentity)principal.Identity!);
        return principal;
    }

    public static ClaimsPrincipal Unauthenticated() => new(new ClaimsIdentity());
}

internal sealed class RecordingAuditWriter : IAuditWriter
{
    public List<AuditLogEntry> Entries { get; } = [];
    public bool ThrowOnWrite { get; set; }

    /// <summary>When set, every write throws this exception (for example an <see cref="OperationCanceledException"/>).</summary>
    public Exception? ThrowException { get; set; }

    public Task RecordRequiredAsync(AuditLogEntry entry, CancellationToken ct = default)
    {
        if (ThrowException != null)
        {
            throw ThrowException;
        }

        if (ThrowOnWrite)
        {
            throw new AuditUnavailableException("audit down");
        }

        lock (Entries) Entries.Add(entry);
        return Task.CompletedTask;
    }

    public Task RecordBestEffortAsync(AuditLogEntry entry, CancellationToken ct = default) => RecordRequiredAsync(entry, ct);

    public Task RecordAsync(AuditLogEntry entry, CancellationToken ct = default) => RecordRequiredAsync(entry, ct);
}

internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

internal sealed class TestHttpContextAccessor(HttpContext context) : IHttpContextAccessor
{
    public HttpContext? HttpContext { get; set; } = context;
}

/// <summary>Captures log entries (level, rendered message, exception) so a test can assert what was logged and what was not.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    public List<(LogLevel Level, string Category, string Message, Exception? Exception)> Entries { get; } = [];

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(CapturingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (owner.Entries) owner.Entries.Add((logLevel, category, formatter(state, exception), exception));
        }
    }
}

internal static class DecisionServices
{
    /// <summary>Builds the request services the decision resolvers need; the guard and the grant policy can be left out (composition tests).</summary>
    public static ServiceProvider Build(
        RecordingAuditWriter? audit = null,
        bool withGuard = true,
        bool withGrantPolicy = true,
        IndividualGrantOptions? grantOptions = null,
        TimeProvider? time = null,
        CapturingLoggerProvider? logs = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(logs ?? new CapturingLoggerProvider()));
        services.AddSingleton<IIdentitySubjectResolver, IdentitySubjectResolver>();
        if (withGuard)
        {
            services.AddSingleton<IConsentDecisionGuard, ConsentDecisionGuard>();
        }

        if (withGrantPolicy)
        {
            services.AddSingleton<IGrantPolicy>(new GrantPolicy(Options.Create(grantOptions ?? new IndividualGrantOptions())));
        }

        services.AddSingleton<IAuditWriter>(audit ?? new RecordingAuditWriter());
        services.AddSingleton(time ?? TimeProvider.System);
        services.AddSingleton<IConsentDecisionDenialRecorder>(sp =>
            new ConsentDecisionDenialRecorder(sp.GetRequiredService<IAuditWriter>(), sp.GetRequiredService<TimeProvider>()));
        return services.BuildServiceProvider();
    }

    public static IHttpContextAccessor Accessor(ClaimsPrincipal principal, IServiceProvider services, TenantId? tenant = null)
    {
        var context = new DefaultHttpContext { User = principal, RequestServices = services };
        if (tenant != null)
        {
            context.Items["TenantId"] = tenant.Value;
        }

        return new TestHttpContextAccessor(context);
    }
}
