namespace Autheris.Domain.Options.TableCache;

using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Autheris.Domain.Caching.TableCache;

/// <summary>
/// Gateway options schema for Governed Table Cache (Section: "Gateway:TableCache").
/// </summary>
public sealed class TableCacheOptions
{
    public const string SectionName = "Gateway:TableCache";

    public bool Enabled { get; init; } = false;

    public TableCacheStorageTier DefaultStorageTier { get; init; } = TableCacheStorageTier.DuckDbDisk;

    [Required]
    public string PersistentDirectory { get; init; } = "/var/lib/autheris/cache";

    public int DefaultMemoryLimitMb { get; init; } = 2048;

    public int DefaultDiskLimitMb { get; init; } = 10240;

    public int DefaultTtlSeconds { get; init; } = 3600;


    public int MaxStaleSurvivalSeconds { get; init; } = 86400; // 24 hours default, hard safety ceiling 7 days

    public TableCacheEncryptionOptions Encryption { get; init; } = new();

    public List<TableCacheTargetOptions> Tables { get; init; } = [];
}
