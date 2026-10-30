using Autheris.Application.Dbt.Interfaces;
using Autheris.Domain.Common;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Model;

namespace Autheris.Extensions.Tests;

/// <summary>Test seam: a tenant-less dbt sync is a cluster-admin import for the configured default tenant (DT-A-9); tests have none configured, so they pass the caller explicitly.</summary>
internal static class DbtSyncTestExtensions
{
    public static Task<DbtSyncResult> IngestManifestStreamAsClusterAdminAsync(this IDbtMetadataIngestionService service, Stream manifestStream, bool dryRun = false, CancellationToken ct = default)
        => service.IngestManifestStreamAsync(
            manifestStream,
            new DbtGovernanceImportCaller(new VirtualFilterActor(new Sid("system:dbt-sync"), IsSync: true, null), Autheris.Tests.Common.TestTenants.Default, true),
            dryRun,
            ct);
}
