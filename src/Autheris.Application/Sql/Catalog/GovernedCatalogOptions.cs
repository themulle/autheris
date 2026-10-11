namespace Autheris.Application.Sql.Catalog;

/// <summary>Settings of the metadata-backed catalog (WP-X0c).</summary>
public sealed class GovernedCatalogOptions
{
    /// <summary>Hard upper bound of <see cref="CacheTtlSeconds"/> (SEC-X1-09). Configuration can only lower the TTL.</summary>
    public const int MaxCacheTtlSeconds = 300;

    /// <summary>How long a loaded table entry is served before the database metadata is read again.</summary>
    public int CacheTtlSeconds { get; init; } = MaxCacheTtlSeconds;

    /// <summary>Throws when the TTL is not within 1..300 seconds. Called at construction of the provider, so the host fails at startup.</summary>
    public void Validate()
    {
        if (CacheTtlSeconds is < 1 or > MaxCacheTtlSeconds)
        {
            throw new ArgumentOutOfRangeException(nameof(CacheTtlSeconds), CacheTtlSeconds,
                $"The catalog cache TTL must be between 1 and {MaxCacheTtlSeconds} seconds.");
        }
    }
}
