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

    /// <summary>Rewrites <c>@name</c> markers of the known parameters to <c>:name</c>, skipping string literals and quoted identifiers.</summary>
    public static string RewriteMarkers(string sql, IReadOnlySet<string> names)
    {
        ArgumentNullException.ThrowIfNull(sql);
        var sb = new System.Text.StringBuilder(sql.Length);
        int i = 0;
        while (i < sql.Length)
        {
            char c = sql[i];
            if (c is '\'' or '"')
            {
                int start = i++;
                while (i < sql.Length)
                {
                    if (sql[i] == c)
                    {
                        if (i + 1 < sql.Length && sql[i + 1] == c) { i += 2; continue; }
                        i++;
                        break;
                    }

                    i++;
                }

                sb.Append(sql, start, i - start);
                continue;
            }

            if (c == '@' && i + 1 < sql.Length && (char.IsAsciiLetter(sql[i + 1]) || sql[i + 1] == '_'))
            {
                int end = i + 1;
                while (end < sql.Length && (char.IsAsciiLetterOrDigit(sql[end]) || sql[end] == '_')) end++;
                string name = sql.Substring(i + 1, end - i - 1);
                if (names.Contains(name))
                {
                    sb.Append(':').Append(name);
                    i = end;
                    continue;
                }
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }
}
