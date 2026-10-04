namespace Autheris.Application.SchemaRegistry;

public sealed class RegisteredSchema
{
    public required string ServiceName { get; init; }
    public required string Version { get; init; }
    public required string Sdl { get; init; }
    public DateTimeOffset RegisteredAt { get; init; } = DateTimeOffset.UtcNow;
    public string? GitCommit { get; init; }
    public string? GitBranch { get; init; }
    public string? RegisteredBy { get; init; }
    public bool IsActive { get; set; } = true;
}
