namespace Autheris.Application.Services;

using Autheris.Domain.Model;

/// <summary>Issue #111 (A-1 G1, F-C11): how much of a catalog row a consumer may put on the wire.</summary>
public enum CatalogOutputShape
{
    /// <summary>The row does not exist for this caller (fail closed, also the answer for unknown values).</summary>
    NotFound = 0,

    /// <summary>The allow-listed summary view: no columns, no free text, no access hints.</summary>
    Summary = 1,

    /// <summary>The complete output (columns are already filtered by the visibility engine).</summary>
    Full = 2
}

/// <summary>
/// The single shaping rule shared by MCP, GraphQL and <c>CatalogDiscoveryService</c>: full output only for an administrator view
/// (rows of the admin branches keep <see cref="CatalogView.Unmarked"/>) or for <see cref="CatalogView.Granted"/> and
/// <see cref="CatalogView.Full"/> rows; <see cref="CatalogView.Summary"/> gets the allow-list; everything else (an unmarked row of a
/// non-administrator, any unknown value) is not found. <c>isAdmin</c> must be the <see cref="CallerScopeResolver.IsAdminView"/> result
/// of the same resolution (same tenancy mode) that produced the list.
/// </summary>
public static class CatalogViewShaping
{
    public static CatalogOutputShape Resolve(bool isAdmin, CatalogView view)
    {
        if (isAdmin)
        {
            return CatalogOutputShape.Full;
        }

        return view switch
        {
            CatalogView.Granted => CatalogOutputShape.Full,
            CatalogView.Full => CatalogOutputShape.Full,
            CatalogView.Summary => CatalogOutputShape.Summary,
            _ => CatalogOutputShape.NotFound
        };
    }
}
