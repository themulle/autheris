namespace Autheris.Domain.Model;

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

/// <summary>
/// F-ARCH-11: Envoy External Authorization (ext_authz) request representation.
/// Conforms to envoy.service.auth.v3.CheckRequest JSON protocol.
/// </summary>
public sealed record EnvoyCheckRequest
{
    [JsonPropertyName("attributes")]
    public EnvoyAttributeContext? Attributes { get; init; }
}

public sealed record EnvoyAttributeContext
{
    [JsonPropertyName("source")]
    public EnvoyPeer? Source { get; init; }

    [JsonPropertyName("destination")]
    public EnvoyPeer? Destination { get; init; }

    [JsonPropertyName("request")]
    public EnvoyRequest? Request { get; init; }

    [JsonPropertyName("contextExtensions")]
    public Dictionary<string, string> ContextExtensions { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record EnvoyPeer
{
    [JsonPropertyName("address")]
    public EnvoyAddress? Address { get; init; }

    [JsonPropertyName("service")]
    public string? Service { get; init; }

    [JsonPropertyName("principal")]
    public string? Principal { get; init; }
}

public sealed record EnvoyAddress
{
    [JsonPropertyName("socketAddress")]
    public EnvoySocketAddress? SocketAddress { get; init; }
}

public sealed record EnvoySocketAddress
{
    [JsonPropertyName("address")]
    public string? Address { get; init; }

    [JsonPropertyName("portValue")]
    public int PortValue { get; init; }
}

public sealed record EnvoyRequest
{
    [JsonPropertyName("time")]
    public string? Time { get; init; }

    [JsonPropertyName("http")]
    public EnvoyHttpRequest? Http { get; init; }
}

public sealed record EnvoyHttpRequest
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("method")]
    public string Method { get; init; } = "GET";

    [JsonPropertyName("headers")]
    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("path")]
    public string Path { get; init; } = "/";

    [JsonPropertyName("host")]
    public string? Host { get; init; }

    [JsonPropertyName("scheme")]
    public string? Scheme { get; init; }

    [JsonPropertyName("body")]
    public string? Body { get; init; }
}

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
}
