using Autheris.Domain.Common;
using Autheris.Domain.Options;

namespace Autheris.Tests.Common;

/// <summary>
/// The default tenant used by tests that configure <c>Gateway:Tenancy:DefaultTenantId</c> (single-tenant deployment shape). There is no
/// product-side default tenant constant: the default tenant is deployment configuration (DT-A-1).
/// </summary>
public static class TestTenants
{
    /// <summary>Tenant id text of <see cref="Default"/>.</summary>
    public const string DefaultId = "default-tenant";

    /// <summary>The configured default tenant of single-tenant test deployments.</summary>
    public static readonly TenantId Default = new(DefaultId);
}

/// <summary>Test-only deployment shape: <see cref="Single"/> configures a default tenant, <see cref="Multi"/> configures none.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Test shape name.")]
public enum TenancyShape
{
    Multi = 0,
    Single = 1
}

public static class TenancyShapeExtensions
{
    /// <summary>The default tenant (catalog owner) of the shape; <c>null</c> for <see cref="TenancyShape.Multi"/>.</summary>
    public static TenantId? Owner(this TenancyShape shape) => shape == TenancyShape.Single ? TestTenants.Default : (TenantId?)null;

    /// <summary>The tenancy options of the shape: <see cref="TenancyShape.Single"/> sets <c>DefaultTenantId</c>.</summary>
    public static TenancyOptions Options(this TenancyShape shape) =>
        new() { DefaultTenantId = shape == TenancyShape.Single ? TestTenants.DefaultId : default(string) };
}
