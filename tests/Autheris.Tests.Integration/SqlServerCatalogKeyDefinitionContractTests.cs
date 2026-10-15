namespace Autheris.Tests.Integration;

using System;
using System.Data.Common;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

/// <summary>Issue #94: declared key/index contract on SQL Server (see <see cref="CatalogKeyDefinitionContractTestsBase"/>).</summary>
public sealed class SqlServerCatalogKeyDefinitionContractTests : CatalogKeyDefinitionContractTestsBase, IClassFixture<SqlServerCatalogKeyDefinitionContractTests.Fixture>
{
    private readonly Fixture _fixture;

    public SqlServerCatalogKeyDefinitionContractTests(Fixture fixture) => _fixture = fixture;

    protected override bool Available => _fixture.Available;
    protected override IGovernanceRepository Repo => _fixture.Repository!;
    protected override DbConnection OpenRaw()
    {
        var conn = new SqlConnection(_fixture.ConnectionString);
        conn.Open();
        return conn;
    }

    protected override void InvalidateCache() => _fixture.Repository!.InvalidateMetadataCacheForTesting();

    public sealed class Fixture : IAsyncLifetime
    {
        private SqlServerTestDatabase? _db;
        public bool Available { get; private set; }
        public string ConnectionString => _db!.ConnectionString;
        public SqlServerGovernanceRepository? Repository { get; private set; }

        public async Task InitializeAsync()
        {
            _db = await SqlServerTestDatabase.CreateAsync();
            if (!_db.IsAvailable)
            {
                return;
            }

            var env = Substitute.For<IHostEnvironment>();
            env.EnvironmentName.Returns("Development");
            var options = Options.Create(new GatewayOptions
            {
                GovernanceDb = new GovernanceDbOptions { Provider = "SqlServer", ConnectionString = _db.ConnectionString, EnableOutboxProcessor = false, SeedDemoData = false }
            });
            Repository = GovernanceStoreFactory.CreateSqlServer(Substitute.For<IEpochValidationService>(), options, env);
            Available = true;
        }

        public async Task DisposeAsync()
        {
            Repository?.Dispose();
            if (_db != null)
            {
                await _db.DisposeAsync();
            }
        }
    }
}
