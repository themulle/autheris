namespace Autheris.Application.SchemaRegistry;

public sealed class SchemaDiffResult
{
    public required IReadOnlyList<SchemaChange> Changes { get; init; }
    public bool HasBreakingChanges => Changes.Any(c => c.IsBreaking);
    public bool IsCompatible => !HasBreakingChanges;
    public int BreakingCount => Changes.Count(c => c.ChangeType == SchemaChangeType.Breaking);
    public int DangerousCount => Changes.Count(c => c.ChangeType == SchemaChangeType.Dangerous);
    public int SafeCount => Changes.Count(c => c.ChangeType == SchemaChangeType.Safe);

    public static SchemaDiffResult Empty => new() { Changes = Array.Empty<SchemaChange>() };

    public static SchemaDiffResult FromChanges(IEnumerable<SchemaChange> changes) => new()
    {
        Changes = changes.ToList()
    };
}
