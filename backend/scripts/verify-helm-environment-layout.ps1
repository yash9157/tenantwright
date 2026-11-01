[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$chartsRoot = Join-Path (Split-Path -Parent $PSScriptRoot) 'charts'
. (Join-Path $chartsRoot 'environment-values.ps1')
if (-not (Get-Command ConvertFrom-Yaml -ErrorAction SilentlyContinue)) {
    throw 'ConvertFrom-Yaml is required for environment verification.'
}

function Assert-Condition {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

$fixtureParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$fixtureRoot = Join-Path $fixtureParent ('tenantwright-environment-' + [guid]::NewGuid().ToString('N'))
try {
    foreach ($directory in @('backend', 'platform', 'private/backend')) {
        New-Item -ItemType Directory -Path (Join-Path $fixtureRoot $directory) -Force | Out-Null
    }
    Set-Content (Join-Path $fixtureRoot 'backend/common.values.yaml') @'
env:
  PostgreSql__Runtime__Mode: Direct
  PostgreSql__Runtime__BackendPoolSize: 0
networkPolicy:
  ingressController:
    namespace: kube-system
'@
    Set-Content (Join-Path $fixtureRoot 'backend/messaging.values.yaml') 'messaging: {provider: MassTransit}'
    Set-Content (Join-Path $fixtureRoot 'backend/permissions.values.yaml') @'
env:
  EfCore__AutoMigrate: 'false'
networkPolicy:
  enabled: true
'@
    Set-Content (Join-Path $fixtureRoot 'private/backend/permissions.values.local.yaml') @'
networkPolicy:
  postgresql:
    enabled: true
    port: 6432
    cidrs: [192.0.2.10/32]
'@
    $files = @(Get-EnvironmentValuesFiles -EnvironmentRoot $fixtureRoot -ReleaseName 'tenantwright-permissions' -EnableMessaging)
    $values = Read-EnvironmentValues -Paths $files
    Assert-Condition ($files.Count -eq 5) 'Expected common, trust, messaging, component and private overlays.'
    Assert-Condition ($files[-1].EndsWith('permissions.values.local.yaml')) 'Private overlay must be last.'
    Assert-Condition ($values.env.PostgreSql__Runtime__Mode -eq 'Direct') 'Common runtime configuration was lost.'
    Assert-Condition ($values.env.EfCore__AutoMigrate -eq 'false') 'Component migration setting was lost.'
    Assert-Condition ($values.networkPolicy.ingressController.namespace -eq 'kube-system') 'Ingress ownership was lost.'
    Assert-Condition ($values.networkPolicy.postgresql.port -eq 6432) 'Private PostgreSQL policy was lost.'
    Assert-Condition ($values.messaging.provider -eq 'MassTransit') 'Messaging switch was lost.'

    $merged = Merge-EnvironmentValues -Base @{ env = @{ first = 'keep'; second = 'replace' }; cidrs = @('192.0.2.1/32') } `
        -Overlay @{ env = @{ second = 'new' }; cidrs = @('192.0.2.2/32') }
    Assert-Condition ($merged.env.first -eq 'keep' -and $merged.env.second -eq 'new') 'Nested values must merge.'
    Assert-Condition ($merged.cidrs.Count -eq 1 -and $merged.cidrs[0] -eq '192.0.2.2/32') 'Policy arrays must replace rather than widen.'

    Set-Content (Join-Path $fixtureRoot 'backend/gateway.values.yaml') 'networkPolicy: {enabled: true}'
    $gatewayFiles = @(Get-EnvironmentValuesFiles -EnvironmentRoot $fixtureRoot -ReleaseName 'tenantwright-gateway' -EnableMessaging)
    Assert-Condition ($gatewayFiles.Count -eq 1) 'Gateway must not receive database or messaging overlays.'
    Set-Content (Join-Path $fixtureRoot 'platform/valkey.values.yaml') 'valkey: {architecture: standalone}'
    $platformFiles = @(Get-EnvironmentValuesFiles -EnvironmentRoot $fixtureRoot -ReleaseName 'tenantwright-valkey' -Platform)
    Assert-Condition ($platformFiles.Count -eq 1) 'Platform must not receive backend overlays.'

    foreach ($release in @('tenantwright-missing', 'tenantwright-../private')) {
        $rejected = $false
        try { Get-EnvironmentValuesFiles -EnvironmentRoot $fixtureRoot -ReleaseName $release | Out-Null }
        catch { $rejected = $true }
        Assert-Condition $rejected 'Missing or invalid component input must fail closed.'
    }
    Set-Content (Join-Path $fixtureRoot 'backend/gateway.values.yaml') 'ingress: {host: <production-host>}'
    $rejected = $false
    try { Read-EnvironmentValues -Paths @($gatewayFiles) | Out-Null }
    catch { $rejected = $true }
    Assert-Condition $rejected 'Incomplete production values must fail closed.'

    $parameters = (Get-Command (Join-Path $chartsRoot 'helm-install-all.ps1')).Parameters
    Assert-Condition ($parameters.ContainsKey('Environment') -and -not $parameters.ContainsKey('UseLatestImages')) `
        'Environment policy must be independent of image selection.'
    $backendPins = Get-Content (Join-Path $chartsRoot 'environments/dev/images.lock.yaml') -Raw | ConvertFrom-Yaml
    Assert-Condition ($backendPins.images.Count -eq 8) 'Expected the eight backend workload restoration pins.'
    foreach ($digest in $backendPins.images.Values) {
        Assert-Condition ($digest -match '^sha256:[0-9a-f]{64}$') 'Restoration pins must be immutable digests.'
    }
    foreach ($invalidDigest in @('latest', 'sha256:abc', ('sha256:' + ('g' * 64)))) {
        $invalidLock = Join-Path $fixtureRoot 'images.lock.yaml'
        Set-Content $invalidLock "images:`n  tenantwright-permissions: $invalidDigest"
        $rejected = $false
        try {
            & (Join-Path $chartsRoot 'helm-install-all.ps1') -Environment dev -ImageDigestsPath $invalidLock -PreflightOnly *> $null
        }
        catch { $rejected = $_.Exception.Message -match 'invalid immutable image digest' }
        Assert-Condition $rejected 'Installer must reject malformed digests before rendering or deployment.'
    }
    Write-Host 'Helm environment resolver, failure paths and restoration lock checks passed.' -ForegroundColor Green
}
finally {
    $resolvedFixture = [IO.Path]::GetFullPath($fixtureRoot)
    if (-not $resolvedFixture.StartsWith($fixtureParent.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing to remove an environment fixture outside the temporary directory.'
    }
    if (Test-Path -LiteralPath $resolvedFixture) { Remove-Item -LiteralPath $resolvedFixture -Recurse -Force }
}
