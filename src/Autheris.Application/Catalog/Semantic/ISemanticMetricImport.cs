namespace Autheris.Application.Catalog.Semantic;

using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// Core import contract for the semantic metric model. Producers (the dbt manifest ingestion, any later module) hand a validated
/// manifest to the core catalog through this interface and never reference the catalog implementations.
/// </summary>
public interface ISemanticMetricImport
{
    /// <summary>Replaces the whole manifest of <paramref name="tenantId"/> (removed metrics disappear).</summary>
    Task ImportAsync(DbtSemanticManifest manifest, TenantId tenantId, string? importedBy = null, CancellationToken cancellationToken = default);
}
