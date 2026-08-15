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
    public const string GeneralUnauthorizedScope = "general_unauthorized_scope";
    public const string GeneralRateLimitExceeded = "general_rate_limit_exceeded";
    public const string GeneralUnknownOrigin = "general_unknown_origin";

    public const string InstanceAlreadyClaimed = "instance_already_claimed";
    public const string InstanceSetupTokenInvalid = "instance_setup_token_invalid";

    public const string UserInvalidCredentials = "user_invalid_credentials";
    public const string UserAlreadyExists = "user_already_exists";
    public const string UserSessionNotFound = "user_session_not_found";
    public const string UserNotFound = "user_not_found";
    public const string UserBlocked = "user_blocked";
    public const string UserInvalidToken = "user_invalid_token";
    public const string UserOauth2ProviderError = "user_oauth2_provider_error";

    public const string TeamNotFound = "team_not_found";
    public const string TeamInvalidSecret = "team_invalid_secret";
    public const string MembershipNotFound = "membership_not_found";
    public const string MembershipAlreadyExists = "membership_already_exists";
    public const string MembershipAlreadyConfirmed = "membership_already_confirmed";

    public const string ApiKeyNotFound = "api_key_not_found";
    public const string PlatformNotFound = "platform_not_found";

    public const string ProjectNotFound = "project_not_found";
    public const string ProjectAlreadyExists = "project_already_exists";
    public const string ProjectInvalidId = "project_invalid_id";
    public const string ProjectReserved = "project_reserved";
    public const string ProjectAuthMethodDisabled = "project_auth_method_disabled";
    public const string ProjectProviderDisabled = "project_provider_disabled";

    /// <summary>Every registered type. The unit test asserting <c>^[a-z0-9_]+$</c> walks this list.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        GeneralServerError,
        GeneralRouteNotFound,
        GeneralArgumentInvalid,
        GeneralUnauthorized,
        GeneralUnauthorizedScope,
        GeneralRateLimitExceeded,
        GeneralUnknownOrigin,
        InstanceAlreadyClaimed,
        InstanceSetupTokenInvalid,
        UserInvalidCredentials,
        UserAlreadyExists,
        UserSessionNotFound,
        UserNotFound,
        UserBlocked,
        UserInvalidToken,
        UserOauth2ProviderError,
        TeamNotFound,
        TeamInvalidSecret,
        MembershipNotFound,
        MembershipAlreadyExists,
        MembershipAlreadyConfirmed,
        ApiKeyNotFound,
        PlatformNotFound,
        ProjectNotFound,
        ProjectAlreadyExists,
        ProjectInvalidId,
        ProjectReserved,
        ProjectAuthMethodDisabled,
        ProjectProviderDisabled,
    ];
}
