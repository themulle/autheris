namespace Autheris.Api.Errors;

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

/// <summary>
/// Stable, machine-readable error codes for RFC 7807 <c>application/problem+json</c> responses (WP-ARC-C-05).
/// </summary>
public static class ApiErrorCodes
{
    public const string BadRequest = "BAD_REQUEST";
    public const string InvalidQuery = "INVALID_QUERY";
    public const string InvalidDomainScope = "INVALID_DOMAIN_SCOPE";
    public const string InvalidTenantId = "INVALID_TENANT_ID";
    public const string InvalidJson = "INVALID_JSON";
    public const string MissingRequiredField = "MISSING_REQUIRED_FIELD";
    public const string RequestTimeout = "REQUEST_TIMEOUT";
    public const string UriTooLong = "URI_TOO_LONG";
    public const string RequestHeaderFieldsTooLarge = "REQUEST_HEADER_FIELDS_TOO_LARGE";
    public const string PayloadTooLarge = "PAYLOAD_TOO_LARGE";
    public const string UnsupportedMediaType = "UNSUPPORTED_MEDIA_TYPE";
    public const string NotAcceptable = "NOT_ACCEPTABLE";
    public const string Unauthorized = "UNAUTHORIZED";
    public const string AccessDenied = "ACCESS_DENIED";
    public const string CrossTenantAccessForbidden = "CROSS_TENANT_ACCESS_FORBIDDEN";
    public const string ConsentTenantInvalid = "CONSENT_TENANT_INVALID";
    public const string ReadOnlyToken = "READ_ONLY_TOKEN";
    public const string CsrfValidationFailed = "CSRF_VALIDATION_FAILED";
    public const string UntrustedOrigin = "UNTRUSTED_ORIGIN";
    public const string ResourceNotFound = "RESOURCE_NOT_FOUND";
    public const string Conflict = "CONFLICT";
    public const string UnprocessableEntity = "UNPROCESSABLE_ENTITY";
    public const string TooManyRequests = "TOO_MANY_REQUESTS";
    public const string InternalError = "INTERNAL_ERROR";
    public const string NotImplemented = "NOT_IMPLEMENTED";
    public const string ServiceUnavailable = "SERVICE_UNAVAILABLE";
    public const string GatewayTimeout = "GATEWAY_TIMEOUT";

    // Domain / feature specific codes
    public const string WebhookPayloadInvalid = "WEBHOOK_PAYLOAD_INVALID";
    public const string WebhookSignatureOrFormatInvalid = "WEBHOOK_SIGNATURE_OR_FORMAT_INVALID";
    public const string CdcFilterInvalid = "CDC_FILTER_INVALID";
    public const string CdcTargetUrlNotAllowed = "CDC_TARGET_URL_NOT_ALLOWED";
    public const string CdcCustomHeaderNotAllowed = "CDC_CUSTOM_HEADER_NOT_ALLOWED";
    public const string CdcSecretRequired = "CDC_SECRET_REQUIRED";
    public const string CdcSecretStoreUnavailable = "CDC_SECRET_STORE_UNAVAILABLE";
    public const string CdcSnapshotTooLarge = "CDC_SNAPSHOT_TOO_LARGE";
    public const string CdcStoreBusy = "CDC_STORE_BUSY";
    public const string CdcSubscriptionQuotaExceeded = "CDC_SUBSCRIPTION_QUOTA_EXCEEDED";
    public const string AccessProfilePlainSubjectsRemoved = "ACCESS_PROFILE_PLAIN_SUBJECTS_REMOVED";
    public const string RowFilterSyntaxInvalid = "ROW_FILTER_SYNTAX_INVALID";
    public const string DbtProposalConflict = "DBT_PROPOSAL_CONFLICT";
    public const string DbtManifestInvalid = "DBT_MANIFEST_INVALID";
    public const string RequiredFilterColumnMissing = "REQUIRED_FILTER_COLUMN_MISSING";
    public const string InvalidSecretRef = "INVALID_SECRET_REF";
    public const string DataContractInvalid = "DATA_CONTRACT_INVALID";
    public const string OlapQueryFailed = "OLAP_QUERY_FAILED";
    public const string OlapResultTooLarge = "OLAP_RESULT_TOO_LARGE";
    public const string PrivacyBudgetExhausted = "PRIVACY_BUDGET_EXHAUSTED";
    public const string GovernancePlanInvalid = "GOVERNANCE_PLAN_INVALID";
    public const string ProcedureArgumentInvalid = "PROCEDURE_ARGUMENT_INVALID";
    public const string VirtualFilterConflict = "VIRTUAL_FILTER_CONFLICT";
    public const string ResourceLocked = "RESOURCE_LOCKED";
    public const string VirtualFilterInvalid = "VIRTUAL_FILTER_INVALID";

