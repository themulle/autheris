namespace Autheris.Infrastructure.Persistence;

using System.Data.Common;

/// <summary>
/// Parameter binding shared by the engine-neutral SQL cores (ARCH-MNT-22). <c>CreateParameter</c> + name + value yields the same provider
/// parameter as the provider's <c>AddWithValue</c> (<c>new XParameter(name, value)</c>), so type inference and binding order are unchanged.
/// Values are always bound parameters; nothing here concatenates caller data into SQL.
/// </summary>
internal static class SqlCoreBinding
{
    public static DbParameter AddBound(this DbCommand cmd, string name, object? value)
    {
        var parameter = cmd.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        cmd.Parameters.Add(parameter);
        return parameter;
    }
}
