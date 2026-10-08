namespace Autheris.Domain.Model;

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

/// <summary>
/// F-ARCH-11: Envoy External Authorization response conforming to envoy.service.auth.v3.CheckResponse.
/// </summary>
public sealed record EnvoyCheckResponse
{
    [JsonPropertyName("status")]
    public EnvoyStatus Status { get; init; } = EnvoyStatus.Ok;

    [JsonPropertyName("httpResponse")]
    public EnvoyHttpResponse HttpResponse { get; init; } = new EnvoyHttpResponse();

    public static EnvoyCheckResponse Allow(
        string principal,
        string tenantId,
        string? rlsFilter = null,
        IReadOnlyDictionary<string, string>? additionalHeaders = null)
    {
        var headers = new List<EnvoyHeaderOption>
        {
            new("x-autheris-decision", "allowed"),
            new("x-autheris-principal", principal),
            new("x-autheris-tenant", tenantId)
        };

        // SEC C-1: Do not leak raw RLS SQL filter to downstream mesh headers.
        // Row-level security decisions remain strictly within gateway boundaries.

        if (additionalHeaders != null)
        {
            foreach (var kvp in additionalHeaders)
            {
                headers.Add(new(kvp.Key, kvp.Value));
            }
        }

        return new EnvoyCheckResponse
        {
            Status = EnvoyStatus.Ok,
            HttpResponse = new EnvoyHttpResponse
            {
                OkResponse = new EnvoyOkHttpResponse
                {
                    Headers = headers
                }
            }
        };
    }

    public static EnvoyCheckResponse Deny(int httpStatusCode, string reason, string? details = null)
    {
        var r = string.IsNullOrWhiteSpace(reason) ? "Access Denied" : reason;
        return new EnvoyCheckResponse
        {
            Status = httpStatusCode == 401 ? EnvoyStatus.Unauthenticated : EnvoyStatus.PermissionDenied,
            HttpResponse = new EnvoyHttpResponse
            {
                DeniedResponse = new EnvoyDeniedHttpResponse
                {
                    Status = new EnvoyHttpStatus { Code = httpStatusCode },
                    Headers = new List<EnvoyHeaderOption>
                    {
                        new("x-autheris-decision", "denied"),
                        new("x-autheris-denial-reason", r)
                    },
                    Body = details ?? r
                }
            }
        };
    }
}

public sealed record EnvoyStatus
{
    [JsonPropertyName("code")]
    public int Code { get; init; }

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;

    public static readonly EnvoyStatus Ok = new() { Code = 0, Message = "OK" };
    public static readonly EnvoyStatus PermissionDenied = new() { Code = 7, Message = "PERMISSION_DENIED" };
    public static readonly EnvoyStatus Unauthenticated = new() { Code = 16, Message = "UNAUTHENTICATED" };
    public static readonly EnvoyStatus InvalidArgument = new() { Code = 3, Message = "INVALID_ARGUMENT" };
}

public sealed record EnvoyHttpResponse
{
    [JsonPropertyName("okResponse")]
    public EnvoyOkHttpResponse? OkResponse { get; init; }

    [JsonPropertyName("deniedResponse")]
    public EnvoyDeniedHttpResponse? DeniedResponse { get; init; }
}

public sealed record EnvoyOkHttpResponse
{
    [JsonPropertyName("headers")]
    public List<EnvoyHeaderOption> Headers { get; init; } = new();
}

public sealed record EnvoyDeniedHttpResponse
{
    [JsonPropertyName("status")]
    public EnvoyHttpStatus Status { get; init; } = new() { Code = 403 };

    [JsonPropertyName("headers")]
    public List<EnvoyHeaderOption> Headers { get; init; } = new();

    [JsonPropertyName("body")]
    public string Body { get; init; } = "Access denied";
}

public sealed record EnvoyHttpStatus
{
    [JsonPropertyName("code")]
    public int Code { get; init; } = 403;
}

public sealed record EnvoyHeaderOption
{
    [JsonPropertyName("header")]
    public EnvoyHeader Header { get; init; }

    public EnvoyHeaderOption()
    {
        Header = new EnvoyHeader();
    }

    public EnvoyHeaderOption(string key, string value)
    {
        Header = new EnvoyHeader { Key = key, Value = value };
    }
}

public sealed record EnvoyHeader
{
    [JsonPropertyName("key")]
    public string Key { get; init; } = string.Empty;

    [JsonPropertyName("value")]
    public string Value { get; init; } = string.Empty;
}

/// <summary>
/// Options for exporting Istio EnvoyFilter or WasmPlugin CRDs.
/// </summary>
public sealed record EnvoyFilterExportOptions
{
    public string MeshNamespace { get; init; } = "istio-system";
    public string FilterName { get; init; } = "autheris-ext-authz";
    public string ServiceHost { get; init; } = "autheris-gateway.autheris.svc.cluster.local";
    public int ServicePort { get; init; } = 8080;
    public string AuthzPath { get; init; } = "/api/v1/envoy/check";
    public int TimeoutMs { get; init; } = 250;
    public bool FailOpen { get; init; } = false;
    public IReadOnlyList<string> PathPrefixes { get; init; } = new[] { "/api/", "/graphql" };
    public string WasmPluginTag { get; init; } = "v1.0.0";
    public string? WasmPluginUrl { get; init; }
}
