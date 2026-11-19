namespace Autheris.Modules.Abstractions;

/// <summary>
/// Error codes modules use in problem responses. Each value equals the host constant of the same name
/// (enforced by an architecture test).
/// </summary>
public static class ModuleErrorCodes
{
    public const string BadRequest = "BAD_REQUEST";
    public const string ResourceNotFound = "RESOURCE_NOT_FOUND";
    public const string ServiceUnavailable = "SERVICE_UNAVAILABLE";
    public const string AccessDenied = "ACCESS_DENIED";
    public const string InvalidJson = "INVALID_JSON";
    public const string MissingRequiredField = "MISSING_REQUIRED_FIELD";
    public const string PayloadTooLarge = "PAYLOAD_TOO_LARGE";
}
