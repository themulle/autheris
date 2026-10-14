using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Autheris.Domain.Common;

namespace Autheris.Application.OpenMetadata.Models;

public sealed record OpenMetadataEntityReference
{
    [JsonPropertyName("id")]
    public Guid? Id { get; init; }

    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("fullyQualifiedName")]
    public string FullyQualifiedName { get; init; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }
}

public sealed record OpenMetadataTag
{
    [JsonPropertyName("tagFQN")]
    public string TagFQN { get; init; } = string.Empty;

    [JsonPropertyName("labelType")]
    public string? LabelType { get; init; }

    [JsonPropertyName("state")]
    public string? State { get; init; }

    [JsonPropertyName("source")]
    public string? Source { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }
}

public sealed record OpenMetadataColumn
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("dataType")]
    public string DataType { get; init; } = "VARCHAR";

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("fullyQualifiedName")]
    public string? FullyQualifiedName { get; init; }

    [JsonPropertyName("tags")]
    public List<OpenMetadataTag> Tags { get; init; } = [];

    [JsonPropertyName("children")]
    public List<OpenMetadataColumn>? Children { get; init; }
}

public sealed record OpenMetadataTable
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; } = Guid.NewGuid();

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("fullyQualifiedName")]
    public string FullyQualifiedName { get; init; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("columns")]
    public List<OpenMetadataColumn> Columns { get; init; } = [];

    [JsonPropertyName("tags")]
    public List<OpenMetadataTag> Tags { get; init; } = [];

    [JsonPropertyName("owners")]
    public List<OpenMetadataEntityReference> Owners { get; init; } = [];

    [JsonPropertyName("database")]
    public OpenMetadataEntityReference? Database { get; init; }

    [JsonPropertyName("databaseSchema")]
    public OpenMetadataEntityReference? DatabaseSchema { get; init; }

    [JsonPropertyName("service")]
    public OpenMetadataEntityReference? Service { get; init; }

    [JsonPropertyName("serviceType")]
    public string? ServiceType { get; init; }
}

public sealed record OpenMetadataRule
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("fullyQualifiedName")]
    public string? FullyQualifiedName { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("resources")]
    public List<string> Resources { get; init; } = ["all"];

    [JsonPropertyName("operations")]
    public List<string> Operations { get; init; } = ["All"];

    [JsonPropertyName("effect")]
    public string Effect { get; init; } = "allow"; // "allow" or "deny"

    [JsonPropertyName("condition")]
    public string? Condition { get; init; }
}

public sealed record OpenMetadataPolicy
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; } = Guid.NewGuid();

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("fullyQualifiedName")]
    public string FullyQualifiedName { get; init; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;

    [JsonPropertyName("rules")]
    public List<OpenMetadataRule> Rules { get; init; } = [];
}

public sealed record OpenMetadataRole
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; } = Guid.NewGuid();

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("policies")]
    public List<OpenMetadataEntityReference> Policies { get; init; } = [];

    [JsonPropertyName("users")]
    public List<OpenMetadataEntityReference> Users { get; init; } = [];

    [JsonPropertyName("teams")]
    public List<OpenMetadataEntityReference> Teams { get; init; } = [];
}

public sealed record OpenMetadataTeam
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; } = Guid.NewGuid();

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("fullyQualifiedName")]
    public string? FullyQualifiedName { get; init; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("defaultRoles")]
    public List<OpenMetadataEntityReference> DefaultRoles { get; init; } = [];

    [JsonPropertyName("policies")]
    public List<OpenMetadataEntityReference> Policies { get; init; } = [];

    [JsonPropertyName("users")]
    public List<OpenMetadataEntityReference> Users { get; init; } = [];
}

public sealed record OpenMetadataUser
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; } = Guid.NewGuid();

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("fullyQualifiedName")]
    public string? FullyQualifiedName { get; init; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("email")]
    public string Email { get; init; } = string.Empty;

    [JsonPropertyName("isBot")]
    public bool IsBot { get; init; }

    [JsonPropertyName("roles")]
    public List<OpenMetadataEntityReference> Roles { get; init; } = [];

    [JsonPropertyName("teams")]
    public List<OpenMetadataEntityReference> Teams { get; init; } = [];
}

public sealed record OpenMetadataWebhookEvent
{
    [JsonPropertyName("id")]
    public Guid? Id { get; init; }

    [JsonPropertyName("eventType")]
    public string EventType { get; init; } = string.Empty; // entityCreated, entityUpdated, entityDeleted

    [JsonPropertyName("entityType")]
    public string EntityType { get; init; } = string.Empty; // table, policy, role, team, user

    [JsonPropertyName("entityId")]
    public Guid? EntityId { get; init; }

    [JsonPropertyName("entityFullyQualifiedName")]
    public string? EntityFullyQualifiedName { get; init; }

    [JsonPropertyName("timestamp")]
    public long? Timestamp { get; init; }
}

public sealed record OpenMetadataSyncResult(
    int SyncedTables,
    int SyncedConsents,
    int SyncedMaskingRules,
    IReadOnlyList<TableIdentifier> InvalidationTables,
    IReadOnlyList<string> Warnings,
    bool Success)
{
    /// <summary>
    /// SR-L-23: Warnings reduced to stable codes with counts (<c>OM_TABLE_MAP_FAILED: 3</c>), ordered by code. Contains no
    /// exception text and no table names; safe to return to GraphQL callers. <see cref="Warnings"/> keeps the full text for
    /// administrative REST/log paths.
    /// </summary>
    public IReadOnlyList<string> SanitizedWarnings =>
        Warnings
            .GroupBy(ClassifyWarning, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Key}: {g.Count().ToString(System.Globalization.CultureInfo.InvariantCulture)}")
            .ToList();

    internal static string ClassifyWarning(string warning)
    {
        if (warning.StartsWith("Skipped OpenMetadata table", StringComparison.Ordinal)) return "OM_TABLE_SKIPPED";
        if (warning.StartsWith("Rejected OpenMetadata tables", StringComparison.Ordinal)) return "OM_TABLE_COLLISION";
        if (warning.StartsWith("Failed to map or upsert table", StringComparison.Ordinal)) return "OM_TABLE_MAP_FAILED";
        if (warning.StartsWith("Failed to fetch tables", StringComparison.Ordinal)) return "OM_FETCH_FAILED";
        if (warning.StartsWith("Failed during policy/role/team", StringComparison.Ordinal)) return "OM_CONSENT_SYNC_FAILED";
        if (warning.StartsWith("Invalid OpenMetadata-derived consent", StringComparison.Ordinal)) return "OM_CONSENT_INVALID";
        if (warning.StartsWith("Failed to refresh", StringComparison.Ordinal)) return "OM_CONSENT_REFRESH_FAILED";
        if (warning.StartsWith("Failed to revoke", StringComparison.Ordinal)) return "OM_CONSENT_REVOKE_FAILED";
        if (warning.StartsWith("Failed to create consent", StringComparison.Ordinal)) return "OM_CONSENT_CREATE_FAILED";
        if (warning.StartsWith("Sync already in progress", StringComparison.Ordinal)) return "OM_SYNC_IN_PROGRESS";
        return "OM_WARNING";
    }
}
