using System.Reflection;
using System.Text.Json;
using Shared.EventContracts;
using Shared.Events;
using Xunit;

namespace Shared.EventContracts.Tests;

public sealed class ContractSerializationTests
{
    [Fact]
    public void Every_concrete_contract_should_round_trip_with_system_text_json()
    {
        var contractTypes = typeof(AuditLogWriteRequestedTenantEto).Assembly
            .GetTypes()
            .Where(type => type.IsClass && !type.IsAbstract)
            .Where(type => type.IsDefined(typeof(DistributedContractAttribute), inherit: false));

        foreach (var contractType in contractTypes)
        {
            var instance = Activator.CreateInstance(contractType)
                ?? throw new InvalidOperationException($"Could not construct '{contractType.FullName}'.");
            var json = JsonSerializer.Serialize(instance, contractType);
            var roundTripped = JsonSerializer.Deserialize(json, contractType);

            Assert.NotNull(roundTripped);
            Assert.IsType(contractType, roundTripped);
        }
    }



    [Fact]
    public void Notification_tenant_wire_snapshot_should_remain_stable()
    {
        var contract = new NotificationSendRequestedTenantEto
        {
            RequestId = Guid.Parse("00000000-0000-0000-0000-000000000010"),
            TenantId = Guid.Parse("00000000-0000-0000-0000-000000000011"),
            Category = "security",
            Priority = NotificationSendPriority.High,
            ActionUrl = "/sessions",
            OccurredAtUtc = new DateTime(2026, 7, 25, 10, 0, 0, DateTimeKind.Utc),
            ScheduledForUtc = null,
            SourceService = "identity",
            RespectUserPreferences = false,
            PreferenceKey = null,
            DeliveryPlan = new NotificationDeliveryPlan
            {
                Mode = NotificationDeliveryMode.Parallel,
                Steps = [new NotificationChannelPlan
                    {
                        Channel = NotificationSendChannel.App,
                        Subject = "Session revoked",
                        TemplateName = "Identity.SessionRevoked",
                        TemplateModel = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["action"] = "admin-revoke"
            }
                    }]
            },
            Recipients = ((IEnumerable<NotificationRecipientEto>)[
                new NotificationRecipientEto
                {
                    RecipientType = NotificationSendRecipientType.UserId,
                    RecipientValue = "00000000-0000-0000-0000-000000000012"
                }
            ]).Select((recipient, index) => new NotificationRecipientPlan
            {
                Key = index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                PreferenceUserId = null,
                Destinations = [new NotificationDestination
                {
                    Channel = NotificationSendChannel.App,
                    RecipientType = recipient.RecipientType,
                    RecipientValue = recipient.RecipientValue
                }]
            }).ToList(),
        };

        var json = JsonSerializer.Serialize(contract);

