namespace Autheris.Application.Products.Sync;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Procedures.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// Git synchronisation of data products. The repository state is validated as a whole before anything is written: the first violation
/// (file rules, the same creation authority, lineage, hash and base-object checks as a REST PUT, a product owner equal to the sync
/// identity) rejects the commit and writes nothing. The sync never creates grants, a deletion in the repository retires the product and
/// never deletes it, drift (a product that is not Git-managed, a changed owner, a stored hash that differs from its history) is reported
/// and never overwritten. The load settings and the description of a Git product belong to the product owner after creation.
/// Every result is audited with the commit id and counts, never with file content.
/// </summary>
public sealed partial class ProductSyncService(
    DataProductService products,
    IDataProductRepository repository,
    IAuditWriter audit,
    IProcedureRegistry? procedures = null,
    TimeProvider? timeProvider = null)
{
    /// <summary>A retirement is final (the name stays taken), so one commit retires only a bounded number of products.</summary>
    internal const int MaxRetirementsPerSync = 25;

    private const int PageSize = 200;
    private const int MaxPages = 50;

    private readonly DataProductService _products = products ?? throw new ArgumentNullException(nameof(products));
    private readonly IDataProductRepository _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    private readonly IAuditWriter _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    private readonly IProcedureRegistry? _procedures = procedures;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    [GeneratedRegex(@"\A[0-9a-fA-F]{7,64}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex CommitRegex();

    private sealed record Work(ProductSyncItem Item, ProductSyncEntry? Entry, ProductPutRequest? Put, IReadOnlyList<ProductSyncClassification> Classify);

    /// <summary>Validates the commit and reports what an apply would do; nothing is written.</summary>
    public async Task<ProductSyncResult> PlanAsync(ProductSyncRequest request, CancellationToken ct = default)
    {
        var work = await BuildAsync(request, ct).ConfigureAwait(false);
        return new ProductSyncResult(request.Commit, false, work.Select(static w => w.Item).ToList());
    }

    /// <summary>Applies the commit: creates and changes the declared products, retires the removed ones, skips drift.</summary>
    public async Task<ProductSyncResult> ApplyAsync(ProductSyncRequest request, CancellationToken ct = default)
    {
        var work = await BuildAsync(request, ct).ConfigureAwait(false);
        var caller = request.Caller;
        try
        {
            foreach (var w in work)
            {
                switch (w.Item.Action)
                {
                    case ProductSyncAction.Create or ProductSyncAction.Change when w.Put is not null:
                        await _products.PutAsync(w.Put with { DryRun = false }, ct).ConfigureAwait(false);
                        foreach (var c in w.Classify)
                        {
                            await _products.ClassifyColumnAsync(caller, w.Item.Schema, w.Item.Name, c.Column, c.Class, c.Reason, ct).ConfigureAwait(false);
                        }

                        break;
                    case ProductSyncAction.Retire:
                        await _products.RetireAsync(caller, w.Item.Schema, w.Item.Name, ct).ConfigureAwait(false);
                        break;
                }
            }
        }
        catch (ProductException ex)
        {
            await AuditAsync(AuditEventTypes.ProductSyncRejected, request, "ERROR", new { phase = "apply", code = ex.Code }, ct).ConfigureAwait(false);
            throw;
        }

        var result = new ProductSyncResult(request.Commit, true, work.Select(static w => w.Item).ToList());
        var declassified = work.Sum(static w => w.Classify.Count(static c => c.Class != ProductColumnClass.PiiMasked)) +
                           work.Where(static w => w.Item.Action is ProductSyncAction.Create or ProductSyncAction.Change).Sum(static w => w.Entry?.Procedure?.ClearedResultColumns.Count ?? 0);
        await AuditAsync(
            AuditEventTypes.ProductSyncApplied, request, "ALLOW",
            new
            {
                created = result.Count(ProductSyncAction.Create),
                changed = result.Count(ProductSyncAction.Change),
                unchanged = result.Count(ProductSyncAction.Unchanged),
                retired = result.Count(ProductSyncAction.Retire),
                drift = result.Count(ProductSyncAction.Drift),
                declassifiedColumns = declassified,
                syncOriginated = true
            }, ct).ConfigureAwait(false);
        return result;
    }

    private async Task<IReadOnlyList<Work>> BuildAsync(ProductSyncRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            return await BuildCoreAsync(request, ct).ConfigureAwait(false);
        }
        catch (ProductSyncRejectedException ex)
        {
            await AuditAsync(AuditEventTypes.ProductSyncRejected, request, "DENY", new { phase = "validate", reason = ex.Reason, file = ex.FileIndex, code = ex.ProductCode }, ct).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<IReadOnlyList<Work>> BuildCoreAsync(ProductSyncRequest request, CancellationToken ct)
    {
        var caller = request.Caller;
        if (caller is null || !caller.ViaSyncPath || string.IsNullOrWhiteSpace(caller.Tenant.Value))
        {
            throw new ProductSyncRejectedException(ProductSyncReasons.NotAllowed);
        }

        if (string.IsNullOrEmpty(request.Commit) || !CommitRegex().IsMatch(request.Commit))
        {
            throw new ProductSyncRejectedException(ProductSyncReasons.CommitInvalid);
        }

        var entries = ProductSyncFileParser.Parse(request.Files ?? []);
        var work = new List<Work>(entries.Count);
        foreach (var entry in entries)
        {
            work.Add(await PlanEntryAsync(caller, entry, ct).ConfigureAwait(false));
        }

        var declared = entries.Select(static e => (e.Schema, e.Name)).ToHashSet();
        var retirements = new List<Work>();
        var after = (string?)null;
        for (var page = 0; page < MaxPages; page++)
        {
            var batch = await _repository.ListProductsAsync(caller.Tenant, after, PageSize, ct).ConfigureAwait(false);
            retirements.AddRange(batch
                .Where(p => p.ManagedBy is not null && p.Status != DataProductStatus.Retired && !declared.Contains((p.Schema, p.Name)))
                .Select(static p => new Work(new ProductSyncItem(p.Schema, p.Name, ProductSyncAction.Retire), null, null, [])));
            if (batch.Count < PageSize)
            {
                break;
            }

            after = $"{batch[^1].Schema}/{batch[^1].Name}";
        }

        if (retirements.Count > MaxRetirementsPerSync)
        {
            throw new ProductSyncRejectedException(ProductSyncReasons.TooManyRetirements);
        }

        work.AddRange(retirements);
        return work;
    }

    private async Task<Work> PlanEntryAsync(ProductCaller caller, ProductSyncEntry entry, CancellationToken ct)
    {
        CheckProcedureBinding(entry);
        var existing = await _repository.GetProductAsync(caller.Tenant, entry.Schema, entry.Name, ct).ConfigureAwait(false);
        if (existing is not null && await DriftReasonAsync(caller, entry, existing, ct).ConfigureAwait(false) is { } drift)
        {
            return new Work(new ProductSyncItem(entry.Schema, entry.Name, ProductSyncAction.Drift, drift), entry, null, []);
        }

        // The load settings and the description of an existing Git product belong to its product owner.
        var put = new ProductPutRequest(
            caller, entry.Schema, entry.Name, entry.Kind, entry.Definition, entry.Owner,
            existing?.Load ?? entry.Load, entry.Limits ?? existing?.Limits, existing?.Description ?? entry.Description, DryRun: true);
        DataProduct planned;
        try
        {
            planned = await _products.PutAsync(put, ct).ConfigureAwait(false);
        }
        catch (ProductException ex)
        {
            throw new ProductSyncRejectedException(ProductSyncReasons.FieldInvalid, entry.FileIndex, ex.Code);
        }

        var classify = PlanClassification(entry, planned, caller);
        var unchanged = existing is not null && existing.DefinitionHash == planned.DefinitionHash && existing.Limits == planned.Limits && classify.Count == 0;
        var action = existing is null ? ProductSyncAction.Create : unchanged ? ProductSyncAction.Unchanged : ProductSyncAction.Change;
        return new Work(new ProductSyncItem(entry.Schema, entry.Name, action), entry, put, classify);
    }

    /// <summary>A procedure file is only accepted for a procedure that is registered with the same product binding, procedure and mode.</summary>
    private void CheckProcedureBinding(ProductSyncEntry entry)
    {
        if (entry.Procedure is not { } declared)
        {
            return;
        }

        if (_procedures is null || !_procedures.TryGet(declared.Name, out var registered) || registered is null ||
            !string.Equals(registered.Definition.ProductSchema, declared.ProductSchema, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(registered.Definition.ProcedureName, declared.ProcedureName, StringComparison.OrdinalIgnoreCase) ||
            registered.Definition.Mode != declared.Mode)
        {
            throw new ProductSyncRejectedException(ProductSyncReasons.ProcedureNotBound, entry.FileIndex);
        }
    }

    private async Task<string?> DriftReasonAsync(ProductCaller caller, ProductSyncEntry entry, DataProduct existing, CancellationToken ct)
    {
        if (existing.Status == DataProductStatus.Retired)
        {
            return "RETIRED";
        }

        if (existing.ManagedBy is null)
        {
            return "NOT_GIT_MANAGED";
        }

        if (existing.Kind != entry.Kind)
        {
            return "KIND_DIFFERS";
        }

        if (!string.Equals(existing.ProductOwner.Value, entry.Owner.Value, StringComparison.OrdinalIgnoreCase))
        {
            return "OWNER_DIFFERS";
        }

        var versions = await _repository.GetProductVersionsAsync(caller.Tenant, existing.Id, ct).ConfigureAwait(false);
        return versions.Count > 0 && !string.Equals(versions.MaxBy(static v => v.Version)!.DefinitionHash, existing.DefinitionHash, StringComparison.Ordinal)
            ? "HASH_DIFFERS"
            : null;
    }

    /// <summary>
    /// The classification changes the file asks for: every declared class that differs from the planned column, and a return to masked
    /// for a declassified column the file no longer declares. A column the product does not have rejects the commit.
    /// </summary>
    private static List<ProductSyncClassification> PlanClassification(ProductSyncEntry entry, DataProduct planned, ProductCaller caller)
    {
        var changes = new List<ProductSyncClassification>();
        foreach (var declared in entry.Classifications)
        {
            var column = planned.Columns.FirstOrDefault(c => string.Equals(c.Name, declared.Column, StringComparison.OrdinalIgnoreCase))
                         ?? throw new ProductSyncRejectedException(ProductSyncReasons.ColumnUnknown, entry.FileIndex);
            if (column.Class == declared.Class)
            {
                continue;
            }

            if (ProductColumnClassifier.Declassify(column, declared.Class, declared.Reason, caller.Sid).Column is null)
            {
                throw new ProductSyncRejectedException(ProductSyncReasons.ReasonRequired, entry.FileIndex);
            }

            changes.Add(declared with { Column = column.Name });
        }

        changes.AddRange(planned.Columns
            .Where(c => c.DeclassifiedBy is not null && !entry.Classifications.Any(d => string.Equals(d.Column, c.Name, StringComparison.OrdinalIgnoreCase)))
            .Select(static c => new ProductSyncClassification(c.Name, ProductColumnClass.PiiMasked, null)));
        return changes;
    }

    private Task AuditAsync(string eventType, ProductSyncRequest request, string decision, object details, CancellationToken ct) =>
        _audit.RecordRequiredAsync(new AuditLogEntry
        {
            EventType = eventType,
            TenantId = request.Caller.Tenant,
            ActorSid = request.Caller.Sid,
            TargetTable = "products.sync",
            Decision = decision,
            DetailsJson = JsonSerializer.Serialize(new { commit = request.Commit is { } c && CommitRegex().IsMatch(c) ? c : "invalid", files = request.Files?.Count ?? 0, detail = details }),
            OccurredAt = _time.GetUtcNow()
        }, ct);
}
