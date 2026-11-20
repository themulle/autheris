using System;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Tests.Common;
using Shouldly;

// Kept outside Governance/Contract/ on purpose: the coverage guard scans that folder for contract roots, and this partial adds a case to CdcStreamsContract.
namespace Autheris.Tests.Integration.Governance.Contract;

public abstract partial class CdcStreamsContract
{
    [RequiresContainerRuntimeFact]
    public async Task Reset_concurrent_with_append_completes_without_deadlock()
    {
        if (!Store.IsAvailable) return;
        var store = Store.CdcStreams;
        var s = await store.EnsureStreamAsync(Source, Orders);
        const int iterations = 20;

        var appends = Task.Run(async () =>
        {
            for (var i = 0; i < iterations; i++)
            {
                await store.AppendAsync(Batch(s, i, 1, watermark: 100 + i, firstVersion: i + 1));
            }
        });
        var resets = Task.Run(async () =>
        {
            for (var i = 0; i < iterations; i++)
            {
                await store.ResetWatermarkAsync(s.Id, "{}");
            }
        });

        await Task.WhenAll(appends, resets).WaitAsync(TimeSpan.FromSeconds(60));

        (await store.ReadAsync(s.Id, 1, 100)).Events.Select(e => e.Offset).ShouldBe(Enumerable.Range(1, iterations).Select(i => (long)i));
        await store.ResetWatermarkAsync(s.Id, "{\"final\":1}");
        (await store.GetWatermarkStateAsync(s.Id)).ShouldBe("{\"final\":1}");
    }
}
