namespace Autheris.Application.Sql.Policy.RowFilterKeySets;

using TrinoSqlEngine.Governance;

/// <summary>
/// Replacement of one correlated filter site by an <c>IN</c> list of its cached keys. <see cref="Keys"/> are the loaded, already converted
/// key values (never null); <see cref="PaddedCount"/> is the list length (<see cref="KeySetPadding.Bucket"/>, the last key is repeated);
/// <see cref="SiteOrdinal"/> is unique per statement and names the parameters <c>__rls_ks&lt;ordinal&gt;_&lt;index&gt;</c>.
/// SEC-RF107-09: <see cref="ToString"/> prints counts only, never key values.
/// </summary>
public sealed record KeySetReplacement(string ForeignKeyColumn, IReadOnlyList<PolicyValue> Keys, int PaddedCount, int SiteOrdinal)
{
    public override string ToString() => $"KeySetReplacement {{ Keys = {Keys.Count}, PaddedCount = {PaddedCount}, SiteOrdinal = {SiteOrdinal} }}";
}
