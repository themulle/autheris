namespace Autheris.Tests.Unit.Persistence.Migrations;

using System;
using System.Linq;
using Autheris.Infrastructure.Persistence.Migrations;
using Autheris.Infrastructure.Persistence.Migrations.Shape;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class DropRecertificationMigration0037Tests
{
    [Theory]
    [InlineData("postgresql")]
    [InlineData("sqlserver")]
    public void Step0037_DropsRecertificationTables_AndUpdatesShape(string engine)
    {
        var steps = SqlMigrationStep.LoadEmbedded(engine);
        var step = steps.Single(s => s.Version == 37 && s.Name == "drop_recertification");
        step.Phase3Marker.ShouldBe("MIG-0037");
        StepSqlLint.Validate(step.Sql, step.GuardSql, step.Phase3Marker).ShouldBeEmpty();

        step.Sql.ShouldContain("recert_items", Case.Insensitive);
        step.Sql.ShouldContain("recert_campaigns", Case.Insensitive);
        var itemsAt = step.Sql.IndexOf("recert_items", StringComparison.OrdinalIgnoreCase);
        var campaignsAt = step.Sql.IndexOf("recert_campaigns", StringComparison.OrdinalIgnoreCase);
        itemsAt.ShouldBeLessThan(campaignsAt);

        var shape = GovernanceSchemaShapeLoader.LoadAt(engine, 37, steps.ToDictionary(s => s.Version, s => s.ShapeDelta));
        shape.Tables.ShouldNotContain(t => string.Equals(t.Name, "recert_items", StringComparison.OrdinalIgnoreCase));
        shape.Tables.ShouldNotContain(t => string.Equals(t.Name, "recert_campaigns", StringComparison.OrdinalIgnoreCase));
        shape.Tables.ShouldContain(t => string.Equals(t.Name, "functional_right_mappings", StringComparison.OrdinalIgnoreCase));
    }
}
