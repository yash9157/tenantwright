namespace Notifications.Domain.Shared.Constants;

/// <summary>
/// Stable caller-safe error codes for Notifications public operations.
/// </summary>
public static class NotificationErrorCodes
{
    public const string NotificationIdRequired = "notification_id_required";
    public const string ReplayOperationIdRequired = "notification_replay_operation_id_required";
    public const string NotificationNotFound = "notification_not_found";
    public const string DeliveryIdRequired = "notification_delivery_id_required";
    public const string DeliveryNotFound = "notification_delivery_not_found";

    /// <summary>A host lookup by delivery id matched rows in more than one tenant database; the tenant is required.</summary>
    public const string DeliveryTenantRequired = "notification_delivery_tenant_required";
    public const string DeliveryNotFailed = "notification_delivery_not_failed";
    public const string DeliveryNotReviewable = "notification_delivery_not_reviewable";
    public const string DeliveryNotConfirmable = "notification_delivery_not_confirmable";
    public const string NoFailedDeliveries = "notification_no_failed_deliveries";
    public const string NoReplayRecipients = "notification_no_replay_recipients";
    public const string OverrideRecipientsRequired = "notification_override_recipients_required";
    public const string TemplateModelInvalid = "notification_template_model_invalid";

    public const string AudienceRoleRequired = "notification_audience_role_required";
    public const string GroupChannelInvalid = "notification_group_channel_invalid";
    public const string AudienceTenantRequired = "notification_audience_tenant_required";
    public const string GroupRecipientInvalid = "notification_group_recipient_invalid";
    public const string AudienceLimitExceeded = "notification_audience_limit_exceeded";
    public const string TemplateRequired = "notification_template_required";
    public const string RecipientsRequired = "notification_recipients_required";
    public const string ValidRecipientRequired = "notification_valid_recipient_required";
    public const string TenantRequired = "notification_tenant_required";
    public const string ScheduleUtcRequired = "notification_schedule_utc_required";
    public const string ScheduleMustBeFuture = "notification_schedule_must_be_future";
    public const string ScheduleHorizonExceeded = "notification_schedule_horizon_exceeded";
    public const string RecipientChannelMismatch = "notification_recipient_channel_mismatch";
    public const string PushProviderTypeImmutable = "notification_push_provider_type_immutable";
    public const string ApnsEnvironmentImmutable = "notification_apns_environment_immutable";
    public const string ProviderConfigInactive = "notification_provider_config_inactive";
    public const string DefaultProviderDeleteBlocked = "notification_default_provider_delete_blocked";
    public const string ProviderConfigChannelMismatch = "notification_provider_config_channel_mismatch";
    public const string ProviderConfigNameExists = "notification_provider_config_name_exists";
    public const string PolicyPreviewTimeNotUtc = "notification_policy_preview_time_not_utc";
    public const string DigestNotAllowed = "notification_digest_not_allowed";
    public const string QuietHoursFloorRequired = "notification_quiet_hours_floor_required";
    public const string DigestFrequencyNotAllowed = "notification_digest_frequency_not_allowed";
    public const string TemplateNotDraftOnly = "notification_template_not_draft_only";
    public const string TemplateNameReserved = "notification_template_name_reserved";
    public const string TemplateNameExists = "notification_template_name_exists";
    public const string TemplateNameRequired = "notification_template_name_required";
    public const string TemplateNotFound = "notification_template_not_found";
    public const string TemplateRevisionNotFound = "notification_template_revision_not_found";
    public const string TemplateSampleNotFound = "notification_template_sample_not_found";
    public const string TemplateSampleNameExists = "notification_template_sample_name_exists";
    public const string TemplateContentBlockNotFound = "notification_template_content_block_not_found";
    public const string TemplateContentBlockKeyExists = "notification_template_content_block_key_exists";
    public const string UserNotificationNotFound = "notification_user_notification_not_found";
}
