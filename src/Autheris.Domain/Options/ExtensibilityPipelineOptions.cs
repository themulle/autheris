using System.ComponentModel.DataAnnotations;

namespace Autheris.Domain.Options;

/// <summary>Behaviour of the extensibility pipeline when an interceptor throws or exceeds its time budget.</summary>
public enum InterceptorFailurePolicy
{
    /// <summary>Ingress: the request is denied. Egress: the failure propagates and the response is not delivered.</summary>
    FailClosed = 0,

    /// <summary>The failing interceptor is skipped (logged) and the pipeline continues.</summary>
    FailOpen = 1
}

/// <summary>Bound to <c>Gateway:ExtensibilityPipeline</c>.</summary>
public sealed class ExtensibilityPipelineOptions
{
    public const string SectionName = "Gateway:ExtensibilityPipeline";

    /// <summary>Policy for ingress interceptors (access-control decisions). Default fail-closed.</summary>
    public InterceptorFailurePolicy IngressFailurePolicy { get; init; } = InterceptorFailurePolicy.FailClosed;

    /// <summary>Policy for egress interceptors (header / body decoration). Default fail-open.</summary>
    public InterceptorFailurePolicy EgressFailurePolicy { get; init; } = InterceptorFailurePolicy.FailOpen;

    /// <summary>Per-interceptor time budget in milliseconds.</summary>
    [Range(1, 600000)]
    public int InterceptorTimeoutMs { get; init; } = 5000;
}

/// <summary>Plugin loading, integrity verification and auth plugin configuration (<c>Gateway:Plugins</c>).</summary>
public sealed class PluginsOptions
{
    /// <summary>Directory scanned for plugin assemblies at startup.</summary>
    public string Directory { get; init; } = "plugins";

    /// <summary>When true, plugins must carry a matching SHA-256 entry in <see cref="TrustedPluginHashes"/>.</summary>
    public bool RequireIntegrityManifest { get; init; } = false;

    /// <summary>Map of trusted plugin assembly file names to expected hex-encoded SHA-256 digests.</summary>
    public Dictionary<string, string> TrustedPluginHashes { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Authentication plugin capability and enablement settings (<c>Gateway:Plugins:Auth</c>).</summary>
    public Autheris.Domain.Options.Plugins.AuthPluginOptions Auth { get; init; } = new();
}

