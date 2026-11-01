[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$verificationImageDigest = 'sha256:' + ('0' * 64)
$services = @(
    'auditlogs',
    'auth',
    'identity',
    'notifications',
    'permissions',
    'tenant',
    'workflows'
)

# PowerShell wraps error records to the console width, and the wrap can fall inside a word: "application" arrives as
# "applic\nation" on a narrow terminal. Collapsing whitespace is therefore not enough — it has to be removed from both
# sides so the assertion is independent of the width it happens to run in. The phrase itself still has to be present.
function Test-ContainsPhrase {
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string] $Text,
        [Parameter(Mandatory)][string] $Phrase
    )

    $strippedText = [regex]::Replace($Text, '\s+', '')
    $strippedPhrase = [regex]::Replace($Phrase, '\s+', '')
    return $strippedText.IndexOf($strippedPhrase, [StringComparison]::Ordinal) -ge 0
}
foreach ($service in $services) {
    $chart = Join-Path $repoRoot "charts/tenantwright-$service"
    & helm lint $chart --set-string "image.digest=$verificationImageDigest"
    if ($LASTEXITCODE -ne 0) {
        throw "Helm lint failed for tenantwright-$service."
    }

    $directRender = (& helm template "tenantwright-$service-direct" $chart `
        --namespace tenantwright --set-string "image.digest=$verificationImageDigest" 2>&1) -join [Environment]::NewLine
    if ($LASTEXITCODE -ne 0) {
        throw "Direct PostgreSQL render failed for tenantwright-$service.`n$directRender"
    }

    foreach ($evidence in @(
        'PostgreSql__Runtime__RequireExplicitConfiguration: "true"',
        'PostgreSql__Runtime__Mode: "Direct"',
        'PostgreSql__Runtime__BackendPoolSize: "0"',
        'PostgreSql__Runtime__ClientPool__MaximumPoolSize: "4"',
        'PostgreSql__Runtime__ClientPool__ConnectionLifetimeSeconds: "300"'
    )) {
        if ($directRender.IndexOf($evidence, [StringComparison]::Ordinal) -lt 0) {
            throw "Direct PostgreSQL render for tenantwright-$service is missing: $evidence"
        }
    }

    $pooledArguments = @(
        'template',
        "tenantwright-$service-pooled",
        $chart,
        '--namespace',
        'tenantwright',
        '--set-string',
        "image.digest=$verificationImageDigest",
        '--set',
        'env.PostgreSql__Runtime__Mode=TransactionPool',
        '--set-string',
        'env.PostgreSql__Runtime__BackendPoolSize=4'
    )
    if ($service -eq 'auth') {
        $pooledArguments += @(
            '--set-string',
            'env.Auth__Quartz__AutoCreateSchema=false'
        )
    }

    $pooledRender = (& helm @pooledArguments 2>&1) -join [Environment]::NewLine
    if ($LASTEXITCODE -ne 0) {
        throw "Transaction-pool render failed for tenantwright-$service.`n$pooledRender"
    }
    foreach ($evidence in @(
        'PostgreSql__Runtime__Mode: "TransactionPool"',
        'PostgreSql__Runtime__BackendPoolSize: "4"'
    )) {
        if ($pooledRender.IndexOf($evidence, [StringComparison]::Ordinal) -lt 0) {
            throw "Transaction-pool render for tenantwright-$service is missing: $evidence"
        }
    }
    if ($service -eq 'auth' -and
        $pooledRender.IndexOf('Auth__Quartz__AutoCreateSchema: "false"', [StringComparison]::Ordinal) -lt 0) {
        throw 'Auth transaction-pool render must disable runtime Quartz schema creation.'
    }

    $productionExample = Join-Path $repoRoot "charts/environments/production/backend/$service.values.yaml.example"
    $productionSource = Get-Content -LiteralPath $productionExample -Raw
    foreach ($evidence in @(
        'PostgreSql__Runtime__Mode: TransactionPool',
        'PostgreSql__Runtime__BackendPoolSize: <measured-pgbouncer-backend-pool-size>'
    )) {
        if ($productionSource.IndexOf($evidence, [StringComparison]::Ordinal) -lt 0) {
            throw "Production values example for tenantwright-$service is missing: $evidence"
        }
    }
}

