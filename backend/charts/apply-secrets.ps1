[CmdletBinding()]
param(
    [string]$Namespace = "tenantwright",
    [ValidateSet("dev", "production")][string]$Environment = "dev",
    [ValidateSet("app", "observability")]
    [string]$Profile = "app",
    [string]$SecretsFile = "",
    [switch]$CreateNamespace,
    [switch]$DryRun,
    # The workload messaging provider. Any provider requires the RabbitMQ and signing secrets; MassTransit also
    # requires its licence secret.
    [ValidateSet("None", "MassTransit", "Wolverine")][string]$MessagingProvider = "None",
    [switch]$RestartAppDeployments
)

$ErrorActionPreference = "Stop"

function Assert-CommandExists {
    param([Parameter(Mandatory = $true)][string]$CommandName)

    if (-not (Get-Command $CommandName -ErrorAction SilentlyContinue)) {
        throw "Required command not found: $CommandName"
    }
}

function Assert-YamlCmdletsAvailable {
    if (-not (Get-Command "ConvertFrom-Yaml" -ErrorAction SilentlyContinue)) {
        throw "This script requires PowerShell YAML cmdlets (`ConvertFrom-Yaml`). Install a YAML module (for example `Install-Module powershell-yaml -Scope CurrentUser`)."
    }
}

function Get-PropertyValue {
    param(
        [Parameter(Mandatory = $true)]$Object,
        [Parameter(Mandatory = $true)][string]$Name
    )

    if ($null -eq $Object) {
        return $null
    }

    if ($Object -is [System.Collections.IDictionary]) {
        if ($Object.Contains($Name)) {
            return $Object[$Name]
        }

        return $null
    }

    $property = $Object.PSObject.Properties[$Name]
    if ($null -ne $property) {
        return $property.Value
    }

    return $null
}

function Get-DefaultSecretsFilePaths {
    if ($Profile -eq "observability") {
        return @((Join-Path $secretsRoot "observability-secrets.local.yaml"))
    }

    $paths = @((Join-Path $secretsRoot "app-secrets.local.yaml"))
    $publicRegistrationSecrets = Join-Path $secretsRoot "public-registration-secrets.local.yaml"
    if (Test-Path $publicRegistrationSecrets) {
        $paths += $publicRegistrationSecrets
    }
    return $paths
}

function Resolve-SecretsFilePaths {
    if (-not [string]::IsNullOrWhiteSpace($SecretsFile)) {
        if (-not (Test-Path $SecretsFile)) {
            throw "Secrets file not found: $SecretsFile"
        }

        return @((Resolve-Path -Path $SecretsFile).Path)
    }

    $resolved = Get-DefaultSecretsFilePaths
    foreach ($path in $resolved) {
        if (-not (Test-Path $path)) {
            throw "Default secrets file not found for profile '$Profile': $path"
        }
    }
    return @($resolved | ForEach-Object { (Resolve-Path -Path $_).Path })
}

function Split-YamlDocuments {
    param([Parameter(Mandatory = $true)][string]$YamlText)

    $normalized = $YamlText -replace "`r`n", "`n"
    $parts = [regex]::Split($normalized, '(?m)^\s*---\s*$')
    $docs = @()

    foreach ($part in $parts) {
        $trimmed = $part.Trim()
        if (-not [string]::IsNullOrWhiteSpace($trimmed)) {
            $docs += $trimmed
        }
    }

    return $docs
}

function Read-SecretDocuments {
    param([Parameter(Mandatory = $true)][string]$Path)

    $raw = Get-Content -Path $Path -Raw
    $docTexts = Split-YamlDocuments -YamlText $raw
    if ($docTexts.Count -eq 0) {
        throw "Secrets file '$Path' does not contain any YAML documents."
    }

    $documents = @()
    foreach ($docText in $docTexts) {
        try {
            $parsed = ConvertFrom-Yaml -Yaml $docText
        }
        catch {
            throw "Unable to parse YAML document in '$Path': $($_.Exception.Message)"
        }

        if ($null -ne $parsed) {
            $documents += $parsed
        }
    }

    if ($documents.Count -eq 0) {
        throw "Secrets file '$Path' did not parse into any Kubernetes objects."
    }

    return $documents
}

function Test-SecretKeyExists {
    param(
        [Parameter(Mandatory = $true)]$SecretDocument,
        [Parameter(Mandatory = $true)][string]$Key
    )

    $stringData = Get-PropertyValue -Object $SecretDocument -Name "stringData"
    $data = Get-PropertyValue -Object $SecretDocument -Name "data"

    if ($stringData -is [System.Collections.IDictionary]) {
        if ($stringData.Contains($Key)) {
            return $true
        }
    }
    elseif ($null -ne $stringData -and $null -ne $stringData.PSObject.Properties[$Key]) {
        return $true
    }

    if ($data -is [System.Collections.IDictionary]) {
        return $data.Contains($Key)
    }

    return $null -ne $data -and $null -ne $data.PSObject.Properties[$Key]
}

