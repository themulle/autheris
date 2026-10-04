namespace Autheris.Application.SchemaRegistry;

public sealed class SchemaRegistrationRequest
{
    public required string ServiceName { get; init; }
    public required string Sdl { get; init; }
    public string? Version { get; init; }
    public string? GitCommit { get; init; }
    public string? GitBranch { get; init; }
    public string? RegisteredBy { get; init; }
    public bool DryRun { get; init; }
    public bool ForceIfBreaking { get; init; }
}

public sealed class SchemaRegistrationResponse
{
    public required bool Success { get; init; }
    public required string Message { get; init; }
    public RegisteredSchema? Schema { get; init; }
    public required SchemaDiffResult Diff { get; init; }
}
