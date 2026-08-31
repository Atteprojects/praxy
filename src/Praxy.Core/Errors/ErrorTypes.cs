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

    public const string OrganizationNotFound = "organization_not_found";

    public const string ApiKeyNotFound = "api_key_not_found";
    public const string PlatformNotFound = "platform_not_found";

    public const string ProjectNotFound = "project_not_found";
    public const string ProjectAlreadyExists = "project_already_exists";
    public const string ProjectInvalidId = "project_invalid_id";
    public const string ProjectReserved = "project_reserved";
    public const string ProjectAuthMethodDisabled = "project_auth_method_disabled";
    public const string ProjectProviderDisabled = "project_provider_disabled";

    public const string DatabaseNotFound = "database_not_found";
    public const string DatabaseAlreadyExists = "database_already_exists";
    public const string TableNotFound = "table_not_found";
    public const string TableAlreadyExists = "table_already_exists";
    public const string ColumnNotFound = "column_not_found";
    public const string ColumnAlreadyExists = "column_already_exists";
    public const string ColumnInvalid = "column_invalid";
    public const string IndexNotFound = "index_not_found";
    public const string IndexAlreadyExists = "index_already_exists";
    public const string IndexInvalid = "index_invalid";
    public const string IndexDependency = "index_dependency";
    public const string SchemaJobNotFound = "schema_job_not_found";
    public const string SchemaJobInvalidState = "schema_job_invalid_state";
    public const string RowSizeExceeded = "row_size_exceeded";
    public const string GeneralForceRequired = "general_force_required";
    public const string GeneralResourceLimitExceeded = "general_resource_limit_exceeded";

    public const string RowNotFound = "row_not_found";
    public const string RowAlreadyExists = "row_already_exists";
    public const string RowInvalidStructure = "row_invalid_structure";
    public const string GeneralQueryInvalid = "general_query_invalid";

    public const string WebhookNotFound = "webhook_not_found";
    public const string WebhookDeliveryNotFound = "webhook_delivery_not_found";
    public const string WebhookInvalidRedeliverState = "webhook_invalid_redeliver_state";

    public const string FunctionNotFound = "function_not_found";
    public const string FunctionAlreadyExists = "function_already_exists";
    public const string FunctionInvalid = "function_invalid";
    public const string FunctionDisabled = "function_disabled";
    public const string FunctionEnvVarNotFound = "function_env_var_not_found";
    public const string FunctionDeploymentNotFound = "function_deployment_not_found";
    public const string FunctionInvalidDeploymentState = "function_invalid_deployment_state";
    public const string FunctionInvalidSource = "function_invalid_source";
    public const string FunctionNoActiveDeployment = "function_no_active_deployment";
    public const string FunctionExecutionNotFound = "function_execution_not_found";
    public const string FunctionExecutionTimeout = "function_execution_timeout";
    public const string FunctionExecutionFailed = "function_execution_failed";
    public const string FunctionGitRepositoryInvalid = "function_git_repository_invalid";

    public const string SiteNotFound = "site_not_found";
    public const string SiteAlreadyExists = "site_already_exists";
    public const string SiteInvalid = "site_invalid";
    public const string SiteEnvVarNotFound = "site_env_var_not_found";
    public const string SiteDeploymentNotFound = "site_deployment_not_found";
    public const string SiteInvalidDeploymentState = "site_invalid_deployment_state";
    public const string SiteInvalidSource = "site_invalid_source";
    public const string SiteNoActiveDeployment = "site_no_active_deployment";
    public const string SiteDomainNotFound = "site_domain_not_found";
    public const string SiteDomainAlreadyExists = "site_domain_already_exists";
    public const string SiteDomainInvalid = "site_domain_invalid";
    public const string SiteGitRepositoryInvalid = "site_git_repository_invalid";

    public const string VcsGithubNotConfigured = "vcs_github_not_configured";
    public const string VcsGithubInstallationRequired = "vcs_github_installation_required";
    public const string VcsGithubInstallationNotFound = "vcs_github_installation_not_found";
    public const string VcsGithubRepositoryInaccessible = "vcs_github_repository_inaccessible";
    public const string VcsWebhookInvalidSignature = "vcs_webhook_invalid_signature";

    public const string MessagingProviderNotFound = "messaging_provider_not_found";
    public const string MessagingProviderInvalid = "messaging_provider_invalid";
    public const string MessagingTopicNotFound = "messaging_topic_not_found";
    public const string MessagingTopicAlreadyExists = "messaging_topic_already_exists";
    public const string MessagingSubscriberAlreadyExists = "messaging_subscriber_already_exists";
    public const string MessagingSubscriberNotFound = "messaging_subscriber_not_found";
    public const string MessagingMessageNotFound = "messaging_message_not_found";
    public const string MessagingMessageInvalid = "messaging_message_invalid";
    public const string MessagingTemplateInvalid = "messaging_template_invalid";

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
        OrganizationNotFound,
        ApiKeyNotFound,
        PlatformNotFound,
        ProjectNotFound,
        ProjectAlreadyExists,
        ProjectInvalidId,
        ProjectReserved,
        ProjectAuthMethodDisabled,
        ProjectProviderDisabled,
        DatabaseNotFound,
        DatabaseAlreadyExists,
        TableNotFound,
        TableAlreadyExists,
        ColumnNotFound,
        ColumnAlreadyExists,
        ColumnInvalid,
        IndexNotFound,
        IndexAlreadyExists,
        IndexInvalid,
        IndexDependency,
        SchemaJobNotFound,
        SchemaJobInvalidState,
        RowSizeExceeded,
        GeneralForceRequired,
        GeneralResourceLimitExceeded,
        RowNotFound,
        RowAlreadyExists,
        RowInvalidStructure,
        GeneralQueryInvalid,
        WebhookNotFound,
        WebhookDeliveryNotFound,
        WebhookInvalidRedeliverState,
        FunctionNotFound,
        FunctionAlreadyExists,
        FunctionInvalid,
        FunctionDisabled,
        FunctionEnvVarNotFound,
        FunctionDeploymentNotFound,
        FunctionInvalidDeploymentState,
        FunctionInvalidSource,
        FunctionNoActiveDeployment,
        FunctionExecutionNotFound,
        FunctionExecutionTimeout,
        FunctionExecutionFailed,
        FunctionGitRepositoryInvalid,
        SiteNotFound,
        SiteAlreadyExists,
        SiteInvalid,
        SiteEnvVarNotFound,
        SiteDeploymentNotFound,
        SiteInvalidDeploymentState,
        SiteInvalidSource,
        SiteNoActiveDeployment,
        SiteDomainNotFound,
        SiteDomainAlreadyExists,
        SiteDomainInvalid,
        SiteGitRepositoryInvalid,
        VcsGithubNotConfigured,
        VcsGithubInstallationRequired,
        VcsGithubInstallationNotFound,
        VcsGithubRepositoryInaccessible,
        VcsWebhookInvalidSignature,
        MessagingProviderNotFound,
        MessagingProviderInvalid,
        MessagingTopicNotFound,
        MessagingTopicAlreadyExists,
        MessagingSubscriberAlreadyExists,
        MessagingSubscriberNotFound,
        MessagingMessageNotFound,
        MessagingMessageInvalid,
        MessagingTemplateInvalid,
    ];
}
