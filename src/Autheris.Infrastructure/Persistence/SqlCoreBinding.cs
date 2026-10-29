namespace Autheris.Infrastructure.Persistence;

using System.Data.Common;

/// <summary>
/// Parameter binding shared by the engine-neutral SQL cores (ARCH-MNT-22). <c>CreateParameter</c> + name + value yields the same provider
/// parameter as the provider's <c>AddWithValue</c> (<c>new XParameter(name, value)</c>), so type inference and binding order are unchanged.
/// Values are always bound parameters; nothing here concatenates caller data into SQL.
/// </summary>
internal static class SqlCoreBinding
{
    /// <summary>
    /// DT-SC-04: a tenant parameter never binds NULL, empty or whitespace (<c>default(TenantId)</c> has a null value), so a missed site fails closed
    /// with an <see cref="ArgumentException"/> instead of matching or writing an empty tenant. Applied by every shared parameter binder.
    /// </summary>
    internal static void GuardTenantParameter(string name, object? value)
    {
        // Only the primary tenant parameters; nullable owner/event tenant parameters (cluster rows) are bound elsewhere under other names.
        if ((string.Equals(name, "@tenantId", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "@tenant", StringComparison.OrdinalIgnoreCase))
            && (value is null || value is DBNull || (value is string text && string.IsNullOrWhiteSpace(text))))
        {
            throw new ArgumentException("A tenant is required; default(TenantId) is not a tenant.", nameof(value));
        }
    }

    public static DbParameter AddBound(this DbCommand cmd, string name, object? value)
    {
        GuardTenantParameter(name, value);
        var parameter = cmd.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        cmd.Parameters.Add(parameter);
        return parameter;
    }
}
