namespace Autheris.Application.Sql.Policy.RowFilterKeySets;

/// <summary>
/// Cached key set of one correlated filter. <see cref="Tenant"/> and <see cref="DataSource"/> are verified on every hit (SEC-RF107-02).
/// <see cref="Keys"/> are the converted key values (<see cref="long"/>, <see cref="decimal"/> or <see cref="string"/>, see
/// <see cref="KeySetValueConverter"/>); <see cref="LoadedAt"/> is taken before the key query starts (SEC-RF107-04 b).
/// <see cref="Overflow"/> is a sentinel (more than <c>MaxKeys</c> keys, or an entry too large to cache as keys); <see cref="NotBindable"/> is a
/// sentinel for a loaded set with a value that does not fit the foreign key type (SEC-RF107-07 b). Both carry no keys and keep the site
/// <c>EXISTS</c> for the duration without another key query. <see cref="ToString"/> prints counts only (SEC-RF107-09).
/// </summary>
public sealed record RowFilterKeySetEntry(
    IReadOnlyList<object> Keys,
    bool Overflow,
    DateTimeOffset LoadedAt,
    string Tenant,
    string DataSource,
    bool NotBindable = false)
{
    // SEC-RF107-13 (a): 64 character hex cache key (2 x 64 + 40), object headers of the entry, its array and the timestamp.
    private const long FixedOverhead = (2 * 64 + 40) + 128;
    private const long StringHeader = 40;
    private const long BoxedNumeric = 24;
    private const long ReferenceSlot = 8;

    public static RowFilterKeySetEntry OverflowSentinel(DateTimeOffset loadedAt, string tenant, string dataSource)
        => new([], true, loadedAt, tenant, dataSource);

    public static RowFilterKeySetEntry NotBindableSentinel(DateTimeOffset loadedAt, string tenant, string dataSource)
        => new([], false, loadedAt, tenant, dataSource, NotBindable: true);

    /// <summary>
    /// Estimated bytes (SEC-RF107-13 a): per key a reference slot plus a string (2 x length + 40) or a boxed numeric (24), plus tenant, data source,
    /// the cache key and fixed overhead. An estimate for the cache size limit, not an exact measurement.
    /// </summary>
    public long EstimateBytes()
    {
        long bytes = FixedOverhead + (2L * Tenant.Length + StringHeader) + (2L * DataSource.Length + StringHeader);
        foreach (object key in Keys)
        {
            bytes += ReferenceSlot + (key is string text ? 2L * text.Length + StringHeader : BoxedNumeric);
        }

        return bytes;
    }

    public override string ToString() => $"RowFilterKeySetEntry {{ Keys = {Keys.Count}, Overflow = {Overflow}, NotBindable = {NotBindable}, LoadedAt = {LoadedAt:O} }}";
}
