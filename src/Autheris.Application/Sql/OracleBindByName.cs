using System.Data.Common;
using System.Security;

namespace Autheris.Application.Sql;

/// <summary>
/// SEC-ADG-03: ODP.NET binds by POSITION unless <c>OracleCommand.BindByName</c> is true. Every Oracle command that carries
/// parameters must call <see cref="Enable"/> before it executes; a command without the property is rejected.
/// </summary>
public static class OracleBindByName
{
    public static void Enable(DbCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var property = command.GetType().GetProperty("BindByName");
        if (property is null || property.PropertyType != typeof(bool) || !property.CanRead || !property.CanWrite)
        {
            throw new SecurityException("The Oracle command does not expose BindByName; positional binding is not permitted.");
        }

        property.SetValue(command, true);
        if (!(bool)property.GetValue(command)!)
        {
            throw new SecurityException("BindByName could not be enabled on the Oracle command.");
        }
    }
}
