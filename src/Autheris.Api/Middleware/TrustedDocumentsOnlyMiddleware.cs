namespace Autheris.Api.Middleware;

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using HotChocolate;
using HotChocolate.Execution;
using HotChocolate.Language;
using Microsoft.Extensions.DependencyInjection;
using RequestDelegate = HotChocolate.Execution.RequestDelegate;

/// <summary>
/// SEC H-08: Allowlist of trusted GraphQL documents. Documents are normalized (parsed and re-printed)
/// and identified by their SHA-256 hash, so whitespace/comment differences do not matter.
/// </summary>
public sealed class TrustedDocumentStore
{
    private readonly HashSet<string> _hashes = new(StringComparer.Ordinal);

    public TrustedDocumentStore(IEnumerable<string> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        foreach (var document in documents)
        {
            if (!string.IsNullOrWhiteSpace(document))
            {
                _hashes.Add(ComputeHash(document));
            }
        }
    }

    public int Count => _hashes.Count;

    public static TrustedDocumentStore LoadFromDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !System.IO.Directory.Exists(System.IO.Path.GetFullPath(directory)))
        {
            throw new InvalidOperationException(
                "GraphQL.PersistedQueriesOnly=true requires an existing GraphQL.TrustedDocumentsDirectory.");
        }

        var root = System.IO.Path.GetFullPath(directory);
        var documents = new List<string>();
        foreach (var file in System.IO.Directory.EnumerateFiles(root, "*.*", System.IO.SearchOption.AllDirectories))
        {
            if (file.EndsWith(".graphql", StringComparison.OrdinalIgnoreCase) ||
                file.EndsWith(".gql", StringComparison.OrdinalIgnoreCase))
            {
                documents.Add(System.IO.File.ReadAllText(file, Encoding.UTF8));
            }
        }

        var store = new TrustedDocumentStore(documents);
        if (store.Count == 0)
        {
            throw new InvalidOperationException(
                $"GraphQL.PersistedQueriesOnly=true, but the trusted documents directory '{root}' contains no *.graphql / *.gql documents.");
        }

        return store;
    }

    public static string ComputeHash(string documentText)
    {
        ArgumentNullException.ThrowIfNull(documentText);
        return ComputeHash(Utf8GraphQLParser.Parse(documentText));
    }

    public static string ComputeHash(DocumentNode document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var normalized = document.ToString();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    public bool IsTrusted(DocumentNode document) => _hashes.Contains(ComputeHash(document));

    public bool IsTrusted(string documentText)
    {
        try
        {
            return _hashes.Contains(ComputeHash(documentText));
        }
        catch (SyntaxException)
        {
            return false;
        }
    }
}

/// <summary>
/// SEC H-08: HotChocolate request middleware placed directly after UseDocumentCache (before parsing,
/// validation and execution). Rejects every ad-hoc document that is not part of the trusted document store.
/// Applies to HTTP and WebSocket (subscription) requests alike.
/// </summary>
public sealed class TrustedDocumentsOnlyMiddleware
{
    private readonly RequestDelegate _next;

    public TrustedDocumentsOnlyMiddleware(RequestDelegate next)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
    }

    public async ValueTask InvokeAsync(RequestContext context)
    {
        DocumentNode? doc = context.OperationDocumentInfo?.Document;
        if (doc == null && context.Request.Document is IOperationDocumentNodeProvider nodeProvider)
        {
            doc = nodeProvider.Document;
        }

        string? rawText = null;
        if (doc == null && context.Request.Document is not null)
        {
            rawText = context.Request.Document.ToString();
        }

        if (doc == null && string.IsNullOrWhiteSpace(rawText))
        {
            // No document supplied (id-only request). Without a document store the parser stage fails closed.
            await _next(context).ConfigureAwait(false);
            return;
        }

        var store = context.RequestServices.GetService<TrustedDocumentStore>();
        bool trusted = store != null && (doc != null ? store.IsTrusted(doc) : store.IsTrusted(rawText!));

        if (!trusted)
        {
            var error = ErrorBuilder.New()
                .SetMessage("Only trusted (persisted) GraphQL documents are allowed on this gateway.")
                .SetCode("TRUSTED_DOCUMENT_REQUIRED")
                .Build();
            context.Result = OperationResult.FromError(error);
            return;
        }

        await _next(context).ConfigureAwait(false);
    }
}
