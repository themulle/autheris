using System.Text.Json;

namespace Autheris.Infrastructure.Persistence.Migrations.Shape;

/// <summary>
/// One operation of a step's shape delta (plan D-5). <c>Op</c> is one of <c>addTable</c>, <c>dropTable</c>, <c>addColumn</c>,
/// <c>dropColumn</c>, <c>alterColumn</c>, <c>setPrimaryKey</c>, <c>addIndex</c>, <c>dropIndex</c>.
/// </summary>
internal sealed record ShapeOp(
    string Op,
    string Table,
    string? Name = null,
    SchemaColumn? Column = null,
    IReadOnlyList<string>? Columns = null,
    bool Unique = false,
    SchemaTable? NewTable = null);

/// <summary>The shape change a migration step (version 2 and higher) makes, as data. Folded onto the frozen v0001 shape.</summary>
internal sealed record GovernanceSchemaShapeDelta(IReadOnlyList<ShapeOp> Ops)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = false };

    public static GovernanceSchemaShapeDelta Parse(string json, string description)
    {
        ArgumentNullException.ThrowIfNull(json);
        GovernanceSchemaShapeDelta? delta;
        try
        {
            delta = JsonSerializer.Deserialize<GovernanceSchemaShapeDelta>(json, Json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"The governance shape delta '{description}' cannot be parsed (fail closed).", ex);
        }

        if (delta?.Ops is null)
        {
            throw new InvalidOperationException($"The governance shape delta '{description}' is empty or incomplete (fail closed).");
        }

        return delta;
    }
}

/// <summary>Folds shape deltas onto a shape. Any inconsistency (unknown table or column, duplicate) fails closed.</summary>
internal static class GovernanceSchemaShapeFolder
{
    public static GovernanceSchemaShape Fold(GovernanceSchemaShape shape, GovernanceSchemaShapeDelta delta)
    {
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentNullException.ThrowIfNull(delta);
        var tables = shape.Tables.ToList();
        foreach (var op in delta.Ops)
        {
            ApplyOp(tables, op);
        }

        return shape with { Tables = tables };
    }

    private static void ApplyOp(List<SchemaTable> tables, ShapeOp op)
    {
        var at = tables.FindIndex(t => string.Equals(t.Name, op.Table, StringComparison.OrdinalIgnoreCase));
        switch (op.Op)
        {
            case "addTable":
                Require(at < 0, op, "the table already exists");
                tables.Add(op.NewTable ?? throw Bad(op, "newTable is missing"));
                return;
            case "dropTable":
                Require(at >= 0, op, "the table does not exist");
                tables.RemoveAt(at);
                return;
        }

        Require(at >= 0, op, "the table does not exist");
        var table = tables[at];
        switch (op.Op)
        {
            case "addColumn":
                var added = op.Column ?? throw Bad(op, "column is missing");
                Require(!table.Columns.Any(c => Same(c.Name, added.Name)), op, "the column already exists");
                tables[at] = table with { Columns = [.. table.Columns, added] };
                break;
            case "dropColumn":
                Require(table.Columns.Any(c => Same(c.Name, op.Name)), op, "the column does not exist");
                tables[at] = table with { Columns = table.Columns.Where(c => !Same(c.Name, op.Name)).ToArray() };
                break;
            case "alterColumn":
                var altered = op.Column ?? throw Bad(op, "column is missing");
                Require(table.Columns.Any(c => Same(c.Name, altered.Name)), op, "the column does not exist");
                tables[at] = table with { Columns = table.Columns.Select(c => Same(c.Name, altered.Name) ? altered : c).ToArray() };
                break;
            case "setPrimaryKey":
                tables[at] = table with { PrimaryKey = op.Columns ?? throw Bad(op, "columns are missing") };
                break;
            case "addIndex":
                Require(!table.Indexes.Any(i => Same(i.Name, op.Name)), op, "the index already exists");
                tables[at] = table with { Indexes = [.. table.Indexes, new SchemaIndex(op.Name ?? throw Bad(op, "name is missing"), op.Unique, op.Columns ?? throw Bad(op, "columns are missing"))] };
                break;
            case "dropIndex":
                Require(table.Indexes.Any(i => Same(i.Name, op.Name)), op, "the index does not exist");
                tables[at] = table with { Indexes = table.Indexes.Where(i => !Same(i.Name, op.Name)).ToArray() };
                break;
            default:
                throw Bad(op, "unknown operation");
        }
    }

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static void Require(bool condition, ShapeOp op, string reason)
    {
        if (!condition)
        {
            throw Bad(op, reason);
        }
    }

    private static InvalidOperationException Bad(ShapeOp op, string reason) =>
        new($"The governance shape delta operation '{op.Op}' on '{op.Table}' is invalid: {reason} (fail closed).");
}
