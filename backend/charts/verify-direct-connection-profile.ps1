[CmdletBinding()]
param(
    [string]$TopologyPath = "",
    [string]$EnvironmentValuesDirectory = "",
    [string]$Namespace = "tenantwright",
    [switch]$CheckClusterSecrets,
    # Egress CIDR drift is checked by default: every CIDR previously committed here
    # had gone stale, which denies all pod egress to PostgreSQL while looking fine.
    [switch]$SkipEgressCidrCheck
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-PropertyValue {
    param(
        [Parameter(Mandatory)]$Object,
        [Parameter(Mandatory)][string]$Name
    )

    if ($Object -is [System.Collections.IDictionary]) {
        return $Object[$Name]
    }

    return $Object.PSObject.Properties[$Name].Value
}

function Assert-Equal {
    param(
        [Parameter(Mandatory)]$Actual,
        [Parameter(Mandatory)]$Expected,
        [Parameter(Mandatory)][string]$Message
    )

    if ($Actual -ne $Expected) {
        throw "$Message Expected '$Expected', found '$Actual'."
    }
}

function Get-ConnectionStringSetting {
    param(
        [Parameter(Mandatory)][string]$ConnectionString,
        [Parameter(Mandatory)][string[]]$Names
    )

    foreach ($part in $ConnectionString.Split(
        ";",
        [StringSplitOptions]::RemoveEmptyEntries)) {
        $separator = $part.IndexOf("=")
        if ($separator -le 0) {
            continue
        }

        $name = $part.Substring(0, $separator).Trim()
        if ($Names -contains $name) {
            return $part.Substring($separator + 1).Trim()
        }
    }

    return $null
}

if (-not (Get-Command ConvertFrom-Yaml -ErrorAction SilentlyContinue)) {
    throw "ConvertFrom-Yaml is required. Install the powershell-yaml module."
}

. (Join-Path $PSScriptRoot 'environment-values.ps1')
$profileRoot = if ([string]::IsNullOrWhiteSpace($EnvironmentValuesDirectory)) {
    Join-Path $PSScriptRoot 'environments/dev'
} else { [IO.Path]::GetFullPath($EnvironmentValuesDirectory) }
if ([string]::IsNullOrWhiteSpace($TopologyPath)) {
    $TopologyPath = Join-Path $profileRoot "private/postgresql.local.yaml"
}

if (-not (Test-Path -LiteralPath $TopologyPath -PathType Leaf)) {
    throw "Local topology file not found: $TopologyPath"
}

$topologyRaw = Get-Content -LiteralPath $TopologyPath -Raw
if ($topologyRaw -match "(?i)postgres(?:ql)?://|password\s*:|connection-?string\s*:") {
    throw "The topology file contains a forbidden password, URI, or connection-string field."
}

$topology = ConvertFrom-Yaml -Yaml $topologyRaw
Assert-Equal (Get-PropertyValue $topology "providerConnectionLimit") 20 `
    "The free-tier provider limit is fixed by this profile."
Assert-Equal (Get-PropertyValue $topology "providerReserve") 4 `
    "The development profile must retain four provider connections."
Assert-Equal (Get-PropertyValue $topology "maximumApplicationPoolSize") 4 `
    "Each application process must retain the approved Npgsql cap."
Assert-Equal (Get-PropertyValue $topology "maximumApplicationReplicas") 2 `
    "Each application HPA must retain the approved replica cap."

$slots = Get-PropertyValue $topology "slots"

# Five instances. Slot letters are stable identifiers: a slot keeps its letter
# even if the service pairing later changes, so history stays readable.
$expectedAssignments = [ordered]@{
    "application-a" = @("auth", "identity")
    "application-b" = @("tenant")
    "application-c" = @("permissions", "auditlogs")
    "application-d" = @("notifications", "workflows")
    "temporal" = @("temporal", "temporal-visibility")
}
$endpointOwners = @{}
$slotEgress = @{}

foreach ($slotName in $expectedAssignments.Keys) {
    $slot = Get-PropertyValue $slots $slotName
    if ($null -eq $slot) {
        throw "Topology is missing provider slot '$slotName'."
    }

    $hostName = [string](Get-PropertyValue $slot "host")
    $port = [int](Get-PropertyValue $slot "port")
    if ([string]::IsNullOrWhiteSpace($hostName) -or
        $hostName -match "(?i)example|invalid|change.?me" -or
        $port -lt 1 -or
        $port -gt 65535) {
        throw "Provider slot '$slotName' has an invalid endpoint."
    }

    $endpointKey = "$hostName`:$port"
    if ($endpointOwners.ContainsKey($endpointKey)) {
        throw "Provider slots '$($endpointOwners[$endpointKey])' and '$slotName' share endpoint '$endpointKey'."
    }
    $endpointOwners[$endpointKey] = $slotName

    # The CA file must exist on disk: a missing or misnamed PEM is what breaks
    # SSL Mode=VerifyFull, and the failure only shows up at pod startup.
    $caFile = [string](Get-PropertyValue $slot "caFile")
    if ([string]::IsNullOrWhiteSpace($caFile)) {
        throw "Provider slot '$slotName' is missing 'caFile'."
    }
    $caPath = Join-Path $profileRoot (Join-Path 'private' $caFile)
    if (-not (Test-Path -LiteralPath $caPath -PathType Leaf)) {
        throw "Provider slot '$slotName' references CA file '$caFile', which does not exist at '$caPath'."
    }

    $egressCidr = [string](Get-PropertyValue $slot "egressCidr")
    if ([string]::IsNullOrWhiteSpace($egressCidr)) {
        throw "Provider slot '$slotName' is missing 'egressCidr'."
    }
    $slotEgress[$slotName] = @{ host = $hostName; cidr = $egressCidr }

    $services = Get-PropertyValue $slot "services"
    $serviceNames = @($services.Keys | Sort-Object)
    $expectedNames = @($expectedAssignments[$slotName] | Sort-Object)
    if (Compare-Object $serviceNames $expectedNames) {
        throw "Provider slot '$slotName' must own exactly: $($expectedNames -join ', ')."
    }

    foreach ($serviceName in $serviceNames) {
        $service = Get-PropertyValue $services $serviceName
        foreach ($requiredProperty in @(
            "database",
            "runtimeSecret",
            "schemaSecret",
            "caSecret")) {
            $value = [string](Get-PropertyValue $service $requiredProperty)
            if ([string]::IsNullOrWhiteSpace($value)) {
                throw "Service '$serviceName' is missing '$requiredProperty'."
            }
        }
    }

    $allocatedConnections = if ($slotName -eq "temporal") {
        16
    }
    else {
        $serviceNames.Count *
            [int](Get-PropertyValue $topology "maximumApplicationReplicas") *
            [int](Get-PropertyValue $topology "maximumApplicationPoolSize")
    }
    $availableConnections =
        [int](Get-PropertyValue $topology "providerConnectionLimit") -
        [int](Get-PropertyValue $topology "providerReserve")
    if ($allocatedConnections -gt $availableConnections) {
        throw "Provider slot '$slotName' allocates $allocatedConnections connections but only $availableConnections are available after reserve."
    }

    Write-Host "PASS ${slotName}: $allocatedConnections application connections; $availableConnections available after reserve."
}

# ---------------------------------------------------------------------------
# Egress CIDR drift.
#
# Each overlay pins a /32 for its database host so the NetworkPolicy permits
# egress. Managed providers rotate those addresses, and a stale /32 denies every database
# connection while every manifest still looks correct. This compares the topology
# and each overlay against live DNS.
# ---------------------------------------------------------------------------
$drift = @()
$resolvable = $true
if (-not $SkipEgressCidrCheck) {

    foreach ($slotName in $slotEgress.Keys) {
        $hostName = $slotEgress[$slotName].host
        try {
            $addresses = @([System.Net.Dns]::GetHostAddresses($hostName) |
                Where-Object { $_.AddressFamily -eq "InterNetwork" } |
                ForEach-Object { "$($_.IPAddressToString)/32" })
        }
        catch {
            $drift += "slot '$slotName' DNS could not be verified"
            $resolvable = $false
            continue
        }

        if ($addresses -notcontains $slotEgress[$slotName].cidr) {
            $drift += "topology slot '$slotName' pins $($slotEgress[$slotName].cidr) but '$hostName' resolves to $($addresses -join ', ')"
        }
    }

}

# Check only the PostgreSQL egress settings for the service's assigned slot.
# Other CIDRs in an overlay can belong to unrelated in-cluster dependencies.
foreach ($slotName in $expectedAssignments.Keys) {
    if ($slotName -eq "temporal") { continue }
    $slot = Get-PropertyValue $slots $slotName
    foreach ($serviceName in $expectedAssignments[$slotName]) {
        $overlayPath = Join-Path $profileRoot "backend/$serviceName.values.yaml"
        if (-not (Test-Path -LiteralPath $overlayPath -PathType Leaf)) {
            $drift += "service '$serviceName' is missing its environment overlay"
            continue
        }

        $overlay = Read-EnvironmentValues -Paths @(Get-EnvironmentValuesFiles -EnvironmentRoot $profileRoot -ReleaseName "tenantwright-$serviceName")
        $postgresql = Get-PropertyValue (Get-PropertyValue $overlay "networkPolicy") "postgresql"
        $cidrs = @(Get-PropertyValue $postgresql "cidrs")
        if ((Get-PropertyValue $postgresql "enabled") -ne $true -or
            [string](Get-PropertyValue $postgresql "port") -ne [string](Get-PropertyValue $slot "port") -or
            $cidrs.Count -ne 1 -or
            [string]$cidrs[0] -ne $slotEgress[$slotName].cidr) {
            $drift += "service '$serviceName' PostgreSQL egress must match slot '$slotName' port and exact CIDR"
        }
    }
}

$temporalOverlay = Read-EnvironmentValues -Paths @(Get-EnvironmentValuesFiles -EnvironmentRoot $profileRoot -ReleaseName 'tenantwright-temporal' -Platform)
$temporalPolicy = Get-PropertyValue $temporalOverlay "networkPolicy"
$temporalCidrs = @(Get-PropertyValue $temporalPolicy "postgresqlCidrs")
if ([string](Get-PropertyValue $temporalPolicy "postgresqlPort") -ne
    [string](Get-PropertyValue (Get-PropertyValue $slots "temporal") "port") -or
    $temporalCidrs.Count -ne 1 -or
    [string]$temporalCidrs[0] -ne $slotEgress["temporal"].cidr) {
    $drift += "Temporal PostgreSQL egress must match its slot port and exact CIDR"
}

if ($drift.Count -gt 0) {
    $detail = ($drift | Sort-Object -Unique) -join "`n  "
    throw "Egress CIDR drift detected. Pods would be denied egress to PostgreSQL:`n  $detail"
}

if (-not $SkipEgressCidrCheck -and $resolvable) {
    Write-Host "PASS egress CIDRs match live DNS for every slot and overlay."
}

# Budget checks apply to final inputs, independently of optional DNS verification.
foreach ($slotName in $expectedAssignments.Keys) {
    if ($slotName -eq 'temporal') { continue }
    foreach ($serviceName in $expectedAssignments[$slotName]) {
        $overlay = Read-EnvironmentValues -Paths @(Get-EnvironmentValuesFiles -EnvironmentRoot $profileRoot -ReleaseName "tenantwright-$serviceName")
        Assert-Equal (Get-PropertyValue $overlay 'replicaCount') 1 "Service '$serviceName' initial replica count is incorrect."
        Assert-Equal (Get-PropertyValue (Get-PropertyValue $overlay 'autoscaling') 'maxReplicas') 2 "Service '$serviceName' replica ceiling is incorrect."
        $runtime = Get-PropertyValue $overlay 'env'
        Assert-Equal (Get-PropertyValue $runtime 'PostgreSql__Runtime__Mode') 'Direct' "Service '$serviceName' runtime mode is incorrect."
        Assert-Equal ([string](Get-PropertyValue $runtime 'PostgreSql__Runtime__ClientPool__MaximumPoolSize')) '4' "Service '$serviceName' direct connection cap is incorrect."
        Assert-Equal ([string](Get-PropertyValue $runtime 'PostgreSql__Runtime__BackendPoolSize')) '0' "Service '$serviceName' direct backend pool is incorrect."
    }
}

$applicationValuesPath = Join-Path $profileRoot "backend/common.values.yaml"
$applicationValues = ConvertFrom-Yaml -Yaml (
    Get-Content -LiteralPath $applicationValuesPath -Raw)
Assert-Equal (Get-PropertyValue $applicationValues "replicaCount") 1 `
    "Development workloads must start at one replica."
Assert-Equal (Get-PropertyValue (Get-PropertyValue $applicationValues "autoscaling") "maxReplicas") 2 `
    "Development HPA maximum is incorrect."
$applicationEnvironment = Get-PropertyValue $applicationValues "env"
Assert-Equal (Get-PropertyValue $applicationEnvironment "PostgreSql__Runtime__Mode") "Direct" `
    "The Free profile cannot select PgBouncer."
Assert-Equal ([string](Get-PropertyValue $applicationEnvironment "PostgreSql__Runtime__BackendPoolSize")) "0" `
    "Direct mode must not declare a PgBouncer backend pool."
Assert-Equal ([string](Get-PropertyValue $applicationEnvironment "PostgreSql__Runtime__ClientPool__MaximumPoolSize")) "4" `
    "The Npgsql maximum is incorrect."

$temporalValues = ConvertFrom-Yaml -Yaml (
    Get-Content -LiteralPath (Join-Path $PSScriptRoot "tenantwright-temporal/values.yaml") -Raw)
$temporalOverrides = Read-EnvironmentValues -Paths @(Get-EnvironmentValuesFiles -EnvironmentRoot $profileRoot -ReleaseName 'tenantwright-temporal' -Platform)
$temporalValues = Merge-EnvironmentValues -Base $temporalValues -Overlay $temporalOverrides
$datastores = $temporalValues.temporal.server.config.persistence.datastores
Assert-Equal $datastores.default.sql.maxConns 8 `
    "Temporal persistence maximum is incorrect."
Assert-Equal $datastores.default.sql.maxIdleConns 1 `
    "Temporal persistence idle maximum is incorrect."
Assert-Equal $datastores.visibility.sql.maxConns 8 `
    "Temporal visibility maximum is incorrect."
Assert-Equal $datastores.visibility.sql.maxIdleConns 1 `
    "Temporal visibility idle maximum is incorrect."

# Every application slot needs a chart overlay, otherwise the release deploys
# with the chart default database port and no egress rule.
foreach ($slotName in $expectedAssignments.Keys) {
    if ($slotName -eq "temporal") { continue }
    foreach ($serviceName in $expectedAssignments[$slotName]) {
        $overlayPath = Join-Path $profileRoot "backend/$serviceName.values.yaml"
        if (-not (Test-Path -LiteralPath $overlayPath -PathType Leaf)) {
            throw "Service '$serviceName' has no environment overlay at '$overlayPath'."
        }
    }
}
Write-Host "PASS every application service has an environment overlay."

if ($CheckClusterSecrets) {
    if (-not (Get-Command kubectl -ErrorAction SilentlyContinue)) {
        throw "kubectl is required for -CheckClusterSecrets."
    }

    foreach ($slotName in $expectedAssignments.Keys) {
        $services = Get-PropertyValue (Get-PropertyValue $slots $slotName) "services"
        foreach ($serviceName in $services.Keys) {
            if ($slotName -eq "temporal") {
                continue
            }

            $runtimeSecret = [string](Get-PropertyValue (Get-PropertyValue $services $serviceName) "runtimeSecret")
            $encoded = & kubectl get secret $runtimeSecret -n $Namespace `
                -o "jsonpath={.data.connection-string}" 2>$null
            if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($encoded)) {
                throw "Runtime Secret '$runtimeSecret' is absent or missing connection-string."
            }

            $connectionString = [Text.Encoding]::UTF8.GetString(
                [Convert]::FromBase64String($encoded))
            $userName = Get-ConnectionStringSetting $connectionString @("Username", "User ID", "UserId")
            $maximumPoolSize = Get-ConnectionStringSetting $connectionString @("Maximum Pool Size", "MaxPoolSize")
            if ([string]::Equals($userName, "avnadmin", [StringComparison]::OrdinalIgnoreCase)) {
                throw "Runtime Secret '$runtimeSecret' still uses the forbidden administrator identity."
            }
            Assert-Equal $maximumPoolSize "4" `
                "Runtime Secret '$runtimeSecret' has the wrong Npgsql maximum."
        }
    }
    Write-Host "PASS cluster runtime Secrets use least-privilege identities with the approved pool cap."
}

Write-Host "Free-tier direct-connection development profile verification passed (5 instances)." -ForegroundColor Green
