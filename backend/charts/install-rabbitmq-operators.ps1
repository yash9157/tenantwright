[CmdletBinding()]
param(
    [string]$Namespace = 'rabbitmq-system',
    [switch]$VerifyOnly
)

$ErrorActionPreference = 'Stop'

$clusterOperatorVersion = 'v2.23.0'
$clusterOperatorSha256 = 'e99f04f46ecbf796b63c0f0e295bbae3d2aee3742897944cd1810cfcc44856ce'
$clusterOperatorImage = 'ghcr.io/rabbitmq/cluster-operator:2.23.0'
$clusterOperatorImageDigest = 'sha256:9236fb4f559bb6c4a04605fdf9aa1c03d00573df2d4da43c71e11b296e0a1886'
$topologyOperatorVersion = 'v1.20.3'
$topologyOperatorSha256 = '73a45a423b3abb9dddfa84248e60699f69a4dbcf3456830b9af62ef496e1e549'
$topologyOperatorImage = 'ghcr.io/rabbitmq/messaging-topology-operator:1.20.3'
$topologyOperatorImageDigest = 'sha256:f377d3c3e2215a8dfb8a7a5ab953ef7f180f295c401e5d2c2060f3b0748fefd6'

function Assert-CommandExists {
    param([Parameter(Mandatory = $true)][string]$CommandName)

    if (-not (Get-Command $CommandName -ErrorAction SilentlyContinue)) {
        throw "Required command not found: $CommandName"
    }
}

