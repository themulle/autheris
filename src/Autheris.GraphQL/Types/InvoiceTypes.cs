using Autheris.Domain.Model;
using Autheris.GraphQL.Loaders;
using HotChocolate;
using HotChocolate.Types;

namespace Autheris.GraphQL.Types;

[ExtendObjectType(typeof(InvoiceRecord))]
public sealed class InvoiceRecordExtensions
{
    public async Task<IReadOnlyList<InvoiceItemRecord>?> GetItemsAsync(
        [Parent] InvoiceRecord invoice,
        InvoiceItemDataLoader dataLoader,
        CancellationToken ct)
    {
        // N+1 & Zero-Trust: Batch DataLoader batches queries across all parent records in 1 roundtrip,
        // and LoadInvoiceItemsBatchAsync enforces consent and audits the child table once.
        return await dataLoader.LoadAsync(invoice.Id, ct);
    }
}
