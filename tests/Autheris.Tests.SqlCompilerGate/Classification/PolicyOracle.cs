namespace Autheris.Tests.SqlCompilerGate.Classification;

using Autheris.Tests.SqlCompilerGate.Harness;
using Autheris.Tests.SqlCompilerGate.Model;

public enum OracleViolationKind
{
    /// <summary>A cell carries the marker or id of a row of another tenant.</summary>
    ForeignTenantMarker,

    /// <summary>A cell carries the marker or id of a row of the own tenant that the consent filter hides.</summary>
    ConsentExcludedMarker,

    /// <summary>A cell carries the clear value of a masked column.</summary>
    ClearMaskedValue,

    /// <summary>A write changed a row that is not visible to the principal.</summary>
    InvisibleRowChanged
}

public sealed record OracleViolation(OracleViolationKind Kind, string Table);

/// <summary>
/// The independent policy oracle (plan 16.5 M-2). A reference evaluator of the fixture's tenant, consent and mask policies that
/// does not depend on any engine: it knows which fixture rows are visible to tenant A, the unique markers and ids of the rows that
/// are not, and the clear values of the masked columns. A result row of any engine is checked against it.
/// </summary>
public sealed class PolicyOracle
{
    private readonly PolicyScenario _scenario;
    private readonly Dictionary<string, (OracleViolationKind Kind, string Table)> _markers = new(StringComparer.Ordinal);
    private readonly Dictionary<long, (OracleViolationKind Kind, string Table)> _ids = [];
    private readonly Dictionary<string, string> _clearMasked = new(StringComparer.Ordinal);

    public PolicyOracle(PolicyScenario scenario)
    {
        _scenario = scenario;
        foreach (var row in GateModel.Rows)
        {
            var table = GateModel.Table(row.Table);
            if (!row.VisibleTo(scenario))
            {
                var kind = row.Tenant == GateModel.TenantA ? OracleViolationKind.ConsentExcludedMarker : OracleViolationKind.ForeignTenantMarker;
                _markers[GateModel.MarkerOf(row.Table, row.Tenant, row.Id)] = (kind, row.Table);
                _markers[GateModel.EmailOf(row.Table, row.Tenant, row.Id)] = (kind, row.Table);
                if (row.Id >= 1000) _ids[row.Id] = (kind, row.Table);
            }

            if (scenario == PolicyScenario.TenantConsentMask)
            {
                for (int i = 0; i < table.Columns.Count; i++)
                {
                    if (table.Columns[i].Masked && row.Values[i] is string clear) _clearMasked[clear] = row.Table;
                }
            }
        }
    }

    /// <summary>Checks the cells of a query result. The returned violations are the offenses of the engine, not differences.</summary>
    public IReadOnlyList<OracleViolation> CheckRows(IEnumerable<object?[]> rows)
    {
        var found = new List<OracleViolation>();
        foreach (var row in rows)
        {
            foreach (var cell in row)
            {
                Check(cell, found);
            }
        }

        return found;
    }

    /// <summary>Checks the changes of a write: no row that is invisible to the principal may change (G5-g), no clear masked value may be written.</summary>
    public IReadOnlyList<OracleViolation> CheckWrite(IEnumerable<RowChange> changes)
    {
        var found = new List<OracleViolation>();
        foreach (var change in changes)
        {
            var table = GateModel.Table(change.Table);
            int tenantIndex = table.IndexOf(GateModel.TenantColumn);
            string? tenant = change.Cells[tenantIndex] as string;
            long id = Convert.ToInt64(change.Cells[table.IndexOf("id")], System.Globalization.CultureInfo.InvariantCulture);
            if (change.Sign == '-')
            {
                // a before image of a row that is not visible: the engine changed or deleted it
                var original = GateModel.Rows.FirstOrDefault(r => r.Table == change.Table && r.Id == id);
                if (original is not null && !original.VisibleTo(_scenario)) found.Add(new OracleViolation(OracleViolationKind.InvisibleRowChanged, change.Table));
            }
            else if (!string.Equals(tenant, GateModel.TenantA, StringComparison.Ordinal))
            {
                // an after image that carries another tenant: a written row left the principal's tenant
                found.Add(new OracleViolation(OracleViolationKind.InvisibleRowChanged, change.Table));
            }
        }

        return found;
    }

    /// <summary>How many cells of <paramref name="rows"/> carry a clear masked value (used to compare two engines).</summary>
    public int CountClearMasked(IEnumerable<object?[]> rows) =>
        CheckRows(rows).Count(v => v.Kind == OracleViolationKind.ClearMaskedValue);

    private void Check(object? cell, List<OracleViolation> found)
    {
        switch (cell)
        {
            case string text:
                foreach (var (marker, info) in _markers)
                {
                    if (text.Contains(marker, StringComparison.Ordinal)) found.Add(new OracleViolation(info.Kind, info.Table));
                }

                foreach (var (clear, table) in _clearMasked)
                {
                    if (text.Contains(clear, StringComparison.Ordinal)) found.Add(new OracleViolation(OracleViolationKind.ClearMaskedValue, table));
                }

                break;
            case sbyte or byte or short or ushort or int or uint or long or ulong or decimal:
                long id = (long)Convert.ToDecimal(cell, System.Globalization.CultureInfo.InvariantCulture);
                if (Convert.ToDecimal(cell, System.Globalization.CultureInfo.InvariantCulture) == id && _ids.TryGetValue(id, out var info2))
                {
                    found.Add(new OracleViolation(info2.Kind, info2.Table));
                }

                break;
        }
    }
}