        Assert.Equal(
            "{\"TenantId\":\"00000000-0000-0000-0000-000000000011\",\"RequestId\":\"00000000-0000-0000-0000-000000000010\",\"DeliveryPlan\":{\"Mode\":1,\"Steps\":[{\"Channel\":4,\"TemplateName\":\"Identity.SessionRevoked\",\"Subject\":\"Session revoked\",\"TemplateModel\":{\"action\":\"admin-revoke\"}}]},\"Recipients\":[{\"Key\":\"0\",\"PreferenceUserId\":null,\"Destinations\":[{\"Channel\":4,\"RecipientType\":3,\"RecipientValue\":\"00000000-0000-0000-0000-000000000012\"}]}],\"ExpiresAtUtc\":null,\"Category\":\"security\",\"Priority\":3,\"ActionUrl\":\"/sessions\",\"OccurredAtUtc\":\"2026-07-25T10:00:00Z\",\"ScheduledForUtc\":null,\"SourceService\":\"identity\",\"RespectUserPreferences\":false,\"PreferenceKey\":null}",
            json);
    }

    [Fact]
    public void Public_property_schema_snapshot_should_cover_every_contract()
    {
        var snapshot = string.Join(
            "\n",
            typeof(AuditLogWriteRequestedTenantEto).Assembly
                .GetTypes()
                .Where(type => type.IsClass && !type.IsAbstract)
                .Where(type => type.IsDefined(typeof(DistributedContractAttribute), inherit: false))
                .OrderBy(type => type.Name, StringComparer.Ordinal)
                .Select(type => $"{type.Name}:{string.Join(',', type.GetProperties(BindingFlags.Instance | BindingFlags.Public).Select(property => property.Name).Order(StringComparer.Ordinal))}"));

        Assert.Equal(ExpectedPropertySchema.ReplaceLineEndings("\n"), snapshot);
    }

    private const string ExpectedPropertySchema = """
AuditLogWriteRequestedHostEto:Actions,Category,ClientId,ClientIpAddress,ClientName,Comments,CorrelationId,DurationMs,EntityChanges,EventId,Exceptions,ExtraProperties,HttpMethod,ImpersonatorTenantId,ImpersonatorTenantName,ImpersonatorUserId,ImpersonatorUserName,IsTruncated,OccurredAtUtc,Path,PayloadBytes,RedactionApplied,RedactionVersion,Signature,SignatureKeyId,SignatureTimestampUtc,SourceService,StatusCode,TenantName,TraceId,UserAgent,UserId,UserName
AuditLogWriteRequestedTenantEto:Actions,Category,ClientId,ClientIpAddress,ClientName,Comments,CorrelationId,DurationMs,EntityChanges,EventId,Exceptions,ExtraProperties,HttpMethod,ImpersonatorTenantId,ImpersonatorTenantName,ImpersonatorUserId,ImpersonatorUserName,IsTruncated,OccurredAtUtc,Path,PayloadBytes,RedactionApplied,RedactionVersion,Signature,SignatureKeyId,SignatureTimestampUtc,SourceService,StatusCode,TenantId,TenantName,TraceId,UserAgent,UserId,UserName
FeatureValuesChangedHostEto:FeatureNames,OccurredAtUtc
FeatureValuesChangedTenantEto:FeatureNames,OccurredAtUtc,TenantId
IdentityPlatformRoleRetirementRequestedEto:LifecycleVersion,OccurredAtUtc,RequestId,RoleId
IdentitySessionRevokedHostEto:Action,OccurredAtUtc,SessionId,UserId
IdentitySessionRevokedTenantEto:Action,OccurredAtUtc,SessionId,TenantId,UserId
NotificationSendRequestedHostEto:ActionUrl,Category,DeliveryPlan,ExpiresAtUtc,OccurredAtUtc,PreferenceKey,Priority,Recipients,RequestId,RespectUserPreferences,ScheduledForUtc,SourceService
NotificationSendRequestedTenantEto:ActionUrl,Category,DeliveryPlan,ExpiresAtUtc,OccurredAtUtc,PreferenceKey,Priority,Recipients,RequestId,RespectUserPreferences,ScheduledForUtc,SourceService,TenantId
PermissionGrantApprovalDecidedEto:DeciderDisplayName,Decision,DecisionReason,OccurredAtUtc,PermissionName,ProviderDisplayName,RequestId,RequesterUserId,TenantId
PermissionGrantApprovalRequestedEto:ApproverRoleNames,ApproverUserIds,OccurredAtUtc,PermissionName,ProviderDisplayName,Reason,RequestId,RequesterDisplayName,TenantId,TicketId
PermissionGrantsChangedHostEto:OccurredAtUtc,ProviderKey,ProviderName,Stamp
PermissionGrantsChangedTenantEto:OccurredAtUtc,ProviderKey,ProviderName,Stamp,TenantId
PermissionsPlatformRoleRetirementCompletedEto:LifecycleVersion,OccurredAtUtc,RequestId,RoleId
TenantAdminGrantsProvisionedEto:OccurredAtUtc,OwnerUserId,RequestId,TenantId
TenantClosureChangedEto:IsClosed,OccurredAtUtc,OwnerUserId,ReasonCode,RequestId,TenantId
TenantDatabaseChangedEto:ChangeKind,DatabaseName,OccurredAtUtc,TargetFingerprint,TenantId
TenantDeactivatedEto:OccurredAtUtc,TenantId,TenantName
TenantInitialOwnerOnboardingCompletedEto:OccurredAtUtc,RequestId,TenantId
TenantInitialOwnerOnboardingRequestedEto:ExpiresAtUtc,OccurredAtUtc,RequestId,TenantId
TenantLifecycleChangedEto:CurrentStatus,LifecycleRevision,OccurredAtUtc,PreviousStatus,ReasonCode,TenantId,TenantName
TenantOrganizationProfileChangedEto:ChangedFields,OccurredAtUtc,TenantId
TenantOwnershipCancellationRequestedEto:OccurredAtUtc,ReasonCode,RequestId,TenantId
TenantOwnershipCompletedEto:NewOwnerUserId,OccurredAtUtc,PreviousOwnerUserId,RequestId,TenantId
TenantOwnershipInvitationCanceledEto:OccurredAtUtc,RequestId,TenantId
TenantOwnershipInvitationCreatedEto:InvitationId,OccurredAtUtc,RequestId,TenantId
TenantOwnershipInvitationRequestedEto:IsResend,Kind,OccurredAtUtc,RequestId,SuggestedUserName,TargetEmail,TenantId,TenantName
TenantOwnershipInvitationVerifiedEto:DisplayName,Email,OccurredAtUtc,RequestId,TenantId,UserId,UserName
""";
}
