namespace Autheris.Infrastructure.Persistence;

using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

public partial class SqliteGovernanceRepository : ITrustedDocumentRepository
{
    public Task<long> GetTrustedDocumentGenerationAsync(CancellationToken ct = default) =>
        ReadAsync(conn => TrustedDocumentStore.GetGenerationAsync(conn, ct), ct);

    public async Task<TrustedDocumentStoreSnapshot> LoadTrustedDocumentSnapshotAsync(CancellationToken ct = default)
    {
        await using var scope = await ReadScopeAsync(ct, snapshot: true).ConfigureAwait(false);
        return await TrustedDocumentStore.LoadSnapshotAsync(scope.Connection, ct, scope.Snapshot).ConfigureAwait(false);
    }

    public Task<TrustedDocumentAppView?> GetTrustedDocumentAppAsync(string appId, CancellationToken ct = default) =>
        ReadAsync(conn => TrustedDocumentStore.GetAppAsync(conn, appId, ct), ct);

    public Task<TrustedDocumentSetDelta> ReplaceTrustedDocumentVersionAsync(
        string appId, string version, IReadOnlyList<string> origins, IReadOnlyList<string> hashes, int maxActiveVersions, DateTimeOffset at, CancellationToken ct = default) =>
        WriteAsync(conn => TrustedDocumentStore.ReplaceVersionAsync(conn, appId, version, origins, hashes, maxActiveVersions, at, ct), ct);

    public Task<TrustedDocumentRetireOutcome> RetireTrustedDocumentVersionAsync(string appId, string version, DateTimeOffset at, CancellationToken ct = default) =>
        WriteAsync(conn => TrustedDocumentStore.RetireVersionAsync(conn, appId, version, at, ct), ct);
}
