namespace Autheris.Domain.Model;

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

/// <summary>
/// F-DATA-05: Apache Iceberg REST Catalog (IRC) Federation & Dynamic STS Credential Vending.
/// </summary>
public sealed record IcebergListNamespacesResponse(
    [property: JsonPropertyName("namespaces")] IReadOnlyList<IReadOnlyList<string>> Namespaces
);

public sealed record IcebergListTablesResponse(
    [property: JsonPropertyName("identifiers")] IReadOnlyList<IcebergRestTableIdentifier> Identifiers
);

public sealed record IcebergRestTableIdentifier(
    [property: JsonPropertyName("namespace")] IReadOnlyList<string> Namespace,
    [property: JsonPropertyName("name")] string Name
);

public sealed record IcebergLoadTableResponse(
    [property: JsonPropertyName("metadata-location")] string MetadataLocation,
    [property: JsonPropertyName("metadata")] IcebergTableMetadata? Metadata,
    [property: JsonPropertyName("config")] IReadOnlyDictionary<string, string> Config
);

public enum StorageCredentialType
{
    AwsStsSession = 1,
    AzureSasToken = 2,
    GcpOauthBearer = 3,
    ScopedLocal = 4
}

public sealed record VendedStorageCredential(
    StorageCredentialType Type,
    string AccessKeyId,
    string SecretAccessKey,
    string SessionToken,
    DateTimeOffset ExpirationUtc,
    string ScopedLocationPrefix
);
