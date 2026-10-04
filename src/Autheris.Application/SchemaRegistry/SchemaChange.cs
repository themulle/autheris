namespace Autheris.Application.SchemaRegistry;

public sealed class SchemaChange
{
    public required SchemaChangeType ChangeType { get; init; }
    public required string Code { get; init; }
    public required string Path { get; init; }
    public required string Description { get; init; }
    public bool IsBreaking => ChangeType == SchemaChangeType.Breaking;

    public static SchemaChange Breaking(string code, string path, string description) => new()
    {
        ChangeType = SchemaChangeType.Breaking,
        Code = code,
        Path = path,
        Description = description
    };

    public static SchemaChange Dangerous(string code, string path, string description) => new()
    {
        ChangeType = SchemaChangeType.Dangerous,
        Code = code,
        Path = path,
        Description = description
    };

    public static SchemaChange Safe(string code, string path, string description) => new()
    {
        ChangeType = SchemaChangeType.Safe,
        Code = code,
        Path = path,
        Description = description
    };
}
