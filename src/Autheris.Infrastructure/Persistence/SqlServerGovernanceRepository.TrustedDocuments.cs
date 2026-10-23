namespace Autheris.Infrastructure.Persistence;

using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

public partial class SqlServerGovernanceRepository : ITrustedDocumentRepository
{
    public async Task<long> GetTrustedDocumentGenerationAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await TrustedDocumentStore.GetGenerationAsync(conn, ct).ConfigureAwait(false);
    }

    public async Task<TrustedDocumentStoreSnapshot> LoadTrustedDocumentSnapshotAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await TrustedDocumentStore.LoadSnapshotAsync(conn, ct).ConfigureAwait(false);
    }

    public async Task<TrustedDocumentAppView?> GetTrustedDocumentAppAsync(string appId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await TrustedDocumentStore.GetAppAsync(conn, appId, ct).ConfigureAwait(false);
    }

    public async Task<TrustedDocumentSetDelta> ReplaceTrustedDocumentVersionAsync(
        string appId, string version, IReadOnlyList<string> origins, IReadOnlyList<string> hashes, int maxActiveVersions, DateTimeOffset at, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await TrustedDocumentStore.ReplaceVersionAsync(conn, appId, version, origins, hashes, maxActiveVersions, at, ct).ConfigureAwait(false);
    }

    public async Task<TrustedDocumentRetireOutcome> RetireTrustedDocumentVersionAsync(string appId, string version, DateTimeOffset at, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await TrustedDocumentStore.RetireVersionAsync(conn, appId, version, at, ct).ConfigureAwait(false);
    }
}
