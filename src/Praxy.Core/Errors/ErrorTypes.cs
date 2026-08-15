namespace Praxy.Core.Errors;

/// <summary>
/// Machine-readable error <c>type</c> strings. These are public API: SDKs switch on them,
/// so they are snake_case, registered in <see cref="All"/>, unit-tested, and never reworded casually.
/// </summary>
public static class ErrorTypes
{
    public const string GeneralServerError = "general_server_error";
    public const string GeneralRouteNotFound = "general_route_not_found";
    public const string GeneralArgumentInvalid = "general_argument_invalid";
    public const string GeneralUnauthorized = "general_unauthorized";
    public const string GeneralRateLimitExceeded = "general_rate_limit_exceeded";

    public const string InstanceAlreadyClaimed = "instance_already_claimed";
    public const string InstanceSetupTokenInvalid = "instance_setup_token_invalid";

    public const string UserInvalidCredentials = "user_invalid_credentials";
    public const string UserAlreadyExists = "user_already_exists";
    public const string UserSessionNotFound = "user_session_not_found";

    public const string ProjectNotFound = "project_not_found";
    public const string ProjectAlreadyExists = "project_already_exists";
    public const string ProjectInvalidId = "project_invalid_id";
    public const string ProjectReserved = "project_reserved";

    /// <summary>Every registered type. The unit test asserting <c>^[a-z0-9_]+$</c> walks this list.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        GeneralServerError,
        GeneralRouteNotFound,
        GeneralArgumentInvalid,
        GeneralUnauthorized,
        GeneralRateLimitExceeded,
        InstanceAlreadyClaimed,
        InstanceSetupTokenInvalid,
        UserInvalidCredentials,
        UserAlreadyExists,
        UserSessionNotFound,
        ProjectNotFound,
        ProjectAlreadyExists,
        ProjectInvalidId,
        ProjectReserved,
    ];
}
