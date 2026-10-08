namespace Autheris.Extensions.OData;

using System;
using System.Collections.Generic;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

public static class ODataResponseFormatter
{
    public static object FormatServiceDocument(string serviceRootUrl, IReadOnlyList<TableMetadata> tables)
    {
        var cleanRoot = serviceRootUrl.TrimEnd('/');
        var entitySets = new List<object>(tables.Count);

        foreach (var table in tables)
        {
            var id = table.Identifier;
            var name = $"{id.Domain}_{id.Schema}_{id.TableName}";
            var relativeUrl = $"{id.Domain}/{id.Schema}/{id.TableName}";

            entitySets.Add(new Dictionary<string, object?>
            {
                ["name"] = name,
                ["kind"] = "EntitySet",
                ["url"] = relativeUrl
            });
        }

        return new Dictionary<string, object?>
        {
            ["@odata.context"] = $"{cleanRoot}/$metadata",
            ["value"] = entitySets
        };
    }

    public static object FormatEntitySetResponse(
        string serviceRootUrl,
        TableIdentifier table,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        int? totalCount = null,
        string? nextLink = null)
    {
        var cleanRoot = serviceRootUrl.TrimEnd('/');
        var entitySetName = $"{table.Domain}_{table.Schema}_{table.TableName}";
        var context = $"{cleanRoot}/$metadata#{entitySetName}";

        var result = new Dictionary<string, object?>
        {
            ["@odata.context"] = context
        };

        if (totalCount.HasValue)
        {
            result["@odata.count"] = totalCount.Value;
        }

        result["value"] = rows;

        if (!string.IsNullOrWhiteSpace(nextLink))
        {
            result["@odata.nextLink"] = nextLink;
        }

        return result;
    }

    public static object FormatErrorResponse(string code, string message)
    {
        return new
        {
            error = new
            {
                code,
                message
            }
        };
    }
}
