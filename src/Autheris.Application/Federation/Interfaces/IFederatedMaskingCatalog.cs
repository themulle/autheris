namespace Autheris.Application.Federation.Interfaces;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// SR-L-12: Supplies catalog-derived masking classifications for federated subgraph result fields.
/// Subgraphs do not expose catalog tables, so classifications are keyed by normalized field name
/// (see <see cref="FederatedFieldName.Normalize"/>).
/// </summary>
public interface IFederatedMaskingCatalog
{
    /// <summary>
    /// Returns the classification map (normalized field name to masking rule) for the given tenant.
    /// Throws if the catalog cannot be read; callers must fail closed.
    /// </summary>
    ValueTask<IReadOnlyDictionary<string, MaskingRule>> GetClassificationsAsync(TenantId tenant, CancellationToken ct = default);
}

/// <summary>Name normalization shared by catalog mapping, config overrides and result masking.</summary>
public static class FederatedFieldName
{
    /// <summary>Lower-cases and removes '_' and '-' so that <c>taxNumber</c> and <c>tax_number</c> match.</summary>
    public static string Normalize(string name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;
        return name.Replace("_", string.Empty, System.StringComparison.Ordinal)
            .Replace("-", string.Empty, System.StringComparison.Ordinal)
            .ToLowerInvariant();
    }
}
