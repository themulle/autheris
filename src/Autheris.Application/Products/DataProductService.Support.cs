namespace Autheris.Application.Products;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.ConsentRequests.Events;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;
using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Nodes;

/// <summary>The definition hash that ties a load to one immutable revision.</summary>
public static class ProductDefinitions
{
    public static string Hash(string definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var normalized = definition.Trim().ReplaceLineEndings("\n");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }
}

public sealed partial class DataProductService
{
    private const int MaxDescriptionLength = 1000;
    private const int MaxDefinitionLength = 100_000;
    private const int MaxBaseObjects = 64;

    [GeneratedRegex(@"\A[a-z][a-z0-9_]{0,62}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex NameRegex();

    private sealed record ProductAnalysis(
        IReadOnlyList<TableIdentifier> BaseObjects,
        IReadOnlyList<ProductOutputColumn> Columns,
        Func<ColumnLineage, bool> IsPii);

    internal static string Normalize(string value) => (value ?? string.Empty).Trim().ToLowerInvariant();

    private static bool ValidName(string value) => NameRegex().IsMatch(value);

    // The schema is the first part of the catalog identity (schema__name): it never holds the separator and never ends with an underscore.
    private static bool ValidSchema(string value) =>
        NameRegex().IsMatch(value) && !value.Contains(ProductSource.Separator, StringComparison.Ordinal) && !value.EndsWith('_', StringComparison.Ordinal);

    private static ProductException Invalid() => new(ProductFailure.Invalid, ProductErrorCodes.DefinitionInvalid);

    private static bool CanView(ProductCaller caller, DataProduct product) =>
        caller.Tenant == product.Tenant &&
        (caller.CanReadAll || ProductAuthorityService.IsProductOwner(caller, product) ||
         string.Equals(caller.Sid.Value, product.CreatedBy.Value, StringComparison.OrdinalIgnoreCase));

    private static bool SameObjects(IReadOnlyList<TableIdentifier> a, IReadOnlyList<TableIdentifier> b) =>
        a.Count == b.Count && a.All(b.Contains);

    private static LoadSettings ValidatedLoad(DataProductKind kind, LoadSettings? load)
    {
        if (load is null || kind == DataProductKind.Procedure)
        {
            return LoadSettings.Default;
        }

        var valid = load.Mode switch
        {
            LoadMode.Schedule => load.Interval is { } interval && interval >= LoadSettings.MinInterval && interval <= LoadSettings.MaxInterval,
            LoadMode.OnDemand => load.TtlMinutes is >= 1 and <= 1440 && load.IdleDropMinutes >= 0,
            LoadMode.Live => true,
            _ => false
        };
        return valid && load.MaxStalenessMinutes is null or > 0 ? load : throw Invalid();
    }

    private static ProductLimits ValidatedLimits(ProductLimits? limits) =>
        limits is null ? ProductLimits.Default
        : limits.MaxRows >= 0 && limits.MaxBytes >= 0 && limits.MaxRuntimeSeconds >= 0 ? limits : throw Invalid();

    private async Task<DataProduct> LoadAsync(ProductCaller caller, string schema, string name, CancellationToken ct) =>
        await _products.GetProductAsync(caller.Tenant, Normalize(schema), Normalize(name), ct).ConfigureAwait(false)
        ?? throw new ProductException(ProductFailure.NotFound, ProductErrorCodes.NotFound);

    private async Task<bool> HasDefinitionAuthorityAsync(ProductCaller caller, DataProduct product, CancellationToken ct)
    {
        var decision = await _authority.AuthorizeCreationAsync(caller, product.Kind, product.BaseObjects, product.ProductOwner, ct).ConfigureAwait(false);
        return decision.Allowed;
    }

    private async Task<DataProduct> ReplaceAsync(DataProduct updated, int expectedVersion, CancellationToken ct)
    {
        var outcome = await _products.ReplaceProductAsync(updated, expectedVersion, null, ct).ConfigureAwait(false);
        return outcome.Status == ProductWriteStatus.Replaced
            ? outcome.Product ?? updated
            : throw new ProductException(outcome.Status == ProductWriteStatus.NotFound ? ProductFailure.NotFound : ProductFailure.Conflict, ProductErrorCodes.Conflict);
    }

    private async Task<ProductAnalysis> AnalyzeAsync(TenantId tenant, DataProductKind kind, string? definition, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(definition) || definition.Length > MaxDefinitionLength)
        {
            throw Invalid();
        }

        if (kind == DataProductKind.Procedure)
        {
            var parts = definition.Trim().Split('.');
            if (parts.Length != 3 || !TableIdentifier.TryCreate(parts[0], parts[1], parts[2], out var procedure))
            {
                throw Invalid();
            }

            // The columns of a procedure result cannot be derived from the catalog: the result is classified when it is exposed.
            return new ProductAnalysis([procedure], [], static _ => true);
        }

        SqlStatement statement;
        try
        {
            var (tree, _) = _sqlEngine.Parse(definition.AsMemory(), null, ct);
            statement = new SqlAstBuilder().BuildStatement(tree);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Invalid();
        }

        var baseObjects = ProductLineageExtractor.CollectBaseObjects(statement);
        if (baseObjects is not { Count: > 0 and <= MaxBaseObjects })
        {
            throw Invalid();
        }

        var catalog = new Dictionary<TableIdentifier, IReadOnlyList<CatalogColumnInfo>>();
        foreach (var table in baseObjects)
        {
            catalog[table] = await _catalog.GetColumnsAsync(tenant, table, ct).ConfigureAwait(false) ?? throw Invalid();
        }

        var columns = ProductLineageExtractor.Extract(statement, catalog) ?? throw Invalid();
        bool IsPii(ColumnLineage lineage) =>
            !catalog.TryGetValue(lineage.Table, out var infos) ||
            infos.FirstOrDefault(i => string.Equals(i.Name, lineage.Column, StringComparison.OrdinalIgnoreCase)) is not { IsPii: false };
        return new ProductAnalysis(baseObjects, columns, IsPii);
    }

    /// <summary>One event per base object for the owners of that object; no approval, no veto. Returns the number of notified owners.</summary>
    private async Task<int> NotifyBaseOwnersAsync(DataProduct product, string eventType, DateTimeOffset now, CancellationToken ct)
    {
        var notified = 0;
        foreach (var baseObject in product.BaseObjects)
        {
            try
            {
                var owners = await _ownership.GetAuthorizedApproverSidsForTableAsync(baseObject, now, ct).ConfigureAwait(false);
                notified += owners.Count;
                if (_outbox is not null)
                {
                    await _outbox.AppendAsync(
                        new ConsentEventWrite(
                            product.Tenant.Value, Guid.NewGuid(), eventType, product.Id.ToString("D"),
                            ProductEventPayloads.Serialize(product, baseObject.ToString(), now), now),
                        transaction: null, ct: ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogWarning(ex, "Base owner notification for a data product event failed.");
            }
        }

        return notified;
    }

    private Task AuditAsync(string eventType, ProductCaller caller, DataProduct product, object details, DateTimeOffset now, CancellationToken ct) =>
        _audit.RecordRequiredAsync(new AuditLogEntry
        {
            EventType = eventType,
            TenantId = product.Tenant,
            ActorSid = caller.Sid,
            TargetTable = $"products.{product.Schema}.{product.Name}",
            Decision = "ALLOW",
            DetailsJson = JsonSerializer.Serialize(new
            {
                productId = product.Id,
                kind = product.Kind.ToString(),
                definitionHash = product.DefinitionHash,
                version = product.Version,
                productOwner = product.ProductOwner.Value,
                viaSync = caller.ViaSyncPath,
                detail = details
            }),
            OccurredAt = now
        }, ct);

    private async Task<ProductException> DenyAsync(ProductCaller caller, string schema, string name, string code, ProductFailure failure, CancellationToken ct)
    {
        await _audit.RecordAsync(new AuditLogEntry
        {
            EventType = AuditEventTypes.ProductAccessDenied,
            TenantId = caller.Tenant,
            ActorSid = caller.Sid,
            TargetTable = $"products.{schema}.{name}",
            Decision = "DENY",
            DetailsJson = JsonSerializer.Serialize(new { code }),
            OccurredAt = _time.GetUtcNow()
        }, ct).ConfigureAwait(false);
        return new ProductException(failure, code);
    }
}
