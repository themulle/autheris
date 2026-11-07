using System.ComponentModel.DataAnnotations;

namespace Autheris.Domain.Options;

public sealed class CachingOptions
{
    [Required] public L1MemoryCacheOptions L1MemoryCache { get; init; } = new();
    [Required] public RedisOptions Redis { get; init; } = new();
    [Required] public EpochValidationOptions EpochValidation { get; init; } = new();
}

public sealed class L1MemoryCacheOptions
{
    [Range(16, 4096)] public int SizeLimitMb { get; init; } = 512;
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
    /// SR-L-10 / DT-A-13: the revocation lookup always fails closed; a token issued less than this many seconds ago is still accepted
    /// when the lookup fails (it cannot have been revoked on another node before the outage began). 0 disables the grace.
    /// </summary>
    [Range(0, 3600)] public int RevocationLookupGraceSeconds { get; init; } = 60;

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
    /// how long a local epoch read may serve decisions of non-sensitive tables (epoch pub/sub pushes invalidations earlier).
    /// Always 0 for sensitive tables and delegated sources, regardless of this value. 0 disables the budget.
    /// </summary>
    [Range(0, 250)] public int LocalStalenessBudgetMilliseconds { get; init; } = 150;
    public int MaxLocalEpochEntries { get; init; } = 50_000;

    /// <summary>
    /// INF-01 R1: Grace period after Redis connection restoration during which Redis epoch reads are treated
    /// as non-authoritative to protect against missed pub/sub invalidations before the revoking peer flushes.
    /// </summary>
    public TimeSpan ReconnectEpochGrace { get; init; } = TimeSpan.FromSeconds(5);
}