    // O-5b step 2 (WP-0): codes of the migrated bare error bodies. Registered here once so no later work package touches this file.
    public const string ProcedureRejected = "PROCEDURE_REJECTED";
    public const string TenancyModeClaimlessAdmin = "TENANCY_MODE_CLAIMLESS_ADMIN";
    public const string InvalidTag = "INVALID_TAG";
    public const string InvalidVisibility = "INVALID_VISIBILITY";
    public const string TagLimitExceeded = "TAG_LIMIT_EXCEEDED";
    public const string OffsetNotSupported = "OFFSET_NOT_SUPPORTED";
    public const string KeysetStreamingUnavailable = "KEYSET_STREAMING_UNAVAILABLE";
    public const string InvalidCursor = "INVALID_CURSOR";
    public const string DataOwnerApprovalRequired = "DATA_OWNER_APPROVAL_REQUIRED";
    public const string InvalidGranteeType = "INVALID_GRANTEE_TYPE";
    public const string InvalidTable = "INVALID_TABLE";
    public const string ApiVersionSunset = "API_VERSION_SUNSET";
    public const string ApiVersionUpgradeRequired = "API_VERSION_UPGRADE_REQUIRED";
    public const string InvalidRebacSubject = "INVALID_REBAC_SUBJECT";
    public const string IndividualGrantExceedsMaximum = "INDIVIDUAL_GRANT_EXCEEDS_MAXIMUM";

    private static readonly Regex CodeFormat = new("^[A-Z][A-Z0-9_]{2,63}$", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(50));

    // Registered codes: this class plus the stable code sets of the domain/application layers that surface through dynamic sources
    // (mapped.Code, ex.Code, decision.ErrorCode). Anything else is never echoed (NL-SC-27).
    private static readonly HashSet<string> Registered = BuildRegistry();

    /// <summary>True when the code matches the governed format and is a registered code.</summary>
    public static bool IsGoverned(string? code) =>
        !string.IsNullOrEmpty(code) && code.Length <= 64 && CodeFormat.IsMatch(code) && Registered.Contains(code);

    /// <summary>The code used when a caller supplies an ungoverned code: the status default.</summary>
    public static string StatusDefault(int statusCode) => statusCode switch
    {
        StatusCodes.Status400BadRequest => BadRequest,
        StatusCodes.Status401Unauthorized => Unauthorized,
        StatusCodes.Status403Forbidden => AccessDenied,
        StatusCodes.Status404NotFound => ResourceNotFound,
        StatusCodes.Status409Conflict => Conflict,
        StatusCodes.Status413PayloadTooLarge => PayloadTooLarge,
        StatusCodes.Status415UnsupportedMediaType => UnsupportedMediaType,
        StatusCodes.Status422UnprocessableEntity => UnprocessableEntity,
        StatusCodes.Status429TooManyRequests => TooManyRequests,
        StatusCodes.Status501NotImplemented => NotImplemented,
        StatusCodes.Status503ServiceUnavailable => ServiceUnavailable,
        StatusCodes.Status504GatewayTimeout => GatewayTimeout,
        >= 500 => InternalError,
        _ => BadRequest
    };

    /// <summary>The code to emit for <paramref name="code"/>: itself when governed, else the status default.</summary>
    public static string Resolve(int statusCode, string? code) => IsGoverned(code) ? code! : StatusDefault(statusCode);

    private static HashSet<string> BuildRegistry()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in new[]
        {
            typeof(ApiErrorCodes),
            typeof(TrinoSqlEngine.Ast.Emit.GovernedSqlErrorCodes),
            typeof(Autheris.Domain.Model.CdcActivationCodes),
            typeof(Autheris.Application.Governance.Grants.GrantErrorCodes),
            typeof(Autheris.Application.Data.Changes.ChangeDetectionErrorCodes),
            typeof(Autheris.Application.VirtualFilters.Federation.VirtualFilterErrorCodes)
        })
        {
            foreach (var field in type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
            {
                if (field is { IsLiteral: true, IsInitOnly: false } && field.FieldType == typeof(string) && field.GetRawConstantValue() is string value)
                {
                    set.Add(value);
                }
            }
        }

        return set;
    }
}
