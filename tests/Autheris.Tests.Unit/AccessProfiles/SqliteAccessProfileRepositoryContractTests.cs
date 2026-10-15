using Autheris.Domain.Interfaces;
using Autheris.Infrastructure.Cache;
using Autheris.Infrastructure.Persistence;
using Autheris.Tests.AccessProfiles;

namespace Autheris.Tests.Unit.AccessProfiles;

/// <summary>WP-SC-03 contract on SQLite.</summary>
[Trait("Category", "Security")]
public sealed class SqliteAccessProfileRepositoryContractTests : AccessProfileRepositoryContractBase
{
    private SqliteGovernanceRepository? _repo;

    protected override Task<IAccessProfileRepository?> CreateRepositoryAsync()
    {
        _repo = GovernanceStoreFactory.CreateSqlite(new EpochValidationService());
        return Task.FromResult<IAccessProfileRepository?>(_repo);
    }

    protected override Task DisposeRepositoryAsync()
    {
        _repo?.Dispose();
        return Task.CompletedTask;
    }
}