$authExample = Get-Content -LiteralPath (
    Join-Path $repoRoot 'charts/environments/production/backend/auth.values.yaml.example') -Raw
if ($authExample.IndexOf("Auth__Quartz__AutoCreateSchema: 'false'", [StringComparison]::Ordinal) -lt 0) {
    throw 'Auth production values must disable runtime Quartz schema creation.'
}

$allocationExample = Get-Content -LiteralPath (
    Join-Path $repoRoot 'charts/environments/production/postgresql-pools.yaml.example') -Raw
foreach ($service in $services) {
    if ($allocationExample.IndexOf("tenantwright-${service}:", [StringComparison]::Ordinal) -lt 0) {
        throw "PostgreSQL pool allocation example is missing tenantwright-$service."
    }
}
foreach ($evidence in @(
    'providerConnectionPoolingAvailable:',
    'providerConnectionLimit:',
    'reservedBackendConnections:',
    'backendPoolSize:'
)) {
    if ($allocationExample.IndexOf($evidence, [StringComparison]::Ordinal) -lt 0) {
        throw "PostgreSQL pool allocation example is missing: $evidence"
    }
}

$installerPath = Join-Path $repoRoot 'charts/helm-install-all.ps1'
$installerSource = Get-Content -LiteralPath $installerPath -Raw
$null = [scriptblock]::Create($installerSource)
foreach ($evidence in @(
    'PostgreSqlPoolAllocationPath',
    'Assert-PostgreSqlRuntimeEnvironmentOwnership',
    'Assert-PostgreSqlPoolAllocation',
    'verified provider connection-pooling availability',
    'Auth__Quartz__AutoCreateSchema=false',
    'exceeds the application budget'
)) {
    if ($installerSource.IndexOf($evidence, [StringComparison]::Ordinal) -lt 0) {
        throw "Production installer is missing PostgreSQL pool gate: $evidence"
    }
}

$budgetPath = Join-Path $repoRoot 'charts/configure-postgresql-pool-budgets.ps1'
$budgetSource = Get-Content -LiteralPath $budgetPath -Raw
$null = [scriptblock]::Create($budgetSource)
foreach ($evidence in @(
    'ConnectionMode',
    'ExpectedPort',
    'Pooling=true',
    'Connection Lifetime',
    'Command Timeout',
    'PostgreSQL connection strings were not printed.'
)) {
    if ($budgetSource.IndexOf($evidence, [StringComparison]::Ordinal) -lt 0) {
        throw "PostgreSQL client-pool budget script is missing: $evidence"
    }
}

