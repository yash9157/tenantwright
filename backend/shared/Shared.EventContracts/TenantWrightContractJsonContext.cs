using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Shared.EventContracts;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(AuditLogWriteRequestedHostEto))]
[JsonSerializable(typeof(AuditLogWriteRequestedTenantEto))]
[JsonSerializable(typeof(FeatureValuesChangedHostEto))]
[JsonSerializable(typeof(FeatureValuesChangedTenantEto))]
[JsonSerializable(typeof(IdentityPlatformRoleRetirementRequestedEto))]
[JsonSerializable(typeof(IdentitySessionRevokedHostEto))]
[JsonSerializable(typeof(IdentitySessionRevokedTenantEto))]
[JsonSerializable(typeof(NotificationSendRequestedHostEto))]
[JsonSerializable(typeof(NotificationSendRequestedTenantEto))]
[JsonSerializable(typeof(NotificationDeliveryPlan))]
[JsonSerializable(typeof(NotificationChannelPlan))]
[JsonSerializable(typeof(NotificationRecipientPlan))]
[JsonSerializable(typeof(NotificationDestination))]
[JsonSerializable(typeof(PermissionGrantApprovalDecidedEto))]
[JsonSerializable(typeof(PermissionGrantApprovalRequestedEto))]
[JsonSerializable(typeof(PermissionGrantsChangedHostEto))]
[JsonSerializable(typeof(PermissionGrantsChangedTenantEto))]
[JsonSerializable(typeof(PermissionsPlatformRoleRetirementCompletedEto))]
[JsonSerializable(typeof(TenantAdminGrantsProvisionedEto))]
[JsonSerializable(typeof(TenantClosureChangedEto))]
[JsonSerializable(typeof(TenantDatabaseChangedEto))]
[JsonSerializable(typeof(TenantDeactivatedEto))]
[JsonSerializable(typeof(TenantLifecycleChangedEto))]
[JsonSerializable(typeof(TenantInitialOwnerOnboardingRequestedEto))]
[JsonSerializable(typeof(TenantInitialOwnerOnboardingCompletedEto))]
[JsonSerializable(typeof(TenantOrganizationProfileChangedEto))]
[JsonSerializable(typeof(TenantOwnershipCancellationRequestedEto))]
[JsonSerializable(typeof(TenantOwnershipCompletedEto))]
[JsonSerializable(typeof(TenantOwnershipInvitationCanceledEto))]
[JsonSerializable(typeof(TenantOwnershipInvitationCreatedEto))]
[JsonSerializable(typeof(TenantOwnershipInvitationRequestedEto))]
[JsonSerializable(typeof(TenantOwnershipInvitationVerifiedEto))]
public sealed partial class TenantWrightContractJsonContext : JsonSerializerContext
{
    public static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(TenantWrightContractJsonContext.Default.Options)
        {
            PropertyNameCaseInsensitive = false,
            WriteIndented = false
        };
        options.TypeInfoResolver = TenantWrightContractJsonContext.Default;
        return options;
    }
}

public static class TenantWrightContractSerializer
{
    public static byte[] SerializeCanonical(object contract, Type declaredType)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(declaredType);

        JsonTypeInfo typeInfo = TenantWrightContractJsonContext.Default.GetTypeInfo(declaredType)
            ?? throw new InvalidOperationException("The distributed contract is not registered in the source-generated JSON context.");
        return SerializeCanonical(contract, typeInfo);
    }

    public static byte[] SerializeCanonical(object contract, JsonTypeInfo typeInfo)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(typeInfo);
        if (contract.GetType() != typeInfo.Type)
            throw new InvalidOperationException("Canonical message metadata must match the exact runtime contract.");
        var element = JsonSerializer.SerializeToElement(contract, typeInfo);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            WriteCanonical(writer, element);
        }
        return stream.ToArray();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
