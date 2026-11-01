namespace Shared;

/// <summary>
/// Stable lower-snake-case codes for errors that are safe to expose to API callers.
/// </summary>
public static class SharedErrorCodes
{
    public const string RequestFailed = "request_failed";
    public const string ValidationFailed = "validation_failed";
    public const string Conflict = "conflict";
    public const string Unauthorized = "unauthorized";
    public const string Forbidden = "forbidden";
    public const string ResourceNotFound = "resource_not_found";
    public const string ConcurrencyConflict = "concurrency_conflict";
    public const string UniqueConstraintViolation = "unique_constraint_violation";
    public const string ForeignKeyConflict = "foreign_key_conflict";
    public const string CheckConstraintViolation = "check_constraint_violation";
    public const string TransactionConflict = "transaction_conflict";
    public const string RequestTimeout = "request_timeout";
    public const string RequestCancelled = "request_cancelled";
    public const string RateLimitExceeded = "rate_limit_exceeded";
    public const string RequestHeaderFieldsTooLarge = "request_header_fields_too_large";
    public const string PayloadTooLarge = "payload_too_large";
    public const string UpstreamFailure = "upstream_failure";
    public const string UpstreamUnavailable = "upstream_unavailable";
    public const string UpstreamTimeout = "upstream_timeout";
    public const string InternalError = "internal_error";

    /// <summary>
    /// An offset page of a cross-tenant view that spans dedicated tenant databases lies beyond the bounded merge window;
    /// the caller must narrow the filters instead of paging deeper.
    /// </summary>
    public const string CrossDatabasePageTooDeep = "cross_database_page_too_deep";
}
