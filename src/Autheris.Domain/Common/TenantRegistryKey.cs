namespace Autheris.Domain.Common;

using System.Text;

/// <summary>Normalized grouping key of the tenant registry: NFKC plus invariant upper case.</summary>
public static class TenantRegistryKey
{
    public static string Normalize(string tenantId)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        return tenantId.Normalize(NormalizationForm.FormKC).ToUpperInvariant();
    }
}
