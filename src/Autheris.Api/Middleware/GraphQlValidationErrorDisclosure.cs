using System.Collections.Immutable;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.GraphQL.Catalog;
using HotChocolate;
using HotChocolate.Execution;
using HotChocolate.Types;
using Microsoft.Extensions.DependencyInjection;
using RequestContext = HotChocolate.Execution.RequestContext;

namespace Autheris.Api.Middleware;

/// <summary>
/// Precise validation errors for catalog documents. A document validation failure (or a variable coercion failure) answers
/// <c>UNKNOWN_FIELD</c>, <c>ARGUMENT_INVALID</c> or <c>VARIABLE_TYPE_MISMATCH</c> only when the caller may read every catalog table the
/// document touches, including relation targets, with every column; in every other case the caller gets the shielded result and
/// nothing about the document is disclosed. Names in the answer come from the syntax tree, the text from fixed templates.
/// The class writes no logs.
/// </summary>
internal static class GraphQlValidationErrorDisclosure
{
    internal const string UnknownFieldCode = "UNKNOWN_FIELD";
    internal const string ArgumentInvalidCode = "ARGUMENT_INVALID";
    internal const string VariableTypeMismatchCode = "VARIABLE_TYPE_MISMATCH";
    internal const string VariableInvalidCode = "GRAPHQL_VARIABLE_INVALID";
    internal const int MaxErrors = 32;
    internal const int MaxEchoedNameLength = 128;

    private static readonly HashSet<string> Anchors = new(StringComparer.Ordinal)
    {
        "sec-Field-Selections",
        "sec-Input-Object-Field-Names",
        "sec-Values-of-Correct-Type",
        "sec-Argument-Names",
        "sec-Required-Arguments",
        "sec-Input-Object-Required-Fields",
        "sec-All-Variable-Usages-Are-Allowed"
    };


    private sealed record Entry(string Code, string? Table, string? Field, string? Argument, string? Variable, string? ExpectedType, HotChocolate.Language.Location Location);

    internal static async ValueTask<OperationResult?> TryBuildPreciseResultAsync(
        RequestContext context, OperationResult result, IReadOnlyList<IError> errors, bool validationFailed)
    {
        if (!validationFailed || errors.Count == 0 || errors.Count > MaxErrors || !HasEligibleShape(errors))
        {
            return null;
        }

        if (GraphQlEnumerationShieldMiddleware.ExtractDocument(context) is not { } document
            || context.Schema is not { } schema
            || CatalogDocumentLocator.TryCreate(document, schema) is not { } locator)
        {
            return null;
        }

        var entries = new List<Entry>(errors.Count);
        foreach (var error in errors)
        {
            if (Map(error, locator) is not { } entry || !WithinCap(entry))
            {
                return null;
            }

            if (!entries.Contains(entry))
            {
                entries.Add(entry);
            }
        }

        if (!await AllTablesFullyVisibleAsync(context, locator.Tables).ConfigureAwait(false))
        {
            return null;
        }

        var built = ImmutableList.CreateBuilder<IError>();
        foreach (var entry in entries)
        {
            built.Add(Build(entry));
        }

        await result.DisposeAsync().ConfigureAwait(false);
        return new OperationResult(built.ToImmutable())
        {
            ContextData = ImmutableDictionary<string, object?>.Empty.Add(ExecutionContextData.ValidationErrors, true)
        };
    }

    /// <summary>
    /// True when answering a variable coercion failure as is cannot reveal whether a catalog table exists: the document names no
    /// catalog root at all, or it names only catalog tables the caller may read with every column (relation targets included).
    /// A document the locator cannot evaluate, and any resolver problem, is false.
    /// </summary>
    internal static async ValueTask<bool> IsCoercionAnswerSafeAsync(RequestContext context)
    {
        if (GraphQlEnumerationShieldMiddleware.ExtractDocument(context) is not { } document || context.Schema is not { } schema)
        {
            return false;
        }

        if (!NamesCatalogRoot(document, schema))
        {
            return true;
        }

        return CatalogDocumentLocator.TryCreate(document, schema) is { } locator
            && await AllTablesFullyVisibleAsync(context, locator.Tables).ConfigureAwait(false);
    }

