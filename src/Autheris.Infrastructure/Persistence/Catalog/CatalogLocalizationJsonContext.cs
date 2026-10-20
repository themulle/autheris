namespace Autheris.Infrastructure.Persistence.Catalog;

using System.Collections.Generic;
using System.Text.Json.Serialization;
using Autheris.Domain.Model;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Dictionary<string, LocalizedTextEntry>))]
internal sealed partial class CatalogLocalizationJsonContext : JsonSerializerContext
{
}
