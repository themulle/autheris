namespace Autheris.Tests.SqlCompilerGate.Harness;

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Autheris.Tests.SqlCompilerGate.Model;

/// <summary>
/// The frozen results of the legacy rewriter (WP-X1 step X1.4). The legacy string rewriter (<c>RewriteRls</c>) was deleted with the
/// cutover; its outcome for every corpus statement, scenario and dialect was recorded on the pre-cutover head and is committed under
/// <c>golden-results/legacy-&lt;Dialect&gt;.json</c>. The differential gate (G4) classifies the live AST outcome against these results,
/// so the direction of every difference (rows, mask, write) is judged exactly as before. The files are never regenerated: the only
/// code that could write them (the legacy engine) no longer exists, and a corpus statement that has no frozen result fails the gate
/// instead of being compared with nothing.
/// </summary>
public sealed class FrozenLegacy
{
    public const string DirectoryName = "golden-results";

    private readonly Dictionary<(string Id, PolicyScenario Scenario), EngineOutcome> _outcomes;

    private FrozenLegacy(string dialect, string frozenFrom, Dictionary<(string, PolicyScenario), EngineOutcome> outcomes)
    {
        Dialect = dialect;
        FrozenFrom = frozenFrom;
        _outcomes = outcomes;
    }

    public string Dialect { get; }

    /// <summary>The commit the results were recorded on (the pre-cutover head).</summary>
    public string FrozenFrom { get; }

    public int Count => _outcomes.Count;

    public bool TryGet(string statementId, PolicyScenario scenario, out EngineOutcome outcome) =>
        _outcomes.TryGetValue((statementId, scenario), out outcome!);

    public static string PathFor(GateDialect dialect) => Path.Combine(AppContext.BaseDirectory, DirectoryName, $"legacy-{dialect}.json");

    public static FrozenLegacy Load(GateDialect dialect) => Parse(File.ReadAllText(PathFor(dialect)));

    // ---- codec --------------------------------------------------------------------------------------------------------------

    public static FrozenLegacy Parse(string json)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        var outcomes = new Dictionary<(string, PolicyScenario), EngineOutcome>();
        foreach (var item in root["results"]!.AsArray())
        {
            var o = item!.AsObject();
            var key = ((string)o["id"]!, Enum.Parse<PolicyScenario>((string)o["scenario"]!));
            outcomes[key] = ReadOutcome(o);
        }

