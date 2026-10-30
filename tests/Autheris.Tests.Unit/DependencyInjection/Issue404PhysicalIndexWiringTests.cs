namespace Autheris.Tests.Unit.DependencyInjection;

using Autheris.Application.Interfaces;
using Autheris.Extensions.DataCatalog;
using Autheris.Infrastructure.Persistence;
using Shouldly;
using Xunit;

/// <summary>
/// Issue #404 Phase 5 tripwire: a null <see cref="IPhysicalIndexRepository"/> would silently disable the physical index sync. Every governance
/// repository implements the port, the composition snapshots register it, and the sync service takes it as a constructor parameter that the
/// container fills whenever it is registered.
/// </summary>
public sealed class Issue404PhysicalIndexWiringTests
{
    [Fact]
    public void AllThreeRepositories_ImplementThePort()
    {
        typeof(IPhysicalIndexRepository).IsAssignableFrom(typeof(SqliteGovernanceRepository)).ShouldBeTrue();
        typeof(IPhysicalIndexRepository).IsAssignableFrom(typeof(PostgreSqlGovernanceRepository)).ShouldBeTrue();
        typeof(IPhysicalIndexRepository).IsAssignableFrom(typeof(SqlServerGovernanceRepository)).ShouldBeTrue();
    }

    [Fact]
    public void SyncService_TakesThePort_AndTheCompositionSnapshotsRegisterIt()
    {
        typeof(DataCatalogSyncService).GetConstructors().Single().GetParameters()
            .ShouldContain(p => p.ParameterType == typeof(IPhysicalIndexRepository));

        foreach (var profile in new[] { "dev-sqlite", "production-like" })
        {
            var path = Autheris.Tests.Unit.Common.SnapshotFile.SnapshotPath($"composition-{profile}.txt");
            File.ReadAllText(path).ShouldContain("Autheris.Application.Interfaces.IPhysicalIndexRepository Singleton", Case.Sensitive);
        }
    }
}
