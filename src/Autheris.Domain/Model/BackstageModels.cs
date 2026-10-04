namespace Autheris.Domain.Model;

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

/// <summary>
/// Spotify Backstage Entity Descriptor (v1alpha1 format)
/// https://backstage.io/docs/features/software-catalog/descriptor-format/
/// </summary>
public sealed record BackstageEntity
{
    [JsonPropertyName("apiVersion")]
    public string ApiVersion { get; init; } = "backstage.io/v1alpha1";

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "API";

    [JsonPropertyName("metadata")]
    public required BackstageMetadata Metadata { get; init; }

    [JsonPropertyName("spec")]
    public required BackstageSpec Spec { get; init; }
}

public sealed record BackstageMetadata
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("namespace")]
    public string Namespace { get; init; } = "default";

    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; init; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    [JsonPropertyName("tags")]
    public List<string> Tags { get; init; } = [];

    [JsonPropertyName("annotations")]
    public Dictionary<string, string> Annotations { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("links")]
    public List<BackstageEntityLink> Links { get; init; } = [];
}

public sealed record BackstageEntityLink
{
    [JsonPropertyName("url")]
    public required string Url { get; init; }

    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; init; }

    [JsonPropertyName("icon")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Icon { get; init; }
}

public sealed record BackstageSpec
{
    [JsonPropertyName("type")]
    public required string Type { get; init; } = "graphql";

    [JsonPropertyName("lifecycle")]
    public required string Lifecycle { get; init; } = "production";

    [JsonPropertyName("owner")]
    public required string Owner { get; init; }

    [JsonPropertyName("system")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? System { get; init; }

    [JsonPropertyName("definition")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Definition { get; init; }
}
