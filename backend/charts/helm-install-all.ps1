[CmdletBinding()]
param(
    [ValidateSet("dev", "production")][string]$Environment = "production",
    [string]$Namespace = "tenantwright",
    [string]$HelmPath = "helm",
    [switch]$InstallCertManager,
    [switch]$InstallRabbitMqOperators,
    [switch]$InstallWorkflowService,
    [ValidateSet("None", "MassTransit", "Wolverine")][string]$MessagingProvider = "None",
    [string]$EnvironmentValuesDirectory = "",
    [string]$ImageDigestsPath = "",
    [string]$PostgreSqlPoolAllocationPath = "",
    [string]$CertManagerNamespace = "cert-manager",
    [switch]$CreateNamespace,
    [switch]$Wait,
    [switch]$PreflightOnly,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"
if ($DryRun -and $PreflightOnly) { throw '-DryRun and -PreflightOnly are separate modes.' }
. (Join-Path $PSScriptRoot 'environment-values.ps1')

function Assert-CommandExists {
    param([Parameter(Mandatory = $true)][string]$CommandName)

    if (-not (Get-Command $CommandName -ErrorAction SilentlyContinue)) {
        throw "Required command not found: $CommandName"
    }
}

function Assert-YamlCmdletsAvailable {
    if (-not (Get-Command "ConvertFrom-Yaml" -ErrorAction SilentlyContinue)) {
        throw "Live deployment requires ConvertFrom-Yaml to validate environment values and image digests."
    }
}

function Get-PropertyValue {
    param(
        [Parameter(Mandatory = $true)]$Object,
        [Parameter(Mandatory = $true)][string]$Name
    )

    if ($Object -is [System.Collections.IDictionary]) {
        return $Object[$Name]
    }

    return $Object.PSObject.Properties[$Name].Value
}

function Assert-NetworkPolicyEnvironmentOwnership {
    param(
        [Parameter(Mandatory = $true)]$ValuesDocument,
        [Parameter(Mandatory = $true)][string]$ReleaseName,
        [Parameter(Mandatory = $true)][bool]$RequiresPostgresql,
        [bool]$AllowDefaultIngressNamespace = $false
    )

    $networkPolicy = Get-PropertyValue -Object $ValuesDocument -Name "networkPolicy"
    if ($null -eq $networkPolicy) {
        throw "Environment values for $ReleaseName must declare networkPolicy ownership."
    }
    if ((Get-PropertyValue -Object $networkPolicy -Name 'enabled') -eq $false) {
        throw "NetworkPolicy must remain enabled for $ReleaseName."
    }

    $ingressController = Get-PropertyValue -Object $networkPolicy -Name "ingressController"
    if ($null -ne $ingressController -and
        (Get-PropertyValue -Object $ingressController -Name 'enabled') -eq $false) {
        throw "The ingress controller policy must remain enabled for $ReleaseName."
    }
    $ingressNamespace = if ($null -ne $ingressController) {
        [string](Get-PropertyValue -Object $ingressController -Name "namespace")
    }
    else {
        ""
    }
    if ([string]::IsNullOrWhiteSpace($ingressNamespace) -and -not $AllowDefaultIngressNamespace) {
        throw "Environment values for $ReleaseName must declare networkPolicy.ingressController.namespace."
    }

    if (-not $RequiresPostgresql) {
        return
    }

    $postgresql = Get-PropertyValue -Object $networkPolicy -Name "postgresql"
    if ($null -eq $postgresql -or (Get-PropertyValue -Object $postgresql -Name "enabled") -ne $true) {
        throw "Environment values for $ReleaseName must explicitly enable networkPolicy.postgresql."
    }

    $port = Get-PropertyValue -Object $postgresql -Name "port"
    if ($port -isnot [int] -or $port -lt 1 -or $port -gt 65535) {
        throw "Environment values for $ReleaseName must declare a valid networkPolicy.postgresql.port."
    }

    $cidrs = @(Get-PropertyValue -Object $postgresql -Name "cidrs")
    if ($cidrs.Count -eq 0) {
        throw "Environment values for $ReleaseName must declare at least one reviewed networkPolicy.postgresql.cidrs entry."
    }

    foreach ($cidr in $cidrs) {
        $parts = ([string]$cidr).Split("/", [System.StringSplitOptions]::RemoveEmptyEntries)
        $address = $null
        if ($parts.Count -ne 2 -or -not [System.Net.IPAddress]::TryParse($parts[0], [ref]$address)) {
            throw "Environment values for $ReleaseName contain an invalid PostgreSQL CIDR."
        }

        $prefix = 0
        $maximumPrefix = if ($address.AddressFamily -eq [System.Net.Sockets.AddressFamily]::InterNetwork) { 32 } else { 128 }
        if (-not [int]::TryParse($parts[1], [ref]$prefix) -or $prefix -lt 0 -or $prefix -gt $maximumPrefix) {
            throw "Environment values for $ReleaseName contain an invalid PostgreSQL CIDR prefix."
        }
    }
}

function Assert-PostgreSqlRuntimeEnvironmentOwnership {
    param(
        [Parameter(Mandatory = $true)]$ValuesDocument,
        [Parameter(Mandatory = $true)][string]$ReleaseName,
        [bool]$AllowDirect = $false
    )

    $environment = Get-PropertyValue -Object $ValuesDocument -Name "env"
    if ($null -eq $environment) {
        throw "Environment values for $ReleaseName must declare PostgreSQL runtime ownership under env."
    }

    $mode = [string](Get-PropertyValue -Object $environment -Name "PostgreSql__Runtime__Mode")
    if ($mode -notin @("TransactionPool", "SessionPool") -and -not ($AllowDirect -and $mode -eq "Direct")) {
        throw "Environment values for $ReleaseName must select TransactionPool or SessionPool; Direct is allowed only with -Environment dev."
    }

    $backendPoolSize = 0
    if (-not [int]::TryParse([string](Get-PropertyValue -Object $environment -Name "PostgreSql__Runtime__BackendPoolSize"), [ref]$backendPoolSize)) {
        throw "Environment values for $ReleaseName must declare an integer PostgreSQL backend pool size."
    }
    $minimumBackendPoolSize = if ($AllowDirect -and $mode -eq "Direct") { 0 } else { 1 }
    if ($backendPoolSize -isnot [int] -or $backendPoolSize -lt $minimumBackendPoolSize -or $backendPoolSize -gt 1000) {
        throw "Environment values for $ReleaseName must declare PostgreSql__Runtime__BackendPoolSize between $minimumBackendPoolSize and 1000."
    }

    if ($ReleaseName -eq "tenantwright-auth" -and $mode -eq "TransactionPool") {
        $autoCreateQuartzSchema = [string](Get-PropertyValue -Object $environment -Name "Auth__Quartz__AutoCreateSchema")
        if (-not [string]::Equals($autoCreateQuartzSchema, "false", [StringComparison]::OrdinalIgnoreCase)) {
            throw "tenantwright-auth must set Auth__Quartz__AutoCreateSchema=false when transaction pooling is selected."
        }
    }

    return @{
        Mode = $mode
        BackendPoolSize = $backendPoolSize
    }
}

function Assert-PostgreSqlPoolAllocation {
    param(
        [Parameter(Mandatory = $true)]$AllocationDocument,
        [Parameter(Mandatory = $true)][hashtable]$RuntimeAllocations
    )

    $providerConnectionPoolingAvailable = Get-PropertyValue -Object $AllocationDocument -Name "providerConnectionPoolingAvailable"
    $providerConnectionLimit = Get-PropertyValue -Object $AllocationDocument -Name "providerConnectionLimit"
    $reservedBackendConnections = Get-PropertyValue -Object $AllocationDocument -Name "reservedBackendConnections"
    $services = Get-PropertyValue -Object $AllocationDocument -Name "services"

    if ($providerConnectionPoolingAvailable -ne $true) {
        throw "PostgreSQL pool allocation requires verified provider connection-pooling availability. Free-tier managed PostgreSQL may not provide PgBouncer."
    }
    if ($providerConnectionLimit -isnot [int] -or
        $providerConnectionLimit -lt 10 -or
        $providerConnectionLimit -gt 100000) {
        throw "PostgreSQL pool allocation must declare providerConnectionLimit between 10 and 100000."
    }
    if ($reservedBackendConnections -isnot [int] -or
        $reservedBackendConnections -lt 1 -or
        $reservedBackendConnections -ge $providerConnectionLimit) {
        throw "PostgreSQL pool allocation must reserve between 1 and providerConnectionLimit-1 backend connections."
    }
    if ($null -eq $services) {
        throw "PostgreSQL pool allocation must contain a services mapping."
    }

    $allocatedBackendConnections = 0
    foreach ($releaseName in $RuntimeAllocations.Keys) {
        $serviceAllocation = Get-PropertyValue -Object $services -Name $releaseName
        if ($null -eq $serviceAllocation) {
            throw "PostgreSQL pool allocation is missing service '$releaseName'."
        }

        $allocationMode = [string](Get-PropertyValue -Object $serviceAllocation -Name "mode")
        $allocationSize = Get-PropertyValue -Object $serviceAllocation -Name "backendPoolSize"
        $runtimeAllocation = $RuntimeAllocations[$releaseName]
        if ($allocationMode -ne $runtimeAllocation.Mode -or
            $allocationSize -ne $runtimeAllocation.BackendPoolSize) {
            throw "PostgreSQL pool allocation for '$releaseName' does not match its environment values."
        }

        $allocatedBackendConnections += $allocationSize
    }

    $serviceNames = if ($services -is [System.Collections.IDictionary]) {
        @($services.Keys)
    }
    else {
        @($services.PSObject.Properties.Name)
    }
    $unexpectedServices = @(
        $serviceNames |
            Where-Object { -not $RuntimeAllocations.ContainsKey($_) }
    )
    if ($unexpectedServices.Count -gt 0) {
        throw "PostgreSQL pool allocation contains unselected services: $($unexpectedServices -join ', ')."
    }

    $applicationBudget = $providerConnectionLimit - $reservedBackendConnections
    if ($allocatedBackendConnections -gt $applicationBudget) {
        throw "PostgreSQL backend allocation ($allocatedBackendConnections) exceeds the application budget ($applicationBudget) after reserve."
    }

    Write-Host (
        "PostgreSQL backend allocation verified: {0} allocated, {1} reserved, {2} provider limit." -f
        $allocatedBackendConnections,
        $reservedBackendConnections,
        $providerConnectionLimit) -ForegroundColor Green
}


$chartsRoot = $PSScriptRoot
$environmentRoot = if ([string]::IsNullOrWhiteSpace($EnvironmentValuesDirectory)) {
    Join-Path $chartsRoot "environments/$Environment"
} else { [IO.Path]::GetFullPath($EnvironmentValuesDirectory) }
$digestPath = if ([string]::IsNullOrWhiteSpace($ImageDigestsPath)) {
    Join-Path $environmentRoot 'images.lock.yaml'
} else { [IO.Path]::GetFullPath($ImageDigestsPath) }
$allocationPath = if ([string]::IsNullOrWhiteSpace($PostgreSqlPoolAllocationPath)) {
    Join-Path $environmentRoot 'postgresql-pools.yaml'
} else { [IO.Path]::GetFullPath($PostgreSqlPoolAllocationPath) }
Assert-CommandExists -CommandName $HelmPath
Assert-YamlCmdletsAvailable
if (-not (Test-Path -LiteralPath $digestPath -PathType Leaf)) { throw 'An approved images.lock.yaml is required.' }
$imageDigests = Get-PropertyValue -Object (Read-EnvironmentValues -Paths @($digestPath)) -Name 'images'
if ($null -eq $imageDigests) { throw "The image lock must contain an 'images' mapping." }

$orderedCharts = @(
    @{ Release = "tenantwright-valkey";         Path = (Join-Path $chartsRoot "tenantwright-valkey");         Platform = $true },
    @{ Release = "tenantwright-rabbitmq";       Path = (Join-Path $chartsRoot "tenantwright-rabbitmq");       Platform = $true },
    @{ Release = "tenantwright-permissions";   Path = (Join-Path $chartsRoot "tenantwright-permissions");   Workload = $true; Messaging = $true },
    @{ Release = "tenantwright-identity";      Path = (Join-Path $chartsRoot "tenantwright-identity");      Workload = $true; Messaging = $true },
    @{ Release = "tenantwright-tenant";        Path = (Join-Path $chartsRoot "tenantwright-tenant");        Workload = $true; Messaging = $true },
    @{ Release = "tenantwright-auditlogs";     Path = (Join-Path $chartsRoot "tenantwright-auditlogs");     Workload = $true; Messaging = $true },
    @{ Release = "tenantwright-notifications"; Path = (Join-Path $chartsRoot "tenantwright-notifications"); Workload = $true; Messaging = $true },
    @{ Release = "tenantwright-auth";          Path = (Join-Path $chartsRoot "tenantwright-auth");          Workload = $true; Messaging = $true },
    @{ Release = "tenantwright-gateway";       Path = (Join-Path $chartsRoot "tenantwright-gateway");       Workload = $true }
)

if ($InstallWorkflowService) {
    $gatewayChart = $orderedCharts | Where-Object { $_.Release -eq "tenantwright-gateway" }
    $orderedCharts = @($orderedCharts | Where-Object { $_.Release -ne "tenantwright-gateway" }) +
        @{ Release = "tenantwright-workflows"; Path = (Join-Path $chartsRoot "tenantwright-workflows"); Workload = $true; Messaging = $true } +
        $gatewayChart
}


if ($InstallCertManager) {
    $orderedCharts = @(@{ Release = 'tenantwright-cert-manager'; Path = (Join-Path $chartsRoot 'tenantwright-cert-manager'); Platform = $true }) + $orderedCharts
}

$runtimeAllocations = @{}
foreach ($item in $orderedCharts | Where-Object { $_.Workload }) {
    $digest = [string](Get-PropertyValue -Object $imageDigests -Name $item.Release)
    if ($digest -notmatch '^sha256:[0-9a-f]{64}$') {
        throw "Missing or invalid immutable image digest for $($item.Release)."
    }
}
foreach ($item in $orderedCharts) {
    $files = @(Get-EnvironmentValuesFiles -EnvironmentRoot $environmentRoot -ReleaseName $item.Release `
        -Platform:([bool]$item.Platform) -EnableMessaging:($MessagingProvider -ne "None" -and [bool]$item.Messaging))
    $values = Read-EnvironmentValues -Paths $files
    $digest = ''
    if ($item.Workload) {
        Assert-NetworkPolicyEnvironmentOwnership -ValuesDocument $values -ReleaseName $item.Release `
            -RequiresPostgresql:($item.Release -ne 'tenantwright-gateway')
        if ($item.Release -ne 'tenantwright-gateway') {
            $runtimeAllocations[$item.Release] = Assert-PostgreSqlRuntimeEnvironmentOwnership `
                -ValuesDocument $values -ReleaseName $item.Release -AllowDirect:($Environment -eq 'dev')
        }
        $digest = [string](Get-PropertyValue -Object $imageDigests -Name $item.Release)
    }
    # Build every dependency before rendering, including the shared migration library.
    $dependencyOutput = (& $HelmPath dependency build $item.Path --skip-refresh 2>&1) -join "`n"
    if ($LASTEXITCODE -ne 0) { throw "Helm dependency build failed for $($item.Release)." }
    $arguments = @()
    foreach ($file in $files) { $arguments += @('-f', $file) }
    if ($item.Workload) { $arguments += @('--set-string', "image.digest=$digest") }
    if ($MessagingProvider -ne "None" -and $item.Messaging) { $arguments += @('--set', "messaging.provider=$MessagingProvider") }
    $targetNamespace = if ($item.Release -eq 'tenantwright-cert-manager') { $CertManagerNamespace } else { $Namespace }
    $rendered = (& $HelmPath template $item.Release $item.Path --namespace $targetNamespace @arguments 2>&1) -join "`n"
    if ($LASTEXITCODE -ne 0) { throw "Helm preflight rendering failed for $($item.Release); inspect the selected inputs securely." }
    if ($Environment -eq 'production' -and $rendered -match '(?i)(\.dev\.example\.com|\.example\.com|example\.invalid|changeme)') {
        throw "Production preflight for $($item.Release) contains a development or placeholder value."
    }
    if ($item.Workload -and $rendered.IndexOf("@$digest", [StringComparison]::Ordinal) -lt 0) {
        throw "Preflight did not render the approved digest for $($item.Release)."
    }
    if ($Environment -eq 'production' -and $item.Platform -and $item.Release -ne 'tenantwright-cert-manager' -and
        $rendered -notmatch '(?m)^\s+storageClassName:\s+\S+') {
        throw "Production preflight for $($item.Release) must render an explicit storageClassName."
    }
    $item.ValuesArguments = $arguments
    $item.TargetNamespace = $targetNamespace
}
if ($Environment -eq 'production') {
    if (-not (Test-Path -LiteralPath $allocationPath -PathType Leaf)) { throw 'A reviewed postgresql-pools.yaml is required.' }
    Assert-PostgreSqlPoolAllocation -AllocationDocument (Read-EnvironmentValues -Paths @($allocationPath)) -RuntimeAllocations $runtimeAllocations
} else {
    # Verify private endpoint/policy ownership and direct-pool budgets without a cluster read.
    & (Join-Path $chartsRoot 'verify-direct-connection-profile.ps1') -EnvironmentValuesDirectory $environmentRoot
}
if ($PreflightOnly -or $DryRun) {
    Write-Host "$Environment Helm preflight passed for $($orderedCharts.Count) releases. No cluster changes were made." -ForegroundColor Green
    return
}

Assert-CommandExists -CommandName 'kubectl'
if ($CreateNamespace) {
    foreach ($targetNamespace in @($orderedCharts.TargetNamespace | Select-Object -Unique)) {
        $namespaceYaml = (& kubectl create namespace $targetNamespace --dry-run=client -o yaml 2>&1) -join "`n"
        if ($LASTEXITCODE -ne 0) { throw "Could not prepare namespace $targetNamespace." }
        $namespaceYaml | & kubectl apply -f - | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Could not apply namespace $targetNamespace." }
    }
}
if ($InstallCertManager) {
    $item = $orderedCharts | Where-Object { $_.Release -eq 'tenantwright-cert-manager' }
    $certManagerArguments = @('upgrade', '--install', $item.Release, $item.Path, '-n', $item.TargetNamespace) +
        $item.ValuesArguments + @('--wait', '--timeout', '10m')
    & $HelmPath @certManagerArguments
    if ($LASTEXITCODE -ne 0) { throw 'Cert-manager installation failed.' }
}
if ($InstallRabbitMqOperators) {
    & (Join-Path $chartsRoot 'install-rabbitmq-operators.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'RabbitMQ operator installation failed.' }
}
foreach ($crd in @('rabbitmqclusters.rabbitmq.com', 'users.rabbitmq.com', 'permissions.rabbitmq.com', 'vhosts.rabbitmq.com', 'policies.rabbitmq.com')) {
    & kubectl get customresourcedefinition $crd *> $null
    if ($LASTEXITCODE -ne 0) { throw "Required RabbitMQ Operator CRD is not installed: $crd" }
}
foreach ($item in $orderedCharts | Where-Object { $_.Release -ne 'tenantwright-cert-manager' }) {
    $arguments = @('upgrade', '--install', $item.Release, $item.Path, '-n', $item.TargetNamespace, '--reset-values') + $item.ValuesArguments
    if ($Wait) { $arguments += @('--wait', '--wait-for-jobs', '--timeout', '10m') }
    $output = (& $HelmPath @arguments 2>&1) -join "`n"
    if ($LASTEXITCODE -ne 0) { throw "Helm upgrade failed for $($item.Release); database changes are not reversed by Helm." }
    Write-Host "Applied $($item.Release) using reviewed $Environment inputs." -ForegroundColor Green
}
