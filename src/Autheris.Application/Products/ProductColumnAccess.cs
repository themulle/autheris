namespace Autheris.Application.Products;

using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

/// <summary>
/// The column state a product column can reach: a <c>PiiMasked</c> column is always masked, a <c>PiiClearEligible</c> column is clear only
/// with a clear rule on the caller's product grant, a <c>NonPii</c> column follows the grant.
/// </summary>
public static class ProductColumnAccess
{
    public static ColumnAccessLevel Resolve(ProductColumnClass columnClass, ColumnAccessLevel grantedByProductGrant) =>
        grantedByProductGrant switch
        {
            ColumnAccessLevel.Deny => ColumnAccessLevel.Deny,
            ColumnAccessLevel.Clear when columnClass != ProductColumnClass.PiiMasked => ColumnAccessLevel.Clear,
            _ => ColumnAccessLevel.Mask
        };

    public static string? ToMaskCategory(ProductColumnClass columnClass) =>
        columnClass == ProductColumnClass.NonPii ? null : ClassificationScopes.PiiCode;
}
