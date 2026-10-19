namespace Autheris.Tests.Unit.Cdc;

using System.Threading.Tasks;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Infrastructure.Streaming;

/// <summary>PLAN-CDC-99 WP-I1-A: the in-memory stream store satisfies the shared store contract.</summary>
public sealed class InMemoryCdcStreamStoreContractTests : CdcStreamStoreContractTests
{
    private InMemoryCdcStreamStore? _shared;

    protected override bool IsDurable => false;

    protected override Task<(ICdcStreamStore Streams, ICdcStreamKeyStore Keys)?> OpenAsync()
    {
        _shared ??= new InMemoryCdcStreamStore();
        return Task.FromResult<(ICdcStreamStore, ICdcStreamKeyStore)?>((_shared, new InMemoryCdcStreamKeyStore(_shared)));
    }
}
