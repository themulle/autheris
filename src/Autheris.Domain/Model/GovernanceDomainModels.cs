using System.Text;
using Autheris.Domain.Common;
using Autheris.Domain.Security;

namespace Autheris.Domain.Model;

public enum CatalogVisibilityMode { Open = 0, OnRequest = 1 }

public static class CatalogVisibilityModeExtensions
{
    public static bool TryParse(string? value, out CatalogVisibilityMode result)
    {
        var span = value is null ? default : value.AsSpan().Trim();
        if (Ascii.EqualsIgnoreCase(span, "OPEN")) { result = CatalogVisibilityMode.Open; return true; }
        if (Ascii.EqualsIgnoreCase(span, "ON_REQUEST") || Ascii.EqualsIgnoreCase(span, "ONREQUEST")) { result = CatalogVisibilityMode.OnRequest; return true; }

        result = CatalogVisibilityMode.OnRequest;
        return false;
    }

    public static CatalogVisibilityMode ParseOrOnRequest(string? value) =>
        TryParse(value, out var parsed) ? parsed : CatalogVisibilityMode.OnRequest;

    public static CatalogVisibilityMode NormalizeOrOnRequest(this CatalogVisibilityMode value) =>
        value is CatalogVisibilityMode.Open or CatalogVisibilityMode.OnRequest ? value : CatalogVisibilityMode.OnRequest;

    public static string ToCanonicalName(this CatalogVisibilityMode value) =>
        NormalizeOrOnRequest(value) switch
        {
            CatalogVisibilityMode.Open => "OPEN",
            _ => "ON_REQUEST"
        };
}

public enum ContainerKind { Domain = 0, DataProduct = 1 }

public readonly record struct ContainerRef(ContainerKind Kind, Guid Id);

public sealed record ContainerDefaults(
    ContainerRef Container,
    Sid? OwnerGroupSid,
    ProtectionClass? DefaultClass,
    CatalogVisibilityMode Visibility,
    bool IsValid = true)
{
    public static ContainerDefaults Unassigned =>
        new(new ContainerRef(ContainerKind.Domain, Guid.Empty), null, ProtectionClass.Confidential, CatalogVisibilityMode.OnRequest, IsValid: false);
}

public sealed record GovernanceDomain(
    Guid Id,
    TenantId Tenant,
    string Name,
    string? Description,
    string? Contact,
    Sid? OwnerGroupSid,
    ProtectionClass? DefaultClass,
    CatalogVisibilityMode Visibility,
    bool IsSystemUnassigned,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ObjectProtection(ProtectionClass? Class, string? MaskCategory = null);

public sealed record ColumnProtection(ProtectionClass EffectiveClass, string? EffectiveMaskCategory);

public sealed record EffectiveProtection(
    ContainerRef Container,
    Sid? OwnerGroupSid,
    ProtectionClass TableClass,
    ProtectionClass DeclaredTableClass,
    string? TableMaskCategory,
    IReadOnlyDictionary<string, ColumnProtection> Columns,
    CatalogVisibilityMode Visibility);
