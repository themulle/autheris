using Autheris.Modules.OpenMetadata;

namespace Autheris.Extensions.Tests.TestSupport;

/// <summary>
/// Configurable in-memory <see cref="IOpenMetadataClient"/> for OpenMetadata extension tests.
/// </summary>
internal sealed class StubOpenMetadataClient : IOpenMetadataClient
{
    public IReadOnlyList<OpenMetadataTable> Tables { get; set; } = [];
    public IReadOnlyList<OpenMetadataPolicy> Policies { get; set; } = [];
    public IReadOnlyList<OpenMetadataRole> Roles { get; set; } = [];
    public IReadOnlyList<OpenMetadataTeam> Teams { get; set; } = [];
    public IReadOnlyList<OpenMetadataUser> Users { get; set; } = [];
    public Func<string, OpenMetadataTable?>? TableByFqnResolver { get; set; }
    public Func<Task<IReadOnlyList<OpenMetadataUser>>>? OnGetUsers { get; set; }

    public Task<IReadOnlyList<OpenMetadataTable>> GetTablesAsync(string? service = null, CancellationToken ct = default)
        => Task.FromResult(Tables);

    public Task<OpenMetadataTable?> GetTableByFqnAsync(string fqn, CancellationToken ct = default)
        => Task.FromResult(TableByFqnResolver is not null
            ? TableByFqnResolver(fqn)
            : Tables.FirstOrDefault(t => string.Equals(t.FullyQualifiedName, fqn, StringComparison.OrdinalIgnoreCase)));

    public Task<IReadOnlyList<OpenMetadataPolicy>> GetPoliciesAsync(CancellationToken ct = default)
        => Task.FromResult(Policies);

    public Task<IReadOnlyList<OpenMetadataRole>> GetRolesAsync(CancellationToken ct = default)
        => Task.FromResult(Roles);

    public Task<IReadOnlyList<OpenMetadataTeam>> GetTeamsAsync(CancellationToken ct = default)
        => Task.FromResult(Teams);

    public Task<IReadOnlyList<OpenMetadataUser>> GetUsersAsync(CancellationToken ct = default)
        => OnGetUsers is not null ? OnGetUsers() : Task.FromResult(Users);
}