function Get-VerifiedManifest {
    param(
        [Parameter(Mandatory = $true)][string]$Uri,
        [Parameter(Mandatory = $true)][string]$ExpectedSha256,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    Invoke-WebRequest -UseBasicParsing -Uri $Uri -OutFile $Destination
    $actualSha256 = (Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualSha256 -ne $ExpectedSha256) {
        throw "Downloaded operator manifest digest mismatch for $Uri."
    }
}

function Set-PinnedImageDigest {
    param(
        [Parameter(Mandatory = $true)][string]$ManifestPath,
        [Parameter(Mandatory = $true)][string]$ExpectedImage,
        [Parameter(Mandatory = $true)][string]$Digest
    )

    $content = Get-Content -LiteralPath $ManifestPath -Raw
    $occurrences = ([regex]::Matches($content, [regex]::Escape($ExpectedImage))).Count
    if ($occurrences -ne 1) {
        throw "Pinned operator manifest must contain exactly one expected image reference: $ExpectedImage"
    }

    $pinnedContent = $content.Replace($ExpectedImage, "$ExpectedImage@$Digest")
    [System.IO.File]::WriteAllText(
        $ManifestPath,
        $pinnedContent,
        [System.Text.UTF8Encoding]::new($false))
}

function Set-OperatorResourceRequests {
    param(
        [Parameter(Mandatory = $true)][string]$ManifestPath,
        [Parameter(Mandatory = $true)][string]$DeploymentName,
        [Parameter(Mandatory = $true)][string]$ContainerName,
        [Parameter(Mandatory = $true)][string]$ExpectedLimitCpu,
        [Parameter(Mandatory = $true)][string]$ExpectedLimitMemory,
        [Parameter(Mandatory = $true)][string]$ExpectedRequestCpu,
        [Parameter(Mandatory = $true)][string]$ExpectedRequestMemory,
        [Parameter(Mandatory = $true)][string]$RequestCpu,
        [Parameter(Mandatory = $true)][string]$RequestMemory
    )

    $content = Get-Content -LiteralPath $ManifestPath -Raw
    $containerMarker = "        name: $ContainerName"
    $deploymentPattern = "(?ms)^kind: Deployment\r?$\n(?:(?!^---\r?$).)*?^  name: $([regex]::Escape($DeploymentName))\r?$"
    if (([regex]::Matches($content, $deploymentPattern)).Count -ne 1) {
        throw "Pinned operator manifest must contain exactly one deployment named '$DeploymentName'."
    }
    if (([regex]::Matches($content, "(?m)^$([regex]::Escape($containerMarker))\r?$" )).Count -ne 1) {
        throw "Pinned operator manifest must contain exactly one container named '$ContainerName'."
    }

    $expectedResources = @"
        resources:
          limits:
            cpu: $ExpectedLimitCpu
            memory: $ExpectedLimitMemory
          requests:
            cpu: $ExpectedRequestCpu
            memory: $ExpectedRequestMemory
"@
    $desiredResources = @"
        resources:
          limits:
            cpu: $ExpectedLimitCpu
            memory: $ExpectedLimitMemory
          requests:
            cpu: $RequestCpu
            memory: $RequestMemory
"@
    $expectedResources = $expectedResources.Replace("`r`n", "`n")
    $desiredResources = $desiredResources.Replace("`r`n", "`n")
    $normalizedContent = $content.Replace("`r`n", "`n")
    $occurrences = ([regex]::Matches(
            $normalizedContent,
            [regex]::Escape($expectedResources))).Count
    if ($occurrences -ne 1) {
        throw "Pinned operator manifest resource shape drifted for deployment '$DeploymentName'."
    }

    $updatedContent = $normalizedContent.Replace($expectedResources, $desiredResources)
    if (([regex]::Matches($updatedContent, [regex]::Escape($desiredResources))).Count -ne 1) {
        throw "Pinned operator manifest resource request customization failed for deployment '$DeploymentName'."
    }

    [System.IO.File]::WriteAllText(
        $ManifestPath,
        $updatedContent,
        [System.Text.UTF8Encoding]::new($false))
}

Assert-CommandExists -CommandName 'kubectl'
if ($Namespace -ne 'rabbitmq-system') {
    throw "The pinned official manifests own namespace 'rabbitmq-system'; a different namespace is not supported."
}

$temporaryDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "tenantwright-rabbitmq-operators-$([Guid]::NewGuid().ToString('N'))"
$null = New-Item -ItemType Directory -Path $temporaryDirectory

try {
    $clusterManifest = Join-Path $temporaryDirectory 'cluster-operator.yml'
    $topologyManifest = Join-Path $temporaryDirectory 'messaging-topology-operator.yaml'

    Get-VerifiedManifest `
        -Uri "https://github.com/rabbitmq/cluster-operator/releases/download/$clusterOperatorVersion/cluster-operator.yml" `
        -ExpectedSha256 $clusterOperatorSha256 `
        -Destination $clusterManifest
    Get-VerifiedManifest `
        -Uri "https://github.com/rabbitmq/messaging-topology-operator/releases/download/$topologyOperatorVersion/messaging-topology-operator-with-certmanager.yaml" `
        -ExpectedSha256 $topologyOperatorSha256 `
        -Destination $topologyManifest
    Set-PinnedImageDigest `
        -ManifestPath $clusterManifest `
        -ExpectedImage $clusterOperatorImage `
        -Digest $clusterOperatorImageDigest
    Set-PinnedImageDigest `
        -ManifestPath $topologyManifest `
        -ExpectedImage $topologyOperatorImage `
        -Digest $topologyOperatorImageDigest
    Set-OperatorResourceRequests `
        -ManifestPath $clusterManifest `
        -DeploymentName 'rabbitmq-cluster-operator' `
        -ContainerName 'operator' `
        -ExpectedLimitCpu '200m' `
        -ExpectedLimitMemory '500Mi' `
        -ExpectedRequestCpu '200m' `
        -ExpectedRequestMemory '500Mi' `
        -RequestCpu '100m' `
        -RequestMemory '256Mi'
    Set-OperatorResourceRequests `
        -ManifestPath $topologyManifest `
        -DeploymentName 'messaging-topology-operator' `
        -ContainerName 'manager' `
        -ExpectedLimitCpu '500m' `
        -ExpectedLimitMemory '512Mi' `
        -ExpectedRequestCpu '300m' `
        -ExpectedRequestMemory '128Mi' `
        -RequestCpu '100m' `
        -RequestMemory '128Mi'

    if ($VerifyOnly) {
        Write-Host "RabbitMQ operator manifests passed pinned SHA-256 and resource request verification." -ForegroundColor Green
        return
    }

    & kubectl apply --server-side --field-manager tenantwright-platform -f $clusterManifest
    if ($LASTEXITCODE -ne 0) {
        throw 'RabbitMQ Cluster Operator installation failed.'
    }
    & kubectl -n $Namespace rollout status deployment/rabbitmq-cluster-operator --timeout=5m
    if ($LASTEXITCODE -ne 0) {
        throw 'RabbitMQ Cluster Operator did not become available.'
    }

    & kubectl get customresourcedefinition certificates.cert-manager.io *> $null
    if ($LASTEXITCODE -ne 0) {
        throw 'cert-manager CRDs must be installed before the RabbitMQ Messaging Topology Operator.'
    }

    & kubectl apply --server-side --field-manager tenantwright-platform -f $topologyManifest
    if ($LASTEXITCODE -ne 0) {
        throw 'RabbitMQ Messaging Topology Operator installation failed.'
    }
    & kubectl -n $Namespace rollout status deployment/messaging-topology-operator --timeout=5m
    if ($LASTEXITCODE -ne 0) {
        throw 'RabbitMQ Messaging Topology Operator did not become available.'
    }
}
finally {
    if (Test-Path -LiteralPath $temporaryDirectory) {
        Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force
    }
}
