namespace Autheris.Application.Products;

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Autheris.Domain.Model;

/// <summary>CloudEvents type identifiers of the product events published through the consent event outbox.</summary>
public static class ProductEventTypes
{
    public const string TypePrefix = "com.autheris.product.";
    public const string Created = TypePrefix + "created";
    public const string Changed = TypePrefix + "changed";
    public const string Retired = TypePrefix + "retired";
}

/// <summary>Thin, non-sensitive payload: names and hashes only, never the definition, column rules or owner identities.</summary>
public sealed record ProductThinEventPayload(
    string TenantId,
    Guid ProductId,
    string Product,
    string Kind,
    string BaseObject,
    string DefinitionHash,
    int Version,
    DateTimeOffset OccurredAt);

public static class ProductEventPayloads
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Serialize(DataProduct product, string baseObject, DateTimeOffset occurredAt) =>
        JsonSerializer.Serialize(
            new ProductThinEventPayload(
                product.Tenant.Value,
                product.Id,
                $"{product.Schema}.{product.Name}",
                product.Kind.ToString(),
                baseObject,
                product.DefinitionHash,
                product.Version,
                occurredAt),
            SerializerOptions);
}
