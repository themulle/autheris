namespace Autheris.Tests.Unit;

using Autheris.Domain.Common;
using Autheris.Domain.Options;

/// <summary>
/// Test shim for the Mode=Single bridge (removed with WP-DT-6): the catalog owner (default tenant) of a legacy tenancy mode. Single maps to the
/// legacy tenant (the transitional default), everything else to no default tenant.
/// </summary>
internal static class TenancyModeTestExtensions
{
    public static Autheris.Domain.Common.TenantId? Owner(this TenancyMode mode) => mode == TenancyMode.Single ? Autheris.Domain.Common.TenantId.LegacySingleTenant : (Autheris.Domain.Common.TenantId?)null;
}
