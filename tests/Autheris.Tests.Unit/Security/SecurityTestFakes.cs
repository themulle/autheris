using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using Autheris.Api.Security;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Identity;
using Autheris.Application.Identity.Providers;
using Autheris.Domain.Identity;
using Autheris.Domain.Model;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Autheris.Tests.Unit.Security;

/// <summary>Role provider whose result is computed by a delegate.</summary>
internal sealed class FakeRoleProvider(string id, Func<IdentityRealm, ProviderRoleResult> result) : IRoleProvider
{
    public string ProviderId => id;

    public Task<ProviderRoleResult> GetRolesAsync(ResolvedIdentity identity, IdentityRealm realm, CancellationToken cancellationToken = default) =>
        Task.FromResult(result(realm));
}

internal sealed class FakeHostEnvironment(string environmentName = "") : IHostEnvironment
{
    public string EnvironmentName { get; set; } = environmentName;
    public string ApplicationName { get; set; } = "";
    public string ContentRootPath { get; set; } = "";
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

/// <summary>Event bus that can fail on subscribe or publish and raise <see cref="ConnectionRestored"/>.</summary>
internal sealed class FakeEventBus : IEventBus
{
    public bool FailSubscribe { get; init; }
    public bool FailPublish { get; init; }

    public event Action? ConnectionRestored;

    public void RaiseConnectionRestored() => ConnectionRestored?.Invoke();

    public Task PublishAsync<T>(string channel, T message, CancellationToken ct = default) =>
        FailPublish ? throw new InvalidOperationException("down") : Task.CompletedTask;

    public IDisposable Subscribe<T>(string channel, Func<T, Task> handler) =>
        FailSubscribe ? throw new InvalidOperationException("down") : new NoopDisposable();

    public Task<long> IncrementCounterAsync(string key, CancellationToken ct = default) => Task.FromResult(0L);

    public Task<long> GetCounterAsync(string key, CancellationToken ct = default) => Task.FromResult(0L);

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}

/// <summary>Graph adapter with delegate-driven results and a call counter.</summary>
internal sealed class FakeGraphAdapter : IGraphClientAdapter
{
    public Func<string, Task<IReadOnlyList<string>>> TransitiveMemberOf { get; init; } = _ => Task.FromResult<IReadOnlyList<string>>([]);
    public Func<string, Task<IReadOnlyList<string>>> AppRoleAssignments { get; init; } = _ => Task.FromResult<IReadOnlyList<string>>([]);
    public int TransitiveMemberOfCalls { get; private set; }

    public Task<IReadOnlyList<string>> GetTransitiveMemberOfAsync(string userKey, CancellationToken ct = default)
    {
        TransitiveMemberOfCalls++;
        return TransitiveMemberOf(userKey);
    }

    public Task<IReadOnlyList<string>> GetAppRoleAssignmentsAsync(string userKey, CancellationToken ct = default) =>
        AppRoleAssignments(userKey);
}

/// <summary>LDAP adapter with delegate-driven results; records every filter and call.</summary>
internal sealed class FakeLdapAdapter : ILdapDirectoryAdapter
{
    public Func<CancellationToken, Task<IReadOnlyList<string>>> FindUsers { get; init; } = _ => Task.FromResult<IReadOnlyList<string>>([]);
    public IReadOnlyList<byte[]> TokenGroups { get; init; } = [];
    public List<string> Filters { get; } = [];
    public int FindUserCalls => Filters.Count;

    public Task<IReadOnlyList<string>> FindUserDnsAsync(string escapedFilter, int maxResults, CancellationToken ct = default)
    {
        Filters.Add(escapedFilter);
        return FindUsers(ct);
    }

    public Task<IReadOnlyList<byte[]>> ReadTokenGroupsAsync(string userDistinguishedName, CancellationToken ct = default) =>
        Task.FromResult(TokenGroups);
}

/// <summary>
/// Minimal Redis double: a dispatch proxy for the multiplexer that hands out a subscriber proxy,
/// which captures the handler registered through <c>Subscribe</c>. Every other member returns its default.
/// </summary>
internal static class FakeRedis
{
    public sealed class Capture
    {
        public Action<RedisChannel, RedisValue>? OnMessage { get; set; }
    }

    public static IConnectionMultiplexer Multiplexer(Capture capture)
    {
        var subscriber = DispatchProxy.Create<ISubscriber, Proxy>();
        ((Proxy)(object)subscriber).Capture = capture;
        var mux = DispatchProxy.Create<IConnectionMultiplexer, Proxy>();
        ((Proxy)(object)mux).Subscriber = subscriber;
        return mux;
    }

#pragma warning disable CA1852 // DispatchProxy generates a subclass at runtime, so the type cannot be sealed.
    public class Proxy : DispatchProxy
#pragma warning restore CA1852
    {
        public Capture? Capture { get; set; }
        public ISubscriber? Subscriber { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null)
            {
                return null;
            }

            if (targetMethod.Name == nameof(IConnectionMultiplexer.GetSubscriber))
            {
                return Subscriber;
            }

            if (targetMethod.Name == nameof(ISubscriber.Subscribe) && Capture is not null)
            {
                Capture.OnMessage = args?.OfType<Action<RedisChannel, RedisValue>>().FirstOrDefault();
            }

            var returnType = targetMethod.ReturnType;
            if (returnType == typeof(Task))
            {
                return Task.CompletedTask;
            }

            return returnType.IsValueType && returnType != typeof(void) ? Activator.CreateInstance(returnType) : null;
        }
    }
}

