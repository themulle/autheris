namespace Autheris.Tests.Unit.Cdc;

using System;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Options;
using NSubstitute;

/// <summary>PLAN-CDC-99 WP-I1-A: the SQLite governance repository satisfies the shared stream/key store contract.</summary>
public sealed class SqliteCdcStreamStoreContractTests : CdcStreamStoreContractTests
{
    private readonly string _connectionString = $"Data Source=cdcs_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";

    protected override Task<(ICdcStreamStore Streams, ICdcStreamKeyStore Keys)?> OpenAsync()
    {
        var repository = Track(GovernanceStoreFactory.CreateSqlite(
            Substitute.For<IEpochValidationService>(),
            Options.Create(new GatewayOptions
            {
                GovernanceDb = new GovernanceDbOptions { ConnectionString = _connectionString, SeedDemoData = false }
            })));
        return Task.FromResult<(ICdcStreamStore, ICdcStreamKeyStore)?>((repository, repository));
    }
}