$temporaryParent = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$temporaryRoot = Join-Path $temporaryParent "tenantwright-postgresql-preflight-$([Guid]::NewGuid().ToString('N'))"
$resolvedTemporaryRoot = [System.IO.Path]::GetFullPath($temporaryRoot)
if (-not $resolvedTemporaryRoot.StartsWith($temporaryParent, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Refusing to create PostgreSQL preflight fixtures outside the system temporary directory.'
}

try {
    New-Item -ItemType Directory -Path $resolvedTemporaryRoot | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $resolvedTemporaryRoot 'backend') | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $resolvedTemporaryRoot 'platform') | Out-Null
    Set-Content -LiteralPath (Join-Path $resolvedTemporaryRoot 'platform/valkey.values.yaml') `
        -Value "valkey:`n  dataStorage:`n    enabled: true`n    requestedSize: 1Gi`n    className: local-path`n    keepPvc: true`n"
    Set-Content -LiteralPath (Join-Path $resolvedTemporaryRoot 'platform/rabbitmq.values.yaml') `
        -Value "cluster:`n  persistence:`n    storageClassName: local-path`n"

    $selectedServices = @(
        'permissions',
        'identity',
        'tenant',
        'auditlogs',
        'notifications',
        'auth'
    )
    foreach ($service in $selectedServices) {
        $examplePath = Join-Path $repoRoot (
            "charts/environments/production/backend/$service.values.yaml.example")
        $values = (Get-Content -LiteralPath $examplePath -Raw).
            Replace('<production-auth-host>', 'auth.production.test').
            Replace('<production-api-host>', 'api.production.test').
            Replace('<production-permissions-host>', 'permissions.production.test').
            Replace('<measured-pgbouncer-backend-pool-size>', '4').
            Replace('<ingress-controller-namespace>', 'kube-system').
            Replace('<postgresql-port>', '6432').
            Replace('<postgresql-private-host>', 'postgresql.production.test').
            Replace('<postgresql-certificate-hostname>', 'postgresql.production.test').
            Replace('<reviewed-postgresql-egress-cidr>', '192.0.2.10/32').
            Replace('<reviewed-auth-egress-cidr>', '192.0.2.11/32').
            Replace('<reviewed-cert-manager-cluster-issuer>', 'tenantwright-production').
            Replace('<permissions-tls-secret-name>', 'permissions-tls').
            Replace('<production-storage-class>', 'local-path').
            Replace('<approved-restore-drill-record>', 'synthetic-preflight-only').
            Replace('<frontend-host>', 'app.production.test')
        if ($values -match '<[^>]+>') {
            throw "Synthetic values for tenantwright-$service retain a placeholder."
        }
        Set-Content -LiteralPath (
            Join-Path $resolvedTemporaryRoot "backend/$service.values.yaml") -Value $values
    }

    $gatewayValues = (Get-Content -LiteralPath (
        Join-Path $repoRoot 'charts/environments/production/backend/gateway.values.yaml.example') -Raw).
        Replace('<production-auth-host>', 'auth.production.test').
        Replace('<frontend-host>', 'app.production.test').
        Replace('<production-api-host>', 'api.production.test').
        Replace('<ingress-controller-namespace>', 'kube-system')
    Set-Content -LiteralPath (Join-Path $resolvedTemporaryRoot 'backend/gateway.values.yaml') `
        -Value $gatewayValues

    $digestLines = @('images:')
    foreach ($service in @($selectedServices + 'gateway')) {
        $digestLines += "  tenantwright-${service}: sha256:$('0' * 64)"
    }
    Set-Content -LiteralPath (Join-Path $resolvedTemporaryRoot 'digests.yaml') -Value $digestLines

    $allocationLines = @(
        'providerConnectionPoolingAvailable: true',
        'providerConnectionLimit: 100',
        'reservedBackendConnections: 20',
        'services:'
    )
    foreach ($service in $selectedServices) {
        $allocationLines += "  tenantwright-${service}:"
        $allocationLines += '    mode: TransactionPool'
        $allocationLines += '    backendPoolSize: 4'
    }
    Set-Content -LiteralPath (Join-Path $resolvedTemporaryRoot 'allocation.yaml') -Value $allocationLines

    & powershell -ExecutionPolicy Bypass -File $installerPath `
        -PreflightOnly `
        -EnvironmentValuesDirectory $resolvedTemporaryRoot `
        -ImageDigestsPath (Join-Path $resolvedTemporaryRoot 'digests.yaml') `
        -PostgreSqlPoolAllocationPath (Join-Path $resolvedTemporaryRoot 'allocation.yaml')
    if ($LASTEXITCODE -ne 0) {
        throw 'Synthetic production PostgreSQL pool preflight failed.'
    }

    $allocationLines[1] = 'providerConnectionLimit: 40'
    Set-Content -LiteralPath (Join-Path $resolvedTemporaryRoot 'allocation.yaml') -Value $allocationLines
    $priorErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $overAllocationOutput = (& powershell -ExecutionPolicy Bypass -File $installerPath `
            -PreflightOnly `
            -EnvironmentValuesDirectory $resolvedTemporaryRoot `
            -ImageDigestsPath (Join-Path $resolvedTemporaryRoot 'digests.yaml') `
            -PostgreSqlPoolAllocationPath (Join-Path $resolvedTemporaryRoot 'allocation.yaml') `
            2>&1) -join [Environment]::NewLine
        $overAllocationExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $priorErrorActionPreference
    }
    if ($overAllocationExitCode -eq 0 -or
        -not (Test-ContainsPhrase -Text $overAllocationOutput -Phrase 'exceeds the application budget')) {
        throw 'Production preflight did not reject a PostgreSQL backend over-allocation.'
    }

    $allocationLines[1] = 'providerConnectionLimit: 100'
    Set-Content -LiteralPath (Join-Path $resolvedTemporaryRoot 'allocation.yaml') -Value $allocationLines
    $authValuesPath = Join-Path $resolvedTemporaryRoot 'backend/auth.values.yaml'
    $unsafeAuthValues = (Get-Content -LiteralPath $authValuesPath -Raw).
        Replace("Auth__Quartz__AutoCreateSchema: 'false'", "Auth__Quartz__AutoCreateSchema: 'true'")
    Set-Content -LiteralPath $authValuesPath -Value $unsafeAuthValues
    try {
        $ErrorActionPreference = 'Continue'
        $unsafeAuthOutput = (& powershell -ExecutionPolicy Bypass -File $installerPath `
            -PreflightOnly `
            -EnvironmentValuesDirectory $resolvedTemporaryRoot `
            -ImageDigestsPath (Join-Path $resolvedTemporaryRoot 'digests.yaml') `
            -PostgreSqlPoolAllocationPath (Join-Path $resolvedTemporaryRoot 'allocation.yaml') `
            2>&1) -join [Environment]::NewLine
        $unsafeAuthExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $priorErrorActionPreference
    }
    if ($unsafeAuthExitCode -eq 0 -or
        -not (Test-ContainsPhrase -Text $unsafeAuthOutput -Phrase 'tenantwright-auth must set Auth__Quartz__AutoCreateSchema=false')) {
        throw 'Production preflight did not reject Auth runtime Quartz schema creation through transaction pooling.'
    }

    $safeAuthValues = $unsafeAuthValues.
        Replace("Auth__Quartz__AutoCreateSchema: 'true'", "Auth__Quartz__AutoCreateSchema: 'false'")
    Set-Content -LiteralPath $authValuesPath -Value $safeAuthValues
    $allocationLines[0] = 'providerConnectionPoolingAvailable: false'
    Set-Content -LiteralPath (Join-Path $resolvedTemporaryRoot 'allocation.yaml') -Value $allocationLines
    try {
        $ErrorActionPreference = 'Continue'
        $unsupportedProviderOutput = (& powershell -ExecutionPolicy Bypass -File $installerPath `
            -PreflightOnly `
            -EnvironmentValuesDirectory $resolvedTemporaryRoot `
            -ImageDigestsPath (Join-Path $resolvedTemporaryRoot 'digests.yaml') `
            -PostgreSqlPoolAllocationPath (Join-Path $resolvedTemporaryRoot 'allocation.yaml') `
            2>&1) -join [Environment]::NewLine
        $unsupportedProviderExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $priorErrorActionPreference
    }
    if ($unsupportedProviderExitCode -eq 0 -or
        -not (Test-ContainsPhrase -Text $unsupportedProviderOutput -Phrase 'verified provider connection-pooling availability')) {
        throw 'Production preflight did not reject a provider plan without connection pooling.'
    }
}
finally {
    if (Test-Path -LiteralPath $resolvedTemporaryRoot) {
        $verifiedRemovalTarget = [System.IO.Path]::GetFullPath($resolvedTemporaryRoot)
        if (-not $verifiedRemovalTarget.StartsWith($temporaryParent, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Refusing to remove a PostgreSQL preflight path outside the system temporary directory.'
        }
        Remove-Item -LiteralPath $verifiedRemovalTarget -Recurse -Force
    }
}

Write-Host 'PostgreSQL direct/transaction-pool chart and operator gates passed.' -ForegroundColor Green
