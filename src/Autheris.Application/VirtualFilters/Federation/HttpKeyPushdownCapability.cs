namespace Autheris.Application.VirtualFilters.Federation;

using System;
using System.Collections.Generic;
using Autheris.Application.Services;
using Autheris.Domain.Model;

/// <summary>How the authorized keys can be sent to a web-api so that it only returns those rows.</summary>
public enum HttpKeyPushdownKind
{
    /// <summary>The endpoint cannot take keys (or the executor would drop or truncate them).</summary>
    None = 0,

    /// <summary>One GET with <c>?{param}=v1,v2,...</c>.</summary>
    QueryParameterList = 1,

    /// <summary>One POST with a JSON array body.</summary>
    JsonBodyArray = 2,

    /// <summary>One GET per key into the <c>{PrimaryKeyField}</c> placeholder of the path template.</summary>
    PathLookup = 3
}

/// <summary>
/// F-GOV-14 (WI-8, M7, SEC-15): the key pushdown an endpoint declares, derived from the existing batch capability of the
/// <see cref="HttpEndpointDescriptor"/> (<c>BatchType</c>, <c>BatchParamName</c>, <c>PrimaryKeyField</c>). Anything the
/// executor would silently drop, reject mid-flight or answer incompletely (paged endpoints, reserved or malformed
/// parameter names, a missing path placeholder) is "no capability", so a pushdown is never assumed to filter.
/// </summary>
public readonly record struct HttpKeyPushdownCapability(HttpKeyPushdownKind Kind, string ParameterName, string KeyField)
{
    private const int MaxNameLength = 64;
    private const int MaxValueLength = 256;

    public static HttpKeyPushdownCapability None => new(HttpKeyPushdownKind.None, string.Empty, string.Empty);

    public static HttpKeyPushdownCapability Resolve(HttpEndpointDescriptor? descriptor)
    {
        if (descriptor == null ||
            descriptor.BatchType == HttpBatchType.None ||
            (descriptor.Pagination != null && descriptor.Pagination.Strategy != HttpPaginationStrategy.None))
        {
            return None;
        }

        var parameter = descriptor.BatchParamName?.Trim();
        var keyField = descriptor.PrimaryKeyField?.Trim();
        if (!IsUsableName(parameter, descriptor) || string.IsNullOrEmpty(keyField))
        {
            return None;
        }

        switch (descriptor.BatchType)
        {
            case HttpBatchType.QueryParameterList:
                return new(HttpKeyPushdownKind.QueryParameterList, parameter!, keyField);
            case HttpBatchType.JsonBodyArray:
                return new(HttpKeyPushdownKind.JsonBodyArray, parameter!, keyField);
            case HttpBatchType.ParallelSingleRequests:
                return IsUsableName(keyField, descriptor) && (descriptor.PathTemplate ?? string.Empty).Contains("{" + keyField + "}", StringComparison.OrdinalIgnoreCase)
                    ? new(HttpKeyPushdownKind.PathLookup, parameter!, keyField)
                    : None;
            default:
                return None;
        }
    }

    /// <summary>
    /// True when the path template still contains a <c>{placeholder}</c> other than <paramref name="filledKeyField"/> (the
    /// one a path lookup fills per request). The gateway fills nothing else in a cross-source read, so such a request
    /// would go upstream with the braces unexpanded (CR-01).
    /// </summary>
    public static bool HasUnfillablePlaceholder(HttpEndpointDescriptor? descriptor, string? filledKeyField = null)
    {
        var template = descriptor?.PathTemplate;
        if (string.IsNullOrEmpty(template))
        {
            return false;
        }

        int i = 0;
        while (i < template.Length)
        {
            char c = template[i];
            if (c == '}')
            {
                return true;   // a stray brace is never a valid, fillable template
            }

            if (c != '{')
            {
                i++;
                continue;
            }

            int close = template.IndexOf('}', i + 1);
            if (close < 0)
            {
                return true;
            }

            var name = template.AsSpan(i + 1, close - i - 1).Trim();
            if (filledKeyField == null || !name.Equals(filledKeyField.AsSpan().Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            i = close + 1;
        }

        return false;
    }

    /// <summary>Whether <paramref name="value"/> can be sent in this kind of request unchanged and safely.</summary>
    public bool IsValueEligible(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxValueLength)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (char.IsControl(c))
            {
                return false;
            }
        }

        return Kind switch
        {
            HttpKeyPushdownKind.QueryParameterList => !value.Contains(','),
            HttpKeyPushdownKind.PathLookup => !value.Contains('/') && !value.Contains('\\') && !value.Contains("..", StringComparison.Ordinal),
            HttpKeyPushdownKind.JsonBodyArray => true,
            _ => false
        };
    }

    /// <summary>Same rules as the executor: well-formed, and not a name the gateway sets itself (tenant, api key).</summary>
    private static bool IsUsableName(string? name, HttpEndpointDescriptor descriptor)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength)
        {
            return false;
        }

        foreach (var c in name)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-' || c == '.'))
            {
                return false;
            }
        }

        // The executor's own list (no copy of it here) plus the descriptor's gateway-set names; no allocation per call.
        foreach (var reserved in DeclarativeHttpDataSourceExecutor.StaticReservedParameterNames)
        {
            if (string.Equals(reserved, name, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return !IsDescriptorReserved(descriptor.TenantIdQueryParam, name) &&
               !IsDescriptorReserved(descriptor.TenantIdHeaderName, name) &&
               !IsDescriptorReserved(descriptor.ApiKeyHeaderName, name);
    }

    private static bool IsDescriptorReserved(string? reserved, string name) =>
        !string.IsNullOrWhiteSpace(reserved) && string.Equals(reserved.Trim(), name, StringComparison.OrdinalIgnoreCase);
}
