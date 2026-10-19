using System.ComponentModel.DataAnnotations;

namespace Autheris.Domain.Options;

public sealed class CachingOptions
{
    [Required] public L1MemoryCacheOptions L1MemoryCache { get; init; } = new();
    [Required] public RedisOptions Redis { get; init; } = new();
    [Required] public GarnetOptions Garnet { get; init; } = new();
    [Required] public EpochValidationOptions EpochValidation { get; init; } = new();
    [Required] public CdnOptions Cdn { get; init; } = new();
}

public sealed class GarnetOptions
{
    /// <summary>
    /// When true, the gateway starts an embedded Microsoft Garnet cache server (RESP-compatible, Tsavorite engine).
    /// </summary>
    public bool EnableEmbeddedServer { get; init; } = false;

    /// <summary>
    /// Bind address for the embedded Garnet server (default: 127.0.0.1).
    /// </summary>
    public string Host { get; init; } = "127.0.0.1";

    /// <summary>
    /// Port for the embedded Garnet server (default: 3278).
    /// </summary>
    [Range(1024, 65535)] public int Port { get; init; } = 3278;

    /// <summary>
    /// Disables internal console output of Garnet so stdout is not flooded with cache logs.
    /// </summary>
    public bool DisableConsole { get; init; } = true;

    /// <summary>
    /// Directory for persistence checkpoints (optional).
    /// </summary>
    public string? CheckpointDir { get; init; }

    /// <summary>
    /// SEC H-01: Secret reference for the Garnet password (--auth Password). Without it a random per-process password
    /// is generated that only the in-process client knows.
    /// </summary>
    public string? PasswordSecretRef { get; init; }

    /// <summary>
    /// SEC H-01: Optional TLS for the embedded Garnet server (--tls). Garnet is bound to loopback outside
    /// Development, so TLS is defense in depth only. Requires <see cref="TlsCertFile"/> (PFX).
    /// </summary>
    public bool EnableTls { get; init; } = false;

    /// <summary>
    /// SEC H-01: Path to the PFX certificate for Garnet TLS (--cert-file-name).
    /// </summary>
    public string? TlsCertFile { get; init; }

    /// <summary>
    /// SEC H-01: Secret reference for the PFX password (--cert-password); resolved via IKeyVaultSecretProvider
    /// or environment variable.
    /// </summary>
    public string? TlsCertPasswordSecretRef { get; init; }

    /// <summary>
    /// SEC H-01: Host name the in-process client expects in the Garnet server certificate (default: Host).
    /// </summary>
    public string? TlsSslHost { get; init; }

    /// <summary>
    /// SEC H-01: Optional SHA-256 or SHA-1 thumbprints (hex) of accepted Garnet server certificates
    /// (pinning, e.g. for self-signed certificates). Empty = regular chain validation.
    /// </summary>
    public List<string> TlsAllowedServerCertificateThumbprints { get; init; } = [];
}

public sealed class L1MemoryCacheOptions
{
    [Range(16, 4096)] public int SizeLimitMb { get; init; } = 512;
    [Range(1, 120)] public int DefaultTtlMinutes { get; init; } = 10;
    [Range(1, 600)] public int SensitiveTableTtlSeconds { get; init; } = 60;
    [Range(0, 5000)] public int AccessProfileEpochCacheMilliseconds { get; init; } = 1000;
}

public sealed class RedisOptions
{
    public bool Enabled { get; init; } = false;
    public string Configuration { get; init; } = "localhost:6379,abortConnect=false";
    public string InstanceName { get; init; } = "Autheris:";
    public string InvalidationChannel { get; init; } = "consent:invalidations";
    [Range(100, 10000)] public int ConnectTimeoutMs { get; init; } = 2000;
    [Range(100, 10000)] public int SyncTimeoutMs { get; init; } = 1000;

    /// <summary>
    /// SR-L-10: when the Redis revocation lookup fails (Redis unreachable or a lookup faults) and the token is not
    /// revoked locally, deny the request (HTTP 503) instead of accepting it. <c>null</c> (default) resolves to
    /// <c>true</c> outside Development and <c>false</c> in Development. Set <c>false</c> explicitly to prefer
    /// availability over security during Redis outages.
    /// </summary>
    public bool? FailClosedOnRevocationLookupError { get; init; }

    /// <summary>
    /// SR-L-10: with the fail-closed policy active, a token issued less than this many seconds ago is still accepted
    /// when the lookup fails (it cannot have been revoked on another node before the outage began). 0 disables the grace.
    /// </summary>
    [Range(0, 3600)] public int RevocationLookupGraceSeconds { get; init; } = 60;

    /// <summary>Resolves <see cref="FailClosedOnRevocationLookupError"/> for the given environment.</summary>
    public bool ResolveFailClosedOnRevocationLookupError(bool isDevelopment) => FailClosedOnRevocationLookupError ?? !isDevelopment;

    /// <summary>
    /// SEC H-01: Secret reference for the Redis password (mandatory outside Development if not in the connection string).
    /// </summary>
    public string? PasswordSecretRef { get; init; }
    /// <summary>
    /// SEC H-01: Secret reference from which the HMAC key for L2 consent cache entries is derived via HKDF
    /// (default: DataMasking.HmacSecretKeyVaultRef).
    /// </summary>
    public string? L2IntegrityKeyVaultRef { get; init; }

    /// <summary>
    /// SEC H-01: Use TLS for the Redis connection. Outside Development TLS is mandatory for non-loopback endpoints
    /// (start fails otherwise). <c>ssl=true</c> in the connection string has the same effect.
    /// </summary>
    public bool UseTls { get; init; } = false;

    /// <summary>
    /// SEC H-01: Expected host name in the Redis server certificate (SNI/validation), if it differs from the endpoint.
    /// </summary>
    public string? SslHost { get; init; }

    /// <summary>
    /// SEC H-01: Optional SHA-256 or SHA-1 thumbprints (hex) of accepted Redis server certificates (pinning).
    /// When set, only these certificates are accepted; empty = regular chain and host name validation.
    /// </summary>
    public List<string> AllowedServerCertificateThumbprints { get; init; } = [];
}

public sealed class EpochValidationOptions
{
    public bool FailClosedOnSensitiveTables { get; init; } = true;
    [Range(1, 300)] public int DegradedMaxStalenessSeconds { get; init; } = 30;
    public bool PipelinedMGetEnabled { get; init; } = true;
    /// <summary>
    /// WP-PF-07: how long a local epoch read may serve decisions of non-sensitive tables (epoch pub/sub pushes invalidations earlier).
    /// Always 0 for sensitive tables and delegated sources, regardless of this value. 0 disables the budget.
    /// </summary>
    [Range(0, 250)] public int LocalStalenessBudgetMilliseconds { get; init; } = 150;
    public int MaxLocalEpochEntries { get; init; } = 50_000;
}
