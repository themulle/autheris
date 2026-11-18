namespace Autheris.Application.Procedures.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.Procedures.Interfaces;
using Autheris.Application.Products;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using TableDecision = (Autheris.Domain.Interfaces.TableAccessDecision Decision, Autheris.Domain.Model.TableMetadata Meta, System.Collections.Generic.IReadOnlySet<string>? SensitivityOnlyMasked);

/// <summary>
/// A read procedure exposed as a data product: the execute decision on the product object (grants, denies and hard row policies of the
/// product only) replaces the consent lookup on the governed table set. The result columns are the declared outputs, masked unless the
/// procedure declares a column clear-eligible and the product grant carries a clear rule. Autheris adds no transaction or connection limit.
/// </summary>
public sealed partial class GovernedProcedureExecutionService
{
    private readonly Autheris.Application.Products.IDataProductRepository? _products;

    private static bool IsProduct(ProcedureDefinition definition) => !string.IsNullOrWhiteSpace(definition.ProductSchema);

    /// <summary>
    /// The product decision of the caller. Any failure (no store, no product of kind procedure, retired product, no declared output, no
    /// grant, a row policy on the product result) is "not visible". A row policy cannot be pushed into a procedure and the result rows are
    /// not staged yet, so a product with one is not served instead of being served unfiltered.
    /// </summary>
    private async Task<ProcedureVisibility> EvaluateProductVisibilityAsync(
        ProcedureDefinition definition,
        ClaimsPrincipal user,
        Sid userSid,
        TenantId tenantId,
        bool consentBypassed,
        CancellationToken ct)
    {
        if (_products == null || _tableRepository == null || definition.DeclaredOutputs.Count == 0 || string.IsNullOrWhiteSpace(tenantId.Value))
        {
            return ProcedureVisibility.Hidden("product");
        }

        var schema = definition.ProductSchema!.Trim().ToLowerInvariant();
        var name = definition.Name.ToLowerInvariant();
        var product = await _products.GetProductAsync(tenantId, schema, name, ct).ConfigureAwait(false);
        if (product is null || product.Kind != DataProductKind.Procedure || product.Status != DataProductStatus.Active)
        {
            return ProcedureVisibility.Hidden("product");
        }

        var objectId = ProductSource.ObjectOf(tenantId, schema, name);
        var stored = await _tableRepository.GetTableMetadataAsync(objectId, ct).ConfigureAwait(false);
        if (stored is null || !stored.Table.IsActive ||
            (!string.IsNullOrEmpty(stored.OwnerTenantId) && !string.Equals(stored.OwnerTenantId, tenantId.Value, StringComparison.Ordinal)))
        {
            return ProcedureVisibility.Hidden("product");
        }

        var classes = ProductColumnClasses(definition);
        var columns = classes.Keys.Select(c => new TableColumn { TableId = stored.Table.Id, ColumnName = c }).ToList();
        var meta = stored with
        {
            Identifier = objectId,
            Columns = columns,
            ColumnClassifications = ClassificationMetadata.AssignPii(
                columns.Where(c => classes[c.ColumnName] != ProductColumnClass.NonPii), "PRODUCT")
        };

        var secContext = Autheris.Domain.Security.SecurityPrincipalContext.FromPrincipal(user, tenantId, clientIp: ResolveClientIp(user));
        var request = new AccessRequest(secContext, meta, AccessOperation.Execute, AccessTransport.Procedure);
        var decision = await _accessDecisionPoint!.DecideAsync(request, ct).ConfigureAwait(false);
        if (!decision.IsAllowed || !string.IsNullOrWhiteSpace(decision.CombinedRowFilterSql))
        {
            return ProcedureVisibility.Hidden("product");
        }

        var decisions = new Dictionary<string, TableDecision>(StringComparer.OrdinalIgnoreCase) { [objectId.ToQualifiedName()] = (decision, meta, null) };
        return new ProcedureVisibility(true, string.Empty, decisions);
    }

    /// <summary>Product column classes: every declared output is <c>PiiMasked</c> unless the procedure declares it clear-eligible.</summary>
    private static Dictionary<string, ProductColumnClass> ProductColumnClasses(ProcedureDefinition definition)
    {
        var clearEligible = new HashSet<string>(definition.ClearedResultColumns, StringComparer.OrdinalIgnoreCase);
        var classes = new Dictionary<string, ProductColumnClass>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in definition.DeclaredOutputs)
        {
            classes[column] = clearEligible.Contains(column) ? ProductColumnClass.PiiClearEligible : ProductColumnClass.PiiMasked;
        }

        return classes;
    }

    /// <summary>Result governance of a product: only declared output columns leave, each in the state its class and the product grant allow.</summary>
    private (IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows) GovernProductResult(
        ProcedureDefinition definition,
        RawProcedureResult raw,
        Dictionary<string, TableDecision> decisions,
        TenantId tenantId)
    {
        var (decision, meta, _) = decisions.Values.Single();
        var classes = ProductColumnClasses(definition);
        var plan = new List<(int Index, string Name, ColumnAccessLevel Level, MaskingRule? Rule)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < raw.Columns.Count; i++)
        {
            var column = raw.Columns[i];
            if (string.IsNullOrWhiteSpace(column))
            {
                continue;
            }

            if (!seen.Add(column))
            {
                throw new InvalidOperationException("The procedure returned duplicate column names; the result cannot be governed.");
            }

            if (!classes.TryGetValue(column, out var columnClass))
            {
                continue; // undeclared column: never exposed
            }

            var level = ProductColumnAccess.Resolve(columnClass, decision.GetEffectiveColumnAccess(column, meta));
            if (level == ColumnAccessLevel.Deny)
            {
                continue;
            }

            MaskingRule? rule = null;
            if (level == ColumnAccessLevel.Mask)
            {
                meta.ColumnMaskingRules.TryGetValue(column, out rule);
                rule ??= new MaskingRule { RuleType = "REDACT" };
                rule = Autheris.Application.Services.GatewayExecutionService.ScopeRuleForTenant(
                    rule, tenantId.Value, _options.Value.DataMasking?.HmacKeyId);
            }

            plan.Add((i, column, level, rule));
        }

        var rows = new List<IReadOnlyDictionary<string, object?>>(raw.Rows.Count);
        foreach (var row in raw.Rows)
        {
            var output = new Dictionary<string, object?>(plan.Count, StringComparer.Ordinal);
            foreach (var (index, columnName, level, rule) in plan)
            {
                object? value = row[index];
                output[columnName] = level == ColumnAccessLevel.Mask
                    ? (_masking != null && rule != null ? _masking.MaskValue(columnName, value, rule) : null)
                    : value;
            }

            rows.Add(output);
        }

        return (plan.Select(p => p.Name).ToList(), rows);
    }
}