function Test-SecretLabelEquals {
    param(
        [Parameter(Mandatory = $true)]$SecretDocument,
        [Parameter(Mandatory = $true)][string]$Key,
        [Parameter(Mandatory = $true)][string]$ExpectedValue
    )

    $metadata = Get-PropertyValue -Object $SecretDocument -Name "metadata"
    $labels = Get-PropertyValue -Object $metadata -Name "labels"
    return (Get-PropertyValue -Object $labels -Name $Key) -eq $ExpectedValue
}

function Convert-DocumentsToSecretMap {
    param(
        [Parameter(Mandatory = $true)]$Documents,
        [Parameter(Mandatory = $true)][string]$ExpectedNamespace,
        [Parameter(Mandatory = $true)][string[]]$AllowedNamespaces
    )

    # The consolidated secrets file spans more than one namespace (application
    # Secrets live in 'tenantwright', Temporal's live in 'tenantwright-temporal'), so a
    # document namespace that differs from -Namespace is expected rather than an
    # error. It still has to be one of the namespaces this profile owns: that is
    # what catches a typo before it creates Secrets somewhere unintended.
    $secretMap = @{}
    $seenNamespaces = @{}
    $identityKeys = @{}
    $allDeclared = $true

    foreach ($doc in $Documents) {
        $kind = Get-PropertyValue -Object $doc -Name "kind"
        if ($kind -ne "Secret") {
            throw "Secrets file contains unsupported kind '$kind'. Only kind: Secret is allowed."
        }

        $metadata = Get-PropertyValue -Object $doc -Name "metadata"
        $name = Get-PropertyValue -Object $metadata -Name "name"
        if ([string]::IsNullOrWhiteSpace($name)) {
            throw "Secret document is missing metadata.name."
        }

        $docNamespace = Get-PropertyValue -Object $metadata -Name "namespace"
        if ([string]::IsNullOrWhiteSpace($docNamespace)) {
            # No explicit namespace: it will be routed by -n at apply time.
            $allDeclared = $false
            $docNamespace = $ExpectedNamespace
        }

        if ($AllowedNamespaces -notcontains $docNamespace) {
            throw "Secret '$name' declares metadata.namespace '$docNamespace', which is not owned by this profile. Allowed: $($AllowedNamespaces -join ', ')."
        }

        # Duplicates are per namespace: the same Secret name may legitimately
        # exist in two namespaces, but never twice in one.
        $identityKey = "$docNamespace/$name"
        if ($identityKeys.ContainsKey($identityKey)) {
            throw "Secrets file contains duplicate Secret '$identityKey'."
        }
        $identityKeys[$identityKey] = $true
        $seenNamespaces[$docNamespace] = $true

        # Validation looks Secrets up by bare name.
        if (-not $secretMap.ContainsKey($name)) {
            $secretMap[$name] = $doc
        }
    }

    return [pscustomobject]@{
        Map                  = $secretMap
        Namespaces           = @($seenNamespaces.Keys | Sort-Object)
        AllNamespacesDeclared = $allDeclared
    }
}

