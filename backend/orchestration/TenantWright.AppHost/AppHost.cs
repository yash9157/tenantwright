using TenantWright.AppHost;

var builder = DistributedApplication.CreateBuilder(args);

const string bootstrapTenantId = "11111111-1111-1111-1111-111111111111";
const string bootstrapTenantName = "default";

var infrastructure = LocalInfrastructure.Add(builder);

var hostAdminPassword = LocalSecrets.AddPassword(builder, "bootstrap-host-admin-password");
var tenantAdminPassword = LocalSecrets.AddPassword(builder, "bootstrap-tenant-admin-password");
var internalClientSecret = LocalSecrets.AddClientSecret(builder, "internal-client-secret");
var notificationsInternalClientSecret = LocalSecrets.AddClientSecret(builder, "notifications-internal-client-secret");
var notificationsProviderProtectionKey = LocalSecrets.AddAes256Key(builder, "notifications-provider-protection-key");
var publicRegistrationHashKey = LocalSecrets.AddClientSecret(builder, "public-registration-hash-key");
var auditSealSigningKey = LocalSecrets.AddAes256Key(builder, "auditlogs-seal-signing-key");
var tenantConnectionProtectionKey = LocalSecrets.AddAes256Key(builder, "tenant-connection-protection-key");

var auth = builder.AddTenantWrightService("auth", @"..\..\app\Auth\Auth\Auth.csproj", httpPort: 5234, httpsPort: 7239)
    .WithServiceInfrastructure(infrastructure, "tenantwright_auth", tenantConnectionProtectionKey)
    .WithEnvironment("OpenIddict__Clients__Internal__Secret", internalClientSecret)
    .WithEnvironment("OpenIddict__Clients__NotificationsInternal__Secret", notificationsInternalClientSecret)
    .WithMessaging(infrastructure, "tenantwright-auth")
    .WithLocalPublicRegistration(builder.Configuration, builder.Environment, infrastructure, publicRegistrationHashKey);

var identity = builder.AddTenantWrightService("identity", @"..\..\services\Identity\Identity\Identity.csproj", httpPort: 5161, httpsPort: 7161)
    .WithServiceInfrastructure(infrastructure, "tenantwright_identity", tenantConnectionProtectionKey)
    .AsInternalService()
    .WithEnvironment("BootstrapSeed__Enabled", "true")
    .WithEnvironment("BootstrapSeed__HostAdmin__Enabled", "true")
    .WithEnvironment("BootstrapSeed__HostAdmin__UserName", "hostadmin")
    .WithEnvironment("BootstrapSeed__HostAdmin__Email", "hostadmin@example.local")
    .WithEnvironment("BootstrapSeed__HostAdmin__Password", hostAdminPassword)
    .WithEnvironment("BootstrapSeed__HostAdmin__ResetPasswordOnSeed", "true")
    .WithEnvironment("BootstrapSeed__TenantAdmin__Enabled", "true")
    .WithEnvironment("BootstrapSeed__TenantAdmin__TenantId", bootstrapTenantId)
    .WithEnvironment("BootstrapSeed__TenantAdmin__TenantName", bootstrapTenantName)
    .WithEnvironment("BootstrapSeed__TenantAdmin__UserName", "tenantadmin")
    .WithEnvironment("BootstrapSeed__TenantAdmin__Email", "tenantadmin@example.local")
    .WithEnvironment("BootstrapSeed__TenantAdmin__Password", tenantAdminPassword)
    .WithEnvironment("BootstrapSeed__TenantAdmin__ResetPasswordOnSeed", "true")
    .WithEnvironment("IdentityInternal__AllowedClientIds", "auth-internal,tenantwright-notifications-internal")
    .WithEnvironment("RemoteFeatureChecker__ClientSecret", internalClientSecret)
    .WithMessaging(infrastructure, "tenantwright-identity");

var tenant = builder.AddTenantWrightService("tenant", @"..\..\services\Tenant\Tenant\Tenant.csproj", httpPort: 5162, httpsPort: 7162)
    .WithServiceInfrastructure(infrastructure, "tenantwright_tenant", tenantConnectionProtectionKey)
    .AsInternalService()
    .WithEnvironment("RemoteFeatureChecker__ClientSecret", internalClientSecret)
    // Impact enrichment reads Identity and Notifications counts under the shared internal client both allow-list.
    .WithEnvironment("OpenIddict__Clients__Internal__ClientId", "auth-internal")
    .WithEnvironment("OpenIddict__Clients__Internal__Secret", internalClientSecret)
    .WithEnvironment("TenantImpactEnrichment__ClientProfile", "Internal")
    .WithSharedDatabaseTargets(
        infrastructure,
        ("AuditLogs", "tenantwright_auditlogs"),
        ("Notifications", "tenantwright_notifications"),
        ("Permissions", "tenantwright_permissions"))
    .WithEnvironment("BootstrapSeed__Enabled", "true")
    .WithEnvironment("BootstrapSeed__DefaultTenant__Enabled", "true")
    .WithEnvironment("BootstrapSeed__DefaultTenant__Id", bootstrapTenantId)
    .WithEnvironment("BootstrapSeed__DefaultTenant__Name", bootstrapTenantName)
    .WithMessaging(infrastructure, "tenantwright-tenant");

