namespace Autheris.Tests.SqlCompilerGate;

using Autheris.Tests.SqlCompilerGate.Classification;
using Autheris.Tests.SqlCompilerGate.Model;
using Shouldly;

/// <summary>The independent policy oracle (plan 16.5 M-2) on synthetic results: it knows the fixture, not any engine.</summary>
public sealed class PolicyOracleTests
{
    [Fact]
    public void Oracle_FlagsForeignTenantMarker()
    {
        var oracle = new PolicyOracle(PolicyScenario.Tenant);

        oracle.CheckRows([[GateModel.MarkerOf("orders", GateModel.TenantB, 1001)]])
            .ShouldHaveSingleItem().Kind.ShouldBe(OracleViolationKind.ForeignTenantMarker);
        oracle.CheckRows([["prefix " + GateModel.MarkerOf("users", GateModel.TenantB, 1002) + " suffix"]])
            .ShouldHaveSingleItem().Kind.ShouldBe(OracleViolationKind.ForeignTenantMarker);

        // a foreign row id in a plain SELECT id
        oracle.CheckRows([[1001L]]).ShouldHaveSingleItem().Kind.ShouldBe(OracleViolationKind.ForeignTenantMarker);
        oracle.CheckRows([[1001]]).ShouldHaveSingleItem().Kind.ShouldBe(OracleViolationKind.ForeignTenantMarker);
        oracle.CheckRows([[1001.00m]]).ShouldHaveSingleItem().Kind.ShouldBe(OracleViolationKind.ForeignTenantMarker);
    }

    [Fact]
    public void Oracle_AcceptsTheRowsOfTheOwnTenant()
    {
        var oracle = new PolicyOracle(PolicyScenario.Tenant);

        oracle.CheckRows([[1L, GateModel.MarkerOf("orders", GateModel.TenantA, 1), 10.5m, null, 2001L]]).ShouldBeEmpty();
    }

    [Fact]
    public void Oracle_FlagsAConsentExcludedRow_OnlyUnderTheConsentScenario()
    {
        var hidden = new object?[] { 2001L, GateModel.MarkerOf("orders", GateModel.TenantA, 2001) };

        new PolicyOracle(PolicyScenario.Tenant).CheckRows([hidden]).ShouldBeEmpty();
        new PolicyOracle(PolicyScenario.TenantConsentMask).CheckRows([hidden])
            .Select(v => v.Kind).Distinct().ShouldBe([OracleViolationKind.ConsentExcludedMarker]);
    }

    [Fact]
    public void Oracle_FlagsAClearMaskedValue_OfEveryRow()
    {
        var oracle = new PolicyOracle(PolicyScenario.TenantConsentMask);

        oracle.CheckRows([[GateModel.EmailOf("orders", GateModel.TenantA, 1)]])
            .ShouldHaveSingleItem().Kind.ShouldBe(OracleViolationKind.ClearMaskedValue);
        oracle.CheckRows([[GateModel.MaskValue]]).ShouldBeEmpty();
        new PolicyOracle(PolicyScenario.Tenant).CheckRows([[GateModel.EmailOf("orders", GateModel.TenantA, 1)]]).ShouldBeEmpty();
    }

    [Fact]
    public void Oracle_FlagsAWriteToAnInvisibleRow_AndAWriteOfAnotherTenant()
    {
        var oracle = new PolicyOracle(PolicyScenario.TenantConsentMask);
        object?[] Order(string tenant, long id, string region) =>
            [id, tenant, 1L, 10m, "open", region, "sales", new DateTime(2024, 1, 1), GateModel.EmailOf("orders", tenant, id), GateModel.MarkerOf("orders", tenant, id)];

        // changing the own, visible row is fine
        oracle.CheckWrite([new Harness.RowChange('-', "orders", Order("A", 1, "EU")), new Harness.RowChange('+', "orders", Order("A", 1, "EU"))]).ShouldBeEmpty();
        // a hidden (consent-excluded) row and a foreign row are not
        oracle.CheckWrite([new Harness.RowChange('-', "orders", Order("A", 2001, "US"))])
            .ShouldHaveSingleItem().Kind.ShouldBe(OracleViolationKind.InvisibleRowChanged);
        oracle.CheckWrite([new Harness.RowChange('-', "orders", Order("B", 1001, "EU"))])
            .ShouldHaveSingleItem().Kind.ShouldBe(OracleViolationKind.InvisibleRowChanged);
        // an inserted row that carries another tenant
        oracle.CheckWrite([new Harness.RowChange('+', "orders", Order("B", 9002, "EU"))])
            .ShouldHaveSingleItem().Kind.ShouldBe(OracleViolationKind.InvisibleRowChanged);
    }

    [Fact]
    public void FixtureMarkers_AreUnique_AndBothTenantsExistInEveryTable()
    {
        foreach (var table in GateModel.Tables)
        {
            var rows = GateModel.Rows.Where(r => r.Table == table.Name).ToList();
            rows.Select(r => r.Id).Distinct().Count().ShouldBe(rows.Count);
            rows.Select(r => r.Tenant).Distinct().Order().ShouldBe([GateModel.TenantA, GateModel.TenantB]);
        }

        GateModel.Rows.Where(r => r.Tenant == GateModel.TenantB).ShouldAllBe(r => r.Id >= 1000);
    }
}
