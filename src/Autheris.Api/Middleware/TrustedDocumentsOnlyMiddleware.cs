namespace Autheris.Api.Middleware;

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Application.Governance.TrustedDocuments;
using Autheris.Domain.Options;
using HotChocolate;
using HotChocolate.Execution;
using HotChocolate.Language;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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

    public static TrustedDocumentStore LoadFromDirectory(string directory, bool allowEmpty = false)
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
        if (store.Count == 0 && !allowEmpty)
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
    /// <summary>Error code of the rejection result; shared with the readiness executor gate (PLAN-OPS-150).</summary>
    public const string RejectionCode = "TRUSTED_DOCUMENT_REQUIRED";

    private readonly RequestDelegate _next;

    public TrustedDocumentsOnlyMiddleware(RequestDelegate next)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
    }

    /// <summary>Why an app-scoped request was rejected. Only logged (Debug), never returned to the caller (SEC-281-4).</summary>
    internal enum AppRejectionReason
    {
        MalformedHeader,
        OriginRejected,
        UnsupportedRequestShape,
        NoSourceText,
        NotTrusted,
        RegistryError
    }

    private static readonly Action<ILogger, AppRejectionReason, string, Exception?> LogAppRejection =
        LoggerMessage.Define<AppRejectionReason, string>(
            LogLevel.Debug, new EventId(28101, "TrustedDocumentAppRejected"), "App-scoped trusted document rejected: reason={Reason} app={App}");

    public async ValueTask InvokeAsync(RequestContext context)
    {
        if (TryGetAppHeader(context, out var headerValues, out var httpContext))
        {
            // SEC-281-2: header present means app path only; no failure falls back to the global store.
            if (await IsAppRequestTrustedAsync(context, httpContext, headerValues).ConfigureAwait(false))
            {
                await _next(context).ConfigureAwait(false);
            }
            else
            {
                Reject(context);
            }

            return;
        }

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
            Reject(context);
            return;
        }

        await _next(context).ConfigureAwait(false);
    }

    /// <summary>The single rejection result of both paths: identical message, code and shape (SEC-281-4).</summary>
    private static void Reject(RequestContext context)
    {
        var error = ErrorBuilder.New()
            .SetMessage("Only trusted (persisted) GraphQL documents are allowed on this gateway.")
            .SetCode(RejectionCode)
            .Build();
        context.Result = OperationResult.FromError(error);
    }

    private static bool TryGetAppHeader(RequestContext context, out Microsoft.Extensions.Primitives.StringValues values, out HttpContext httpContext)
    {
        values = default;
        httpContext = null!;
        var options = context.RequestServices.GetService<IOptions<GatewayOptions>>()?.Value.GraphQL.AppScopedTrustedDocuments;
        if (options is not { Enabled: true })
        {
            return false;
        }

        HttpContext? resolved = null;
        if (context.ContextData.TryGetValue("HttpContext", out var hcObj) && hcObj is HttpContext hc)
        {
            resolved = hc;
        }
        else
        {
            resolved = context.RequestServices.GetService<IHttpContextAccessor>()?.HttpContext;
        }

        if (resolved is null || !resolved.Request.Headers.TryGetValue(options.HeaderName, out values))
        {
            return false;
        }

        httpContext = resolved;
        return true;
    }

    private static async ValueTask<bool> IsAppRequestTrustedAsync(RequestContext context, HttpContext httpContext, Microsoft.Extensions.Primitives.StringValues headerValues)
    {
        var logger = context.RequestServices.GetService<ILoggerFactory>()?.CreateLogger<TrustedDocumentsOnlyMiddleware>();
        bool Fail(AppRejectionReason reason, string app = "-")
        {
            if (logger is not null && logger.IsEnabled(LogLevel.Debug))
            {
                LogAppRejection(logger, reason, app, null);
            }

            return false;
        }

        if (!AppIdentityHeader.TryParse(headerValues, out var app))
        {
            return Fail(AppRejectionReason.MalformedHeader);
        }

        var request = context.Request;
        if (request.DocumentId is { IsEmpty: false } || request.DocumentHash is { IsEmpty: false } || HasPersistedQueryExtension(request.Extensions?.Document))
        {
            return Fail(AppRejectionReason.UnsupportedRequestShape, app.ToString());
        }

        // SEC-281-3: exactly one Origin value, normalized, exact match; no Referer/Host fallback, no non-browser exemption.
        var origins = httpContext.Request.Headers.Origin;
        if (origins.Count != 1 || !AllowedOrigin.TryNormalize(origins[0], out var origin))
        {
            return Fail(AppRejectionReason.OriginRejected, app.ToString());
        }

        // SEC-281-1: only source text is hashable; a pre-parsed document without text is never trusted.
        if (request.Document is null || request.Document is IOperationDocumentNodeProvider)
        {
            return Fail(AppRejectionReason.NoSourceText, app.ToString());
        }

        var registry = context.RequestServices.GetService<ITrustedDocumentAppRegistry>();
        if (registry is null)
        {
            return Fail(AppRejectionReason.RegistryError, app.ToString());
        }

        TrustedDocumentAppSnapshot snapshot;
        try
        {
            snapshot = await registry.GetAsync(context.RequestAborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return Fail(AppRejectionReason.RegistryError, app.ToString());
        }

        // The hashed bytes are the bytes the parser consumes (no re-read, no trimming, no normalization).
        var hash = TrustedDocumentHash.ComputeExact(request.Document.AsSpan());
        if (!snapshot.IsOriginAllowed(app.AppId, origin) || !snapshot.IsDocumentTrusted(app, hash))
        {
            return Fail(AppRejectionReason.NotTrusted, app.ToString());
        }

        return true;
    }

    private static bool HasPersistedQueryExtension(JsonDocument? extensions) =>
        extensions is { RootElement.ValueKind: JsonValueKind.Object } && extensions.RootElement.TryGetProperty("persistedQuery", out _);
}