/// <summary>Options monitor backed by a fixed instance or factory.</summary>
internal sealed class FakeOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T> where T : class
{
    public T CurrentValue => currentValue;
    public T Get(string? name) => currentValue;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

/// <summary>In-memory realm registry resolving a default realm or mapped realms.</summary>
internal sealed class FakeIdentityRealmRegistry : IIdentityRealmRegistry
{
    private readonly Dictionary<string, IdentityRealm> _realms = new(StringComparer.OrdinalIgnoreCase);
    private IdentityRealm? _defaultRealm;

    public FakeIdentityRealmRegistry(IdentityRealm? defaultRealm = null)
    {
        _defaultRealm = defaultRealm;
        if (defaultRealm is not null)
        {
            _realms[defaultRealm.RealmId] = defaultRealm;
        }
    }

    public FakeIdentityRealmRegistry(IEnumerable<IdentityRealm> realms, IdentityRealm? defaultRealm = null)
    {
        _defaultRealm = defaultRealm;
        foreach (var realm in realms)
        {
            _realms[realm.RealmId] = realm;
            _defaultRealm ??= realm;
        }
    }

    public IdentityRealm? DefaultRealm
    {
        get => _defaultRealm;
        set
        {
            _defaultRealm = value;
            if (value is not null)
            {
                _realms[value.RealmId] = value;
            }
        }
    }

    public IdentityRealm? ResolveByIssuer(string issuer) =>
        _realms.Values.FirstOrDefault(r => r.Issuers.Contains(issuer, StringComparer.OrdinalIgnoreCase)) ?? _defaultRealm;

    public IdentityRealm? ResolveRealmForScheme(string? issuer, string? authenticationType) =>
        (issuer != null ? ResolveByIssuer(issuer) : null) ?? _defaultRealm;

    public IdentityRealm? GetRealm(string realmId) => _realms.GetValueOrDefault(realmId) ?? _defaultRealm;
    public IdentityRealm? GetDefaultRealm() => _defaultRealm;
    public IReadOnlyList<IdentityRealm> GetAllRealms() => _realms.Values.ToList();
    public void RegisterRealm(IdentityRealm realm)
    {
        _realms[realm.RealmId] = realm;
        _defaultRealm ??= realm;
    }
    public bool TryGetRealm(string realmId, out IdentityRealm? realm) => _realms.TryGetValue(realmId, out realm);
}

/// <summary>Resolves a caller subject identity from principal or delegate.</summary>
internal sealed class FakeIdentitySubjectResolver(Func<ClaimsPrincipal?, SubjectIdentity>? resolver = null) : IIdentitySubjectResolver
{
    public SubjectIdentity ResolveSubject(ClaimsPrincipal? principal) =>
        resolver != null
            ? resolver(principal)
            : new SubjectIdentity(
                principal?.FindFirst("sub")?.Value ?? principal?.Identity?.Name ?? "test-user",
                SubjectType.User,
                IdentityProviderType.EntraId);
}

/// <summary>Identity pipeline double returning raw principal or executing a custom handler.</summary>
internal sealed class FakeIdentityPipeline : IIdentityPipeline
{
    public Func<ClaimsPrincipal, string, X509Certificate2?, CancellationToken, ValueTask<ClaimsPrincipal>>? Handler { get; set; }

    public ValueTask<ClaimsPrincipal> ProcessAsync(ClaimsPrincipal raw, string scheme, X509Certificate2? clientCert, CancellationToken ct) =>
        Handler != null ? Handler(raw, scheme, clientCert, ct) : ValueTask.FromResult(raw);
}

/// <summary>Generic dispatch-proxy fake returning default values for all interface members.</summary>
internal static class NullFake
{
    public static T Create<T>() where T : class => DispatchProxy.Create<T, NullProxy>();

#pragma warning disable CA1852
    public class NullProxy : DispatchProxy
#pragma warning restore CA1852
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null) return null;
            var returnType = targetMethod.ReturnType;
            if (returnType == typeof(Task)) return Task.CompletedTask;
            if (returnType == typeof(ValueTask)) return ValueTask.CompletedTask;
            if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var resultType = returnType.GetGenericArguments()[0];
                var defaultVal = resultType.IsValueType ? Activator.CreateInstance(resultType) : null;
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(resultType).Invoke(null, [defaultVal]);
            }
            if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(ValueTask<>))
            {
                var resultType = returnType.GetGenericArguments()[0];
                var defaultVal = resultType.IsValueType ? Activator.CreateInstance(resultType) : null;
                return Activator.CreateInstance(returnType, defaultVal);
            }
            return returnType.IsValueType && returnType != typeof(void) ? Activator.CreateInstance(returnType) : null;
        }
    }
}