    private static bool NamesCatalogRoot(HotChocolate.Language.DocumentNode document, ISchemaDefinition schema)
    {
        if (schema.QueryType is not { } queryType)
        {
            return false;
        }

        foreach (var selection in document.Definitions.SelectMany(RootSelections))
        {
            if (selection is HotChocolate.Language.FieldNode field
                && (string.Equals(field.Name.Value, "table", StringComparison.Ordinal)
                    || (queryType.Fields.TryGetField(field.Name.Value, out var definition) && definition.Features.Get<CatalogRootFieldFeature>() is not null)))
            {
                return true;
            }

            if (selection is not HotChocolate.Language.FieldNode)
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<HotChocolate.Language.ISelectionNode> RootSelections(HotChocolate.Language.IDefinitionNode definition) =>
        definition switch
        {
            HotChocolate.Language.OperationDefinitionNode operation => operation.SelectionSet.Selections,
            HotChocolate.Language.FragmentDefinitionNode fragment => fragment.SelectionSet.Selections,
            _ => []
        };

    /// <summary>
    /// True when every error is a sanitized variable coercion failure: a value the type cannot take is reported either as
    /// <c>GRAPHQL_VARIABLE_INVALID</c> or, for a mismatched JSON kind, as the generic validation code without the validation marker.
    /// </summary>
    internal static bool IsVariableCoercionResult(IReadOnlyList<IError> errors) =>
        errors.Count > 0 && errors.All(e => string.Equals(e.Code, VariableInvalidCode, StringComparison.Ordinal)
            || string.Equals(e.Code, ErrorSanitizingFilter.ValidationCode, StringComparison.Ordinal));

    private static bool HasEligibleShape(IReadOnlyList<IError> errors)
    {
        foreach (var error in errors)
        {
            if (error.Locations is not { Count: 1 })
            {
                return false;
            }

            if (!Anchors.Contains(AnchorOf(error) ?? string.Empty))
            {
                return false;
            }
        }

        return true;
    }

    private static string? AnchorOf(IError error)
    {
        if (error.Extensions is not { } extensions || !extensions.TryGetValue("specifiedBy", out var value) || value is not string url)
        {
            return null;
        }

        var hash = url.LastIndexOf('#', StringComparison.Ordinal);
        return hash < 0 ? null : url[(hash + 1)..];
    }

    private static Entry? Map(IError error, CatalogDocumentLocator locator)
    {
        var position = error.Locations![0];

        var table = (LocatedSubject s) => s.Table.TypeName;
        switch (AnchorOf(error))
        {
            case "sec-Field-Selections":
                return locator.TryLocate(position.Line, position.Column, LocatedNodeKind.UnknownField, out var unknown)
                    ? new Entry(UnknownFieldCode, table(unknown), unknown.Field, null, null, null, unknown.Location)
                    : null;
            case "sec-Input-Object-Field-Names":
                if (!locator.TryLocate(position.Line, position.Column, LocatedNodeKind.ObjectField, out var objectField))
                {
                    return null;
                }

                return objectField.IsUnknownColumn
                    ? new Entry(UnknownFieldCode, table(objectField), objectField.Name, objectField.Argument, null, null, objectField.Location)
                    : new Entry(ArgumentInvalidCode, table(objectField), objectField.Field, objectField.Argument, null, null, objectField.Location);
            case "sec-Values-of-Correct-Type":
                if (!locator.TryLocate(position.Line, position.Column, LocatedNodeKind.Value, out var value))
                {
                    return null;
                }

                return value.IsUnknownColumn
                    ? new Entry(UnknownFieldCode, table(value), value.Name, value.Argument, null, null, value.Location)
                    : new Entry(ArgumentInvalidCode, table(value), value.Field, value.Argument, null, null, value.Location);
            case "sec-Argument-Names":
                return locator.TryLocate(position.Line, position.Column, LocatedNodeKind.Argument, out var argument)
                    ? new Entry(ArgumentInvalidCode, table(argument), argument.Field, argument.Argument, null, null, argument.Location)
                    : null;
            case "sec-Required-Arguments":
                return locator.TryLocate(position.Line, position.Column, LocatedNodeKind.KnownField, out var required) && required.Argument is not null
                    ? new Entry(ArgumentInvalidCode, table(required), required.Field, required.Argument, null, null, required.Location)
                    : null;
            case "sec-Input-Object-Required-Fields":
                return locator.TryLocate(position.Line, position.Column, LocatedNodeKind.ObjectValue, out var objectValue)
                    ? new Entry(ArgumentInvalidCode, table(objectValue), objectValue.Field, objectValue.Argument, null, null, objectValue.Location)
                    : null;
            case "sec-All-Variable-Usages-Are-Allowed":
                return locator.TryLocate(position.Line, position.Column, LocatedNodeKind.VariableUsage, out var usage)
                    ? new Entry(VariableTypeMismatchCode, table(usage), null, null, usage.Variable, usage.ExpectedType, usage.Location)
                    : null;
            default:
                return null;
        }
    }

    private static bool WithinCap(Entry entry) =>
        Fits(entry.Table) && Fits(entry.Field) && Fits(entry.Argument) && Fits(entry.Variable) && Fits(entry.ExpectedType)
        && entry.Table is not null
        && entry.Code switch
        {
            UnknownFieldCode => entry.Field is not null,
            ArgumentInvalidCode => entry.Field is not null && entry.Argument is not null,
            _ => entry.Variable is not null && entry.ExpectedType is not null
        };

    private static bool Fits(string? text) => text is null || (text.Length is > 0 and <= MaxEchoedNameLength);

    private static async ValueTask<bool> AllTablesFullyVisibleAsync(RequestContext context, IReadOnlyCollection<CatalogTableType> tables)
    {
        if (tables.Count is 0 or > CatalogDocumentLocator.MaxTables
            || context.RequestServices.GetService<ITableAccessResolver>() is not { } resolver)
        {
            return false;
        }

        var (principal, headers) = GraphQlEnumerationShieldMiddleware.ExtractCaller(context);
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        foreach (var table in tables)
        {
            ResolvedTableAccess resolved;
            try
            {
                resolved = await resolver.ResolveTableAccessAsync(principal, table.Identifier, null, headers, context.RequestAborted).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                return false;
            }

            if (!resolved.Decision.IsAllowed || table.Columns.Any(c => resolved.Metadata.GetColumn(c.ColumnName) is null))
            {
                return false;
            }

            foreach (var column in resolved.Metadata.Columns)
            {
                if (resolved.Decision.GetEffectiveColumnAccess(column.ColumnName, resolved.Metadata) == ColumnAccessLevel.Deny)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static IError Build(Entry entry)
    {
        var message = entry.Code switch
        {
            UnknownFieldCode => $"The field '{entry.Field}' does not exist on '{entry.Table}'.",
            ArgumentInvalidCode => $"The argument '{entry.Argument}' of '{entry.Field}' is invalid.",
            _ => $"The variable '${entry.Variable}' does not match the expected type '{entry.ExpectedType}'."
        };

        var builder = ErrorBuilder.New()
            .SetMessage(message)
            .SetCode(entry.Code)
            .AddLocation(new HotChocolate.Location(entry.Location.Line, entry.Location.Column))
            .SetExtension("table", entry.Table);
        if (entry.Field is not null)
        {
            builder.SetExtension("field", entry.Field);
        }

        if (entry.Argument is not null)
        {
            builder.SetExtension("argument", entry.Argument);
        }

        if (entry.Variable is not null)
        {
            builder.SetExtension("variable", entry.Variable);
        }

        if (entry.ExpectedType is not null)
        {
            builder.SetExtension("expectedType", entry.ExpectedType);
        }

        return builder.Build();
    }
}
