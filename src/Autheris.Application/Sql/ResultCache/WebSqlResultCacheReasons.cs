namespace Autheris.Application.Sql.ResultCache;

/// <summary>Fixed reason codes of the WebSQL result cache. They are the only values allowed in audit fields, responses and metric labels.</summary>
public static class WebSqlResultCacheReasons
{
    public const string SourceDisabled = "source_disabled";
    public const string NotReadOnly = "not_read_only";
    public const string RecursiveCte = "recursive_cte";
    public const string VolatileFunction = "volatile_function";
    public const string TableNotListed = "table_not_listed";
    public const string DelegatedSource = "delegated_source";
    public const string PerUserCredentials = "per_user_credentials";
    public const string CallerNotBindable = "caller_not_bindable";
    public const string PagingUnavailable = "paging_unavailable";
    public const string UnkeyableValue = "unkeyable_value";
    public const string NoConnection = "no_connection";
    public const string EpochUnavailable = "epoch_unavailable";
    public const string Incomplete = "incomplete";
    public const string TooLarge = "too_large";
    public const string UnsupportedValueType = "unsupported_value_type";
    public const string GovernanceChanged = "governance_changed";
    public const string CrossSource = "cross_source";
    public const string UnsupportedClause = "unsupported_clause";
}