        return new FrozenLegacy((string)root["dialect"]!, (string)root["frozenFrom"]!, outcomes);
    }

    private static EngineOutcome ReadOutcome(JsonObject o) => (string)o["kind"]! switch
    {
        "rejected" => new EngineOutcome.Rejected(Enum.Parse<RejectionStage>((string)o["stage"]!), (string)o["reason"]!, (bool)o["governed"]!),
        "rows" => new EngineOutcome.Rows([.. o["rows"]!.AsArray().Select(r => ReadCells(r!.AsArray()))]),
        "written" => new EngineOutcome.Written([.. o["changes"]!.AsArray().Select(c =>
        {
            var co = c!.AsObject();
            return new RowChange(((string)co["sign"]!)[0], (string)co["table"]!, ReadCells(co["cells"]!.AsArray()));
        })]),
        var other => throw new InvalidDataException($"Unknown frozen outcome kind '{other}'.")
    };

    private static object?[] ReadCells(JsonArray cells) => [.. cells.Select(c => c is null ? null : ReadCell((string)c!))];

    private static object ReadCell(string text)
    {
        int colon = text.IndexOf(':', StringComparison.Ordinal);
        string type = text[..colon], value = text[(colon + 1)..];
        var inv = CultureInfo.InvariantCulture;
        if (type.EndsWith("[]", StringComparison.Ordinal) && type != "Byte[]")
        {
            // an array cell (PostgreSQL): the elements are frozen one by one, the type is rebuilt from the first element's type name
            string elementName = type[..^2];
            var elements = value.Length == 0 ? [] : value.Split(ArraySeparator).Select(e => ReadCell(elementName + ":" + e)).ToArray();
            var elementType = ElementType(elementName);
            var array = Array.CreateInstance(elementType, elements.Length);
            for (int i = 0; i < elements.Length; i++) array.SetValue(elements[i], i);
            return array;
        }

        return type switch
        {
            "String" => value,
            "Byte[]" => Convert.FromHexString(value),
            "Boolean" => bool.Parse(value),
            "SByte" => sbyte.Parse(value, inv),
            "Byte" => byte.Parse(value, inv),
            "Int16" => short.Parse(value, inv),
            "UInt16" => ushort.Parse(value, inv),
            "Int32" => int.Parse(value, inv),
            "UInt32" => uint.Parse(value, inv),
            "Int64" => long.Parse(value, inv),
            "UInt64" => ulong.Parse(value, inv),
            "Decimal" => decimal.Parse(value, inv),
            "Double" => double.Parse(value, inv),
            "Single" => float.Parse(value, inv),
            "DateTime" => DateTime.ParseExact(value, "O", inv, DateTimeStyles.RoundtripKind),
            "DateTimeOffset" => DateTimeOffset.ParseExact(value, "O", inv),
            "DateOnly" => DateOnly.ParseExact(value, "O", inv),
            "TimeOnly" => TimeOnly.ParseExact(value, "O", inv),
            "TimeSpan" => TimeSpan.ParseExact(value, "c", inv),
            "Guid" => Guid.ParseExact(value, "D"),
            _ => throw new InvalidDataException($"Unsupported frozen cell type '{type}'.")
        };
    }

    private const char ArraySeparator = '\u001f';

    private static Type ElementType(string name) => name switch
    {
        "String" => typeof(string), "Boolean" => typeof(bool), "SByte" => typeof(sbyte), "Byte" => typeof(byte), "Int16" => typeof(short),
        "UInt16" => typeof(ushort), "Int32" => typeof(int), "UInt32" => typeof(uint), "Int64" => typeof(long), "UInt64" => typeof(ulong),
        "Decimal" => typeof(decimal), "Double" => typeof(double), "Single" => typeof(float), "DateTime" => typeof(DateTime),
        "DateTimeOffset" => typeof(DateTimeOffset), "DateOnly" => typeof(DateOnly), "TimeOnly" => typeof(TimeOnly), "TimeSpan" => typeof(TimeSpan),
        "Guid" => typeof(Guid),
        _ => throw new InvalidDataException($"Unsupported frozen array element type '{name}'.")
    };

    public static string WriteCell(object? value)
    {
        var inv = CultureInfo.InvariantCulture;
        if (value is Array array and not byte[])
        {
            var parts = array.Cast<object?>().Select(e => e is null ? throw new InvalidDataException("Null array elements are not frozen.") : WriteCell(e)).ToList();
            string elementName = array.GetType().GetElementType()!.Name;
            var texts = parts.Select(p => p[(p.IndexOf(':', StringComparison.Ordinal) + 1)..]).ToList();
            if (texts.Any(t => t.Contains(ArraySeparator, StringComparison.Ordinal))) throw new InvalidDataException("An array element contains the separator.");
            return elementName + "[]:" + string.Join(ArraySeparator, texts);
        }

        return value switch
        {
            string s => "String:" + s,
            byte[] b => "Byte[]:" + Convert.ToHexString(b),
            bool f => "Boolean:" + (f ? "True" : "False"),
            sbyte or byte or short or ushort or int or uint or long or ulong => value.GetType().Name + ":" + Convert.ToString(value, inv),
            decimal d => "Decimal:" + d.ToString(inv),
            double d => "Double:" + d.ToString("R", inv),
            float f => "Single:" + f.ToString("R", inv),
            DateTime t => "DateTime:" + t.ToString("O", inv),
            DateTimeOffset o => "DateTimeOffset:" + o.ToString("O", inv),
            DateOnly d => "DateOnly:" + d.ToString("O", inv),
            TimeOnly t => "TimeOnly:" + t.ToString("O", inv),
            TimeSpan s => "TimeSpan:" + s.ToString("c", inv),
            Guid g => "Guid:" + g.ToString("D"),
            _ => throw new InvalidDataException($"Cannot freeze a cell of type {value?.GetType().FullName}.")
        };
    }

    /// <summary>Test support for the codec round trip; production of the files happened once on the pre-cutover head.</summary>
    public static JsonObject WriteOutcome(string id, PolicyScenario scenario, EngineOutcome outcome)
    {
        var o = new JsonObject { ["id"] = id, ["scenario"] = scenario.ToString() };
        switch (outcome)
        {
            case EngineOutcome.Rejected r:
                o["kind"] = "rejected"; o["stage"] = r.Stage.ToString(); o["reason"] = r.Reason; o["governed"] = r.Governed;
                break;
            case EngineOutcome.Rows rows:
                o["kind"] = "rows";
                o["rows"] = new JsonArray([.. rows.Data.Select(r => (JsonNode?)new JsonArray([.. r.Select(c => c is null || c is DBNull ? null : JsonValue.Create(WriteCell(c)))]))]);
                break;
            case EngineOutcome.Written w:
                o["kind"] = "written";
                o["changes"] = new JsonArray([.. w.Changes.Select(c => (JsonNode?)new JsonObject
                {
                    ["sign"] = c.Sign.ToString(),
                    ["table"] = c.Table,
                    ["cells"] = new JsonArray([.. c.Cells.Select(x => x is null || x is DBNull ? null : JsonValue.Create(WriteCell(x)))])
                })]);
                break;
            default:
                throw new InvalidDataException("Unknown outcome.");
        }

        return o;
    }

    public static string Serialize(string dialect, string frozenFrom, IEnumerable<JsonObject> results) =>
        new JsonObject { ["dialect"] = dialect, ["frozenFrom"] = frozenFrom, ["results"] = new JsonArray([.. results]) }
            .ToJsonString(new JsonSerializerOptions { WriteIndented = false });
}