function Validate-SecretsFile {
    param(
        [Parameter(Mandatory = $true)]$SecretMap,
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ProfileName,
        [bool]$RequireMassTransitLicense = $false,
        [bool]$RequireMessagingRuntime = $false
    )

    $required = [ordered]@{}
    $forbiddenNames = @()

    if ($ProfileName -eq "observability") {
        $required["tenantwright-grafana-admin"] = @(
            "admin-user",
            "admin-password"
        )
        $required["tenantwright-alerting-secrets"] = @(
            "alertmanager-webhook-url"
        )
        $required["tenantwright-observability-heartbeat"] = @("webhook-url")
        $required["tenantwright-grafana-oidc"] = @(
            "client-id",
            "client-secret"
        )
        $required["tenantwright-grafana-database"] = @(
            "host",
            "name",
            "user",
            "password"
        )
        $required["tenantwright-grafana-database-ca"] = @("ca.crt")
        $required["tenantwright-prometheus-remote-write"] = @("token")
    }
    else {
        $required["tenantwright-valkey-auth"] = @(
            "default",
            "replication"
        )

        # Every service that mounts a database connection string. Keep this list
        # in step with the charts that declare a `tenantwright-<service>-db` Secret;
        # a service missing from here passes validation and then crash-loops on
        # a Secret that was never applied.
        $databaseServices = @(
            "auditlogs",
            "auth",
            "identity",
            "notifications",
            "permissions",
            "tenant",
            "workflows"
        )
        foreach ($service in $databaseServices) {
            $required["tenantwright-$service-db"] = @("connection-string")
        }

        $required["tenantwright-auth-secrets"] = @(
            "openiddict-internal-client-secret",
            "openiddict-signing-cert-password",
            "openiddict-encryption-cert-password"
        )

        if ($RequireMassTransitLicense) {
            $required["tenantwright-masstransit-license"] = @("license")
        }

        if ($RequireMessagingRuntime) {
            $messagingServices = @(
                "auditlogs", "auth", "identity", "notifications", "permissions", "tenant", "workflows"
            )
            foreach ($service in $messagingServices) {
                $required["tenantwright-$service-rabbitmq"] = @(
                    "rabbitmq-host",
                    "rabbitmq-port",
                    "rabbitmq-username",
                    "rabbitmq-password",
                    "rabbitmq-vhost"
                )
                $required["tenantwright-$service-messaging-signing"] = @("private.pem")
            }
            $required["tenantwright-messaging-verification-keys"] = @(
                "tenantwright-auditlogs-v1.pem",
                "tenantwright-auth-v1.pem",
                "tenantwright-identity-v1.pem",
                "tenantwright-notifications-v1.pem",
                "tenantwright-permissions-v1.pem",
                "tenantwright-tenant-v1.pem",
                "tenantwright-workflows-v1.pem",
                "tenantwright-messaging-replay-v1.pem"
            )
        }

        $forbiddenNames = @(
            "tenantwright-auth-openiddict-certs",
            "tenantwright-auth-tls",
            "tenantwright-gateway-tls",
            "tenantwright-rabbitmq-server-tls",
            "tenantwright-rabbitmq-ca",
            "tenantwright-shared-infra",
            "tenantwright-workflows-temporal-client-tls",
            "tenantwright-workflows-temporal-api-key"
        )
    }

    foreach ($name in $forbiddenNames) {
        if ($SecretMap.ContainsKey($name)) {
            throw "Secrets file '$Path' includes forbidden Secret '$name'. Keep it out of this workflow (managed separately)."
        }
    }

    foreach ($name in $required.Keys) {
        if (-not $SecretMap.ContainsKey($name)) {
            throw "Secrets file '$Path' is missing required Secret '$name'."
        }

        foreach ($requiredKey in $required[$name]) {
            if (-not (Test-SecretKeyExists -SecretDocument $SecretMap[$name] -Key $requiredKey)) {
                throw "Secret '$name' in '$Path' is missing required key '$requiredKey' (expected under stringData or data)."
            }
        }
    }

    if ($RequireMessagingRuntime) {
        foreach ($service in $messagingServices) {
            $rabbitMqSecret = $SecretMap["tenantwright-$service-rabbitmq"]
            if (-not (Test-SecretLabelEquals `
                -SecretDocument $rabbitMqSecret `
                -Key "rabbitmq.com/topology-operator" `
                -ExpectedValue "true")) {
                throw "Secret 'tenantwright-$service-rabbitmq' must carry label rabbitmq.com/topology-operator: 'true'."
            }
        }
    }
}

function Invoke-Kubectl {
    param([Parameter(Mandatory = $true)][string[]]$Args)

    Write-Host "==> kubectl $($Args -join ' ')" -ForegroundColor Cyan
    & kubectl @Args
    if ($LASTEXITCODE -ne 0) {
        throw "kubectl command failed: kubectl $($Args -join ' ')"
    }
}

function Ensure-Namespace {
    param([Parameter(Mandatory = $true)][string]$TargetNamespace)

    & kubectl get namespace $TargetNamespace *> $null
    if ($LASTEXITCODE -eq 0) {
        Write-Host "==> namespace '$TargetNamespace' already exists" -ForegroundColor DarkGray
        return
    }

    Invoke-Kubectl -Args @("create", "namespace", $TargetNamespace)
}

function Restart-AppDeployments {
    param([Parameter(Mandatory = $true)][string]$TargetNamespace)

    $deployments = @(
        "tenantwright-auth",
        "tenantwright-gateway",
        "tenantwright-identity",
        "tenantwright-tenant",
        "tenantwright-permissions",
        "tenantwright-notifications",
        "tenantwright-auditlogs",
        "tenantwright-workflows"
    )

    foreach ($name in $deployments) {
        $args = @("-n", $TargetNamespace, "rollout", "restart", "deployment/$name")
        Write-Host "==> kubectl $($args -join ' ')" -ForegroundColor Cyan
        & kubectl @args
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "Unable to restart deployment/$name in namespace '$TargetNamespace'. It may not exist yet."
        }
    }

    Write-Host "Run rollout status checks as needed (example): kubectl -n $TargetNamespace rollout status deploy/tenantwright-auth" -ForegroundColor Yellow
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$secretsRoot = Join-Path $PSScriptRoot "environments/$Environment/private"

Assert-CommandExists -CommandName "kubectl"
Assert-YamlCmdletsAvailable

if ($MessagingProvider -ne "None" -and $Profile -ne "app") {
    throw "-MessagingProvider is supported only with -Profile app."
}
$RequireMessagingRuntime = $MessagingProvider -ne "None"
$RequireMassTransitLicense = $MessagingProvider -eq "MassTransit"

if (-not $PSBoundParameters.ContainsKey("Namespace") -and $Profile -eq "observability") {
    $Namespace = "tenantwright-observability"
}

$resolvedSecretsFiles = Resolve-SecretsFilePaths
foreach ($resolvedSecretsFile in $resolvedSecretsFiles) {
    Write-Host "==> using secrets file: $resolvedSecretsFile" -ForegroundColor Cyan
}

# Namespaces are created after parsing, once the file has told us which ones it
# actually targets.

$documents = @()
foreach ($resolvedSecretsFile in $resolvedSecretsFiles) {
    $documents += Read-SecretDocuments -Path $resolvedSecretsFile
}

# Namespaces this profile is allowed to write to.
if ($Profile -eq "observability") {
    $allowedNamespaces = @($Namespace)
}
else {
    $allowedNamespaces = @($Namespace, "tenantwright-temporal") | Sort-Object -Unique
}

$parsed = Convert-DocumentsToSecretMap `
    -Documents $documents `
    -ExpectedNamespace $Namespace `
    -AllowedNamespaces $allowedNamespaces
$secretMap = $parsed.Map

Write-Host "==> secrets span namespace(s): $($parsed.Namespaces -join ', ')" -ForegroundColor Cyan

Validate-SecretsFile `
    -SecretMap $secretMap `
    -Path ($resolvedSecretsFiles -join ", ") `
    -ProfileName $Profile `
    -RequireMassTransitLicense:$RequireMassTransitLicense `
    -RequireMessagingRuntime:$RequireMessagingRuntime

if ($CreateNamespace -and -not $DryRun) {
    foreach ($ns in $parsed.Namespaces) {
        Ensure-Namespace -TargetNamespace $ns
    }
}

# When every document declares its own namespace, do NOT pass -n: kubectl would
# reject a manifest whose namespace differs from the flag. Each document routes
# itself. The -n path stays for older single-namespace files that omit it.
$applyArgs = @("apply")
if (-not $parsed.AllNamespacesDeclared) {
    $applyArgs = @("-n", $Namespace, "apply")
}
if ($DryRun) {
    $applyArgs += "--dry-run=client"
}
foreach ($resolvedSecretsFile in $resolvedSecretsFiles) {
    $applyArgs += @("-f", $resolvedSecretsFile)
}

Invoke-Kubectl -Args $applyArgs

if ($Profile -eq "observability") {
    Write-Host "Applied observability secrets from '$resolvedSecretsFile'." -ForegroundColor Green
    Write-Host "Reminder: cert-manager manages 'tenantwright-grafana-tls' when ingress certManager is enabled." -ForegroundColor Yellow
    Write-Host "Reminder: webhook, heartbeat, OIDC, Grafana database, database CA, and remote-write values must come from the approved secret manager." -ForegroundColor Yellow
}
else {
    Write-Host "Applied app/infra secrets from '$resolvedSecretsFile'." -ForegroundColor Green
    Write-Host "Reminder: 'tenantwright-auth-openiddict-certs' (PFX files) is managed separately." -ForegroundColor Yellow
    Write-Host "Reminder: cert-manager manages 'tenantwright-auth-tls' and 'tenantwright-gateway-tls' when ingress certManager is enabled." -ForegroundColor Yellow
    Write-Host "If shared Valkey/RabbitMQ credentials changed, restart infra wrappers manually if needed (valkey/rabbitmq)." -ForegroundColor Yellow
}

if ($RestartAppDeployments) {
    if ($DryRun) {
        Write-Host "Skipping -RestartAppDeployments because -DryRun was specified." -ForegroundColor Yellow
    }
    elseif ($Profile -ne "app") {
        Write-Host "Skipping -RestartAppDeployments because it is only supported with -Profile app." -ForegroundColor Yellow
    }
    else {
        Restart-AppDeployments -TargetNamespace $Namespace
    }
}
