namespace Autheris.Modules.Dbt;

using System.Linq;
using System.Text.Json;

/// <summary>
/// Detection of owner keys in a dbt governance import item. Per-table owners no longer exist (#1009, D13); an item with any of these keys
/// rejects the whole import.
/// </summary>
public static class DbtOwnerSpecValidator
{
    private static readonly string[] OwnerKeys = ["owner", "owners", "group", "groups"];

    /// <summary>True when the item carries any owner related key (<c>owner</c>, <c>owners</c>, <c>group</c>, <c>groups</c>).</summary>
    public static bool HasOwnerKeys(JsonElement item) =>
        item.ValueKind == JsonValueKind.Object &&
        OwnerKeys.Any(key => item.TryGetProperty(key, out _));
}
