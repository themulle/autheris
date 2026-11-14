namespace Autheris.Api.Middleware;

using System;
using System.Buffers;
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
    private static readonly TrustedDocumentNormalizedHashCache SharedHashCache = new();

    private readonly HashSet<string> _hashes = new(StringComparer.Ordinal);
    private readonly TrustedDocumentNormalizedHashCache _hashCache;

    public TrustedDocumentStore(IEnumerable<string> documents)
        : this(documents, SharedHashCache)
    {
    }

    internal TrustedDocumentStore(IEnumerable<string> documents, TrustedDocumentNormalizedHashCache hashCache)
    {
        ArgumentNullException.ThrowIfNull(documents);
        _hashCache = hashCache ?? throw new ArgumentNullException(nameof(hashCache));
        foreach (var document in documents)
        {
            if (!string.IsNullOrWhiteSpace(document))
            {
                _hashes.Add(ComputeHashCore(document, _hashCache));
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
        return ComputeHashCore(documentText, SharedHashCache);
    }

    public static string ComputeHash(ReadOnlySpan<byte> utf8Document) =>
        SharedHashCache.GetOrComputeNormalizedHash(utf8Document);

    public static string ComputeHash(DocumentNode document) =>
        TrustedDocumentNormalizedHashCache.ComputeNormalizedHash(document);

    public bool IsTrusted(DocumentNode document) => _hashes.Contains(ComputeHash(document));

    public bool IsTrusted(ReadOnlySpan<byte> utf8Document)
    {
        try
        {
            return _hashes.Contains(_hashCache.GetOrComputeNormalizedHash(utf8Document));
        }
        catch (SyntaxException)
        {
            return false;
        }
    }

    public bool IsTrusted(string documentText)
    {
        ArgumentNullException.ThrowIfNull(documentText);
        try
        {
            return _hashes.Contains(ComputeHashCore(documentText, _hashCache));
        }
        catch (SyntaxException)
        {
            return false;
        }
    }

    private static string ComputeHashCore(string documentText, TrustedDocumentNormalizedHashCache hashCache)
    {
        int maxBytes = Encoding.UTF8.GetMaxByteCount(documentText.Length);
        byte[]? rented = null;
        Span<byte> buffer = maxBytes <= 1_024
            ? stackalloc byte[1_024]
            : (rented = ArrayPool<byte>.Shared.Rent(maxBytes));
        try
        {
            int written = Encoding.UTF8.GetBytes(documentText, buffer);
            return hashCache.GetOrComputeNormalizedHash(buffer[..written]);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
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
    /// <summary>Error code of the rejection result; shared with the readiness executor gate.</summary>
    public const string RejectionCode = "TRUSTED_DOCUMENT_REQUIRED";

    private readonly RequestDelegate _next;

    public TrustedDocumentsOnlyMiddleware(RequestDelegate next)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
    }

    /// <summary>Why an app-scoped request was rejected. Only logged (Debug), never returned to the caller.</summary>
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
            // header present means app path only; no failure falls back to the global store.
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

        if (doc is not null)
        {
            var store = context.RequestServices.GetService<TrustedDocumentStore>();
            if (store is null || !store.IsTrusted(doc))
            {
                Reject(context);
                return;
            }

            await _next(context).ConfigureAwait(false);
            return;
        }

        if (context.Request.Document is null)
        {
            // No document supplied (id-only request). Without a document store the parser stage fails closed.
            await _next(context).ConfigureAwait(false);
            return;
        }

        var rawBytes = context.Request.Document.AsSpan();
        if (IsUtf8WhiteSpace(rawBytes))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var globalStore = context.RequestServices.GetService<TrustedDocumentStore>();
        if (globalStore is null || !globalStore.IsTrusted(rawBytes))
        {
            Reject(context);
            return;
        }

        await _next(context).ConfigureAwait(false);
    }

    private static bool IsUtf8WhiteSpace(ReadOnlySpan<byte> bytes)
    {
        foreach (byte b in bytes)
        {
            if (b >= 0x80)
            {
                return string.IsNullOrWhiteSpace(Encoding.UTF8.GetString(bytes));
            }

            if (b is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)'\f' or (byte)'\v'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The single rejection result of both paths: identical message, code and shape.</summary>
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

        // exactly one Origin value, normalized, exact match; no Referer/Host fallback, no non-browser exemption.
        var origins = httpContext.Request.Headers.Origin;
        if (origins.Count != 1 || !AllowedOrigin.TryNormalize(origins[0], out var origin))
        {
            return Fail(AppRejectionReason.OriginRejected, app.ToString());
        }

        // only source text is hashable; a pre-parsed document without text is never trusted.
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
