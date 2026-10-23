using Autheris.Infrastructure.Cache;
using Autheris.Infrastructure.Persistence;
using Autheris.Tests.Governance;

namespace Autheris.Tests.Unit.Governance;

/// <summary>PERS-16 / PERS-07 / PERS-09 / GOV-02 store contract on SQLite.</summary>
[Trait("Category", "Security")]
public sealed class SqliteGovernanceStoresContractTests : GovernanceStoresContractBase
{
    private SqliteGovernanceRepository? _repo;

    protected override Task<object?> CreateRepositoryAsync()
    {
        _repo = GovernanceStoreFactory.CreateSqlite(new EpochValidationService());
        return Task.FromResult<object?>(_repo);
    }

    protected override Task DisposeRepositoryAsync()
    {
        _repo?.Dispose();
        return Task.CompletedTask;
    }
}
