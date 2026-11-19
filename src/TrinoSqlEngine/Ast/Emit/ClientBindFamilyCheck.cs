namespace TrinoSqlEngine.Ast.Emit;

using System;
using System.Globalization;

/// <summary>Checks a client value against the family its slot requires (numeric, temporal, boolean); a null value always conforms.</summary>
internal static class ClientBindFamilyCheck
{
    private static readonly string[] DateTimeFormats = ["yyyy-MM-dd", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.FFFFFFF", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ss.FFFFFFF"];

    public static bool Conforms(BindFamily family, object? value)
    {
        if (value is null or DBNull) return true;
        var invariant = CultureInfo.InvariantCulture;
        return family switch
        {
            BindFamily.Numeric => value is sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal
                || (value is string n && decimal.TryParse(n, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, invariant, out _)),
            BindFamily.Temporal => value is DateTime or DateTimeOffset or DateOnly or TimeOnly or TimeSpan
                || (value is string t && DateTime.TryParseExact(t, DateTimeFormats, invariant, DateTimeStyles.None, out _)),
            BindFamily.Boolean => value is bool,
            // A number bound against a text column makes SQL Server and Oracle convert the column, which raises on non-numeric text.
            BindFamily.Text => value is string or Guid or char,
            _ => false
        };
    }
}
