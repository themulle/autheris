namespace Autheris.Domain.Plugins.Auth;

public static class AuthPluginAuditEventTypes
{
    public const string RoleEscalationAttempt = "SECURITY_ALERT_ROLE_ESCALATION_ATTEMPT";
    public const string SystemSidAttempt = "SECURITY_ALERT_SYSTEM_SID_ATTEMPT";
    public const string AuthPluginDecision = "AUTH_PLUGIN_DECISION";
    public const string AuthPluginError = "AUTH_PLUGIN_ERROR";
}
