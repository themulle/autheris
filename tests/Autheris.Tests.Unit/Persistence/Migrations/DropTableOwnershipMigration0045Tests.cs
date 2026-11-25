namespace Autheris.Tests.Unit.Persistence.Migrations;

using System;
using System.Linq;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Dialects;
using Autheris.Infrastructure.Persistence.Migrations;
using Autheris.Infrastructure.Persistence.Migrations.Shape;
using Shouldly;
using Xunit;

/// <summary>
/// #1009 W3-12b (D13, SEC-1009-48): step 0045 drops the per-table ownership model. Owners are not converted, owner grants are revoked
/// and the epochs are bumped; nothing in the code base still names the dropped tables. The executed behaviour on both engines is covered
/// by the integration test <c>DropTableOwnershipMigrationStep0045Tests</c>.
/// </summary>
[Trait("Category", "Security")]
public sealed class DropTableOwnershipMigration0045Tests
{
    private static readonly string[] DroppedTables = ["DATA_OWNER_DELEGATIONS", "TABLE_OWNERS", "DATA_OWNERS"];

    [Theory]
    [InlineData("postgresql")]
    [InlineData("sqlserver")]
    public void Step0045_DropsTheOwnershipTables_RevokesOwnerGrants_AndUpdatesShape(string engine)
    {
        var steps = SqlMigrationStep.LoadEmbedded(engine);
        var step = steps.Single(s => s.Version == 45 && s.Name == "drop_table_ownership");
        step.Phase3Marker.ShouldBe("MIG-0045");
        StepSqlLint.Validate(step.Sql, step.GuardSql, step.Phase3Marker).ShouldBeEmpty();

        foreach (var table in DroppedTables)
        {
            step.Sql.ShouldContain("DROP TABLE", Case.Insensitive);
            step.Sql.ShouldContain(table, Case.Insensitive);
        }

        // Delegations reference owners: they go first, the owners last.
        step.Sql.IndexOf("DATA_OWNER_DELEGATIONS", StringComparison.OrdinalIgnoreCase)
            .ShouldBeLessThan(step.Sql.IndexOf("DATA_OWNERS", StringComparison.OrdinalIgnoreCase));

        var revoke = step.Batches.SelectMany(b => b.Split(';')).Single(s => s.Contains("UPDATE", StringComparison.OrdinalIgnoreCase) && s.Contains("CONSENTS", StringComparison.OrdinalIgnoreCase));
        revoke.ShouldContain("is_revoked = 1");
        revoke.ShouldContain("'OWNER'");
        revoke.ShouldContain("is_revoked = 0");
        step.Sql.ShouldContain("POLICY_EPOCHS SET epoch = epoch + 1", Case.Insensitive);
        step.Sql.ShouldContain("GOVERNANCE_EPOCHS SET epoch = epoch + 1", Case.Insensitive);

        var shape = GovernanceSchemaShapeLoader.LoadAt(engine, 45, steps.ToDictionary(s => s.Version, s => s.ShapeDelta));
        foreach (var table in DroppedTables)
        {
            shape.Tables.ShouldNotContain(t => string.Equals(t.Name, table, StringComparison.OrdinalIgnoreCase));
        }

        shape.Tables.ShouldContain(t => string.Equals(t.Name, "governance_domains", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DroppedTables_AreNoLongerGovernanceTables_GlobalOrRequiredAtStartup()
    {
        var names = Enum.GetValues<GovernanceTable>().Select(t => t.ToSqlName()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var dropped in DroppedTables)
        {
            names.ShouldNotContain(dropped);
            GovernanceGlobalTables.IsGlobal(dropped).ShouldBeFalse();
            GovernanceSchemaValidator.RequiredWave2Tables.ShouldNotContain(dropped);
        }
    }
}