var permissions = builder.AddTenantWrightService("permissions", @"..\..\services\Permissions\Permissions\Permissions.csproj", httpPort: 5163, httpsPort: 7163)
    .WithServiceInfrastructure(infrastructure, "tenantwright_permissions", tenantConnectionProtectionKey)
    .AsInternalService()
    .WithEnvironment("RemoteFeatureChecker__ClientSecret", internalClientSecret)
    .WithEnvironment("IdentityPermissionTargetValidation__ClientSecret", internalClientSecret)
    .WaitFor(tenant)
    .WithMessaging(infrastructure, "tenantwright-permissions");

var auditLogs = builder.AddTenantWrightService("auditlogs", @"..\..\services\AuditLogs\AuditLogs\AuditLogs.csproj", httpPort: 5164, httpsPort: 7164)
    .WithServiceInfrastructure(infrastructure, "tenantwright_auditlogs", tenantConnectionProtectionKey)
    .AsInternalService()
    .WithEnvironment("RemoteFeatureChecker__ClientSecret", internalClientSecret)
    // Audit log sealing signs each seal with the current key; without one every seal run is skipped.
    .WithEnvironment("AuditLogging__Signatures__CurrentKeyId", "auditlogs-local-v1")
    .WithEnvironment("AuditLogging__Signatures__Keys__auditlogs-local-v1", auditSealSigningKey)
    .WithMessaging(infrastructure, "tenantwright-auditlogs");

var notifications = builder.AddTenantWrightService("notifications", @"..\..\services\Notifications\Notifications\Notifications.csproj", httpPort: 5165, httpsPort: 7165)
    .WithServiceInfrastructure(infrastructure, "tenantwright_notifications", tenantConnectionProtectionKey)
    .AsInternalService()
    .WithEnvironment("RemoteFeatureChecker__ClientSecret", internalClientSecret)
    .WithEnvironment("OpenIddict__Clients__NotificationsInternal__ClientId", "tenantwright-notifications-internal")
    .WithEnvironment("OpenIddict__Clients__NotificationsInternal__Secret", notificationsInternalClientSecret)
    .WithEnvironment("IdentityAudienceClient__BaseAddress", identity.GetEndpoint("http"))
    .WithEnvironment("IdentityAudienceClient__ClientProfile", "NotificationsInternal")
    .WithEnvironment("IdentityAudienceClient__Scope", "identity-internal")
    .WithEnvironment("IdentityPreferencesClient__BaseAddress", identity.GetEndpoint("http"))
    .WithEnvironment("IdentityPreferencesClient__ClientProfile", "NotificationsInternal")
    .WithEnvironment("IdentityPreferencesClient__Scope", "identity-internal")
    .WithEnvironment("Notifications__Audience__Enabled", "true")
    .WithEnvironment(
        "Notifications__ProviderSecrets__Protection__Keys__notifications-provider-secrets-v1",
        notificationsProviderProtectionKey)
    .WithReference(identity)
    .WaitFor(identity)
    .WithMessaging(infrastructure, "tenantwright-notifications");

var workflows = builder.AddTenantWrightService("workflows", @"..\..\services\Workflows\Workflows\Workflows.csproj", httpPort: 5166, httpsPort: 7166)
    .WithServiceInfrastructure(infrastructure, "tenantwright_workflows", tenantConnectionProtectionKey)
    .AsInternalService()
    .WithEnvironment("RemoteFeatureChecker__ClientSecret", internalClientSecret)
    .WithLocalTemporal(infrastructure)
    .WithMessaging(infrastructure, "tenantwright-workflows");

builder.AddTenantWrightService("apigateway", @"..\..\gateway\ApiGateway\ApiGateway.csproj", httpPort: 5137, httpsPort: 7030)
    .WithReference(auth)
    .WithReference(identity)
    .WithReference(tenant)
    .WithReference(permissions)
    .WithReference(auditLogs)
    .WithReference(notifications)
    .WithReference(workflows)
    .WithGatewayDestination("auth", auth)
    .WithGatewayDestination("identity", identity)
    .WithGatewayDestination("tenant", tenant)
    .WithGatewayDestination("permissions", permissions)
    .WithGatewayDestination("auditlogs", auditLogs)
    .WithGatewayDestination("notifications", notifications)
    .WithGatewayDestination("workflows", workflows);

builder.Build().Run();
