namespace Autheris.Api.Errors;

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
    public const string PayloadTooLarge = "PAYLOAD_TOO_LARGE";
    public const string UnsupportedMediaType = "UNSUPPORTED_MEDIA_TYPE";
    public const string NotAcceptable = "NOT_ACCEPTABLE";
    public const string Unauthorized = "UNAUTHORIZED";
    public const string AccessDenied = "ACCESS_DENIED";
    public const string CrossTenantAccessForbidden = "CROSS_TENANT_ACCESS_FORBIDDEN";
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
    public const string RowFilterSyntaxInvalid = "ROW_FILTER_SYNTAX_INVALID";
    public const string DbtProposalConflict = "DBT_PROPOSAL_CONFLICT";
    public const string DbtManifestInvalid = "DBT_MANIFEST_INVALID";
    public const string RequiredFilterColumnMissing = "REQUIRED_FILTER_COLUMN_MISSING";
}
