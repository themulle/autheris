using System.Text;

namespace Autheris.Tests.Unit.Persistence.DialectSql;

/// <summary>
/// Reads the L1 static goldens of ARCH-MNT-22 (<c>tests/Autheris.Tests.Architecture/Golden/DialectSql</c>), which were generated from the
/// pre-refactor code, so L2 runtime tests can assert that the refactored code still produces exactly those fragments.
/// </summary>
internal sealed class DialectSqlGolden
{
    private DialectSqlGolden(IReadOnlyList<string> set, IReadOnlyDictionary<string, IReadOnlyList<string>> methods)
    {
        Set = set;
        Methods = methods;
    }

    public IReadOnlyList<string> Set { get; }

    public IReadOnlyDictionary<string, IReadOnlyList<string>> Methods { get; }

    public static DialectSqlGolden Load(string dialect, string area)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Autheris.sln")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException("Autheris.sln not found above the test output directory");
        }

        var path = Path.Combine(dir.FullName, "tests", "Autheris.Tests.Architecture", "Golden", "DialectSql", $"{dialect}.{area}.txt");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("missing dialect SQL golden (fail-closed)", path);
        }

        var set = new List<string>();
        var methods = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        List<string> current = set;
        foreach (var line in File.ReadAllText(path).Split('\n'))
        {
            if (line == "## set")
            {
                current = set;
            }
            else if (line.StartsWith("## method ", StringComparison.Ordinal))
            {
                var list = new List<string>();
                methods[line["## method ".Length..]] = list;
                current = list;
            }
            else if (!line.StartsWith("## set_Transaction ", StringComparison.Ordinal))
            {
                current.Add(Unescape(line));
            }
        }

        return new DialectSqlGolden(set, methods);
    }

    /// <summary>The single fragment of <paramref name="method"/> that contains <paramref name="marker"/> (ordinal).</summary>
    public string Fragment(string method, string marker) =>
        Methods[method].Single(f => f.Contains(marker, StringComparison.Ordinal));

    /// <summary>All ordered fragments of the root method <paramref name="method"/> (string literals and <c>{field:Type.Field}</c> references).</summary>
    public IReadOnlyList<string> Of(string method) => Methods[method];

    private static string Unescape(string value)
    {
        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\' || i == value.Length - 1)
            {
                sb.Append(value[i]);
                continue;
            }

            i++;
            switch (value[i])
            {
                case '\\': sb.Append('\\'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'u':
                    sb.Append((char)Convert.ToInt32(value.Substring(i + 1, 4), 16));
                    i += 4;
                    break;
                default: sb.Append('\\').Append(value[i]); break;
            }
        }

        return sb.ToString();
    }
}
