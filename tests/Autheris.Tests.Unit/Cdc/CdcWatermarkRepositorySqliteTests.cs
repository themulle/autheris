namespace Autheris.Tests.Unit.Cdc;

using System;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Autheris.Tests.Common;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>Durable, fenced CDC watermark persistence on the SQLite governance store (WP-ARC-A-11).</summary>
public sealed class CdcWatermarkRepositorySqliteTests : IDisposable
{
    private const string Source = "mssql_ct";
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private SqliteGovernanceRepository NewRepository()
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Development");
        var options = Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions
            {
                ConnectionString = $"Data Source={System.IO.Path.Combine(_dir.Path, "gov.db")}",
                SeedDemoData = false,
                EnableOutboxProcessor = false
            }
        });
        return GovernanceStoreFactory.CreateSqlite(Substitute.For<IEpochValidationService>(), options, env);
    }

    [Fact]
    public async Task Absent_ReturnsNull()
    {
        using var repository = NewRepository();

        (await repository.GetAsync(Source, "d", "s", "t", default)).ShouldBeNull();
    }

    [Fact]
    public async Task Watermark_SurvivesStoreRecreation()
    {
        using (var first = NewRepository())
        {
            (await first.TryAdvanceAsync(Source, "d", "s", "t", 42, 1, default)).ShouldBeTrue();
        }

        using var second = NewRepository();

        (await second.GetAsync(Source, "d", "s", "t", default)).ShouldBe(42);
    }

    [Fact]
    public async Task Advance_IsMonotonic()
    {
        using var repository = NewRepository();

        await repository.TryAdvanceAsync(Source, "d", "s", "t", 100, 1, default);
        (await repository.TryAdvanceAsync(Source, "d", "s", "t", 50, 1, default)).ShouldBeTrue();

        (await repository.GetAsync(Source, "d", "s", "t", default)).ShouldBe(100);
    }

    [Fact]
    public async Task StaleFencingToken_IsRejected_AndWatermarkUnchanged()
    {
        using var repository = NewRepository();
        await repository.TryAdvanceAsync(Source, "d", "s", "t", 10, 5, default);

        var accepted = await repository.TryAdvanceAsync(Source, "d", "s", "t", 99, 4, default);

        accepted.ShouldBeFalse();
        (await repository.GetAsync(Source, "d", "s", "t", default)).ShouldBe(10);
    }

    [Fact]
    public async Task NewerFencingToken_TakesOver_AndFencesTheOldOwner()
    {
        using var repository = NewRepository();
        await repository.TryAdvanceAsync(Source, "d", "s", "t", 10, 5, default);

        (await repository.TryAdvanceAsync(Source, "d", "s", "t", 20, 6, default)).ShouldBeTrue();
        (await repository.TryAdvanceAsync(Source, "d", "s", "t", 30, 5, default)).ShouldBeFalse();

        (await repository.GetAsync(Source, "d", "s", "t", default)).ShouldBe(20);
    }

    [Fact]
    public async Task Tables_AreIndependent()
    {
        using var repository = NewRepository();
        await repository.TryAdvanceAsync(Source, "d", "s", "a", 7, 1, default);

        (await repository.GetAsync(Source, "d", "s", "b", default)).ShouldBeNull();
    }
}
