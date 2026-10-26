namespace Autheris.Domain.Options.TableCache;

/// <summary>
/// Cryptographic configuration for cache storage at rest.
/// </summary>
public sealed class TableCacheEncryptionOptions
{
    public bool Enabled { get; init; } = true;

    public string KeyVaultKeyName { get; init; } = "table-cache-dek";

}
