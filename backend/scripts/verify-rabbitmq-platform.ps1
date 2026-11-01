[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$chart = Join-Path $repoRoot 'charts/tenantwright-rabbitmq'
$operatorInstaller = Join-Path $repoRoot 'charts/install-rabbitmq-operators.ps1'
$topologyTlsConfigurator = Join-Path $repoRoot 'charts/configure-rabbitmq-topology-tls.ps1'

& helm lint $chart
if ($LASTEXITCODE -ne 0) {
    throw 'RabbitMQ platform Helm lint failed.'
}

$render = (& helm template tenantwright-rabbitmq $chart --namespace tenantwright --set pki.enabled=true 2>&1) -join "`n"
if ($LASTEXITCODE -ne 0) {
    throw "RabbitMQ platform Helm rendering failed.`n$render"
}

$requiredEvidence = @(
    'kind: RabbitmqCluster',
    'replicas: 3',
    'rabbitmq:4.3.6-management@sha256:8dd6e3570ddaa2ef82a6c3a8950e79c1f73893adfbdddbc374fc1f1ac8a0f5dd',
    'disableNonTLSListeners: true',
    'secretName: tenantwright-rabbitmq-server-tls',
    'caSecretName: tenantwright-rabbitmq-ca',
    'kind: Certificate',
    'name: tenantwright-rabbitmq-root-ca',
    'name: tenantwright-rabbitmq-intermediate-ca',
    'name: tenantwright-rabbitmq-server',
    'commonName: "tenantwright-rabbitmq.tenantwright.svc"',
    'tenantwright-rabbitmq.tenantwright.svc.cluster.local',
    "'*.tenantwright-rabbitmq-nodes.tenantwright.svc.cluster.local'",
    'cluster_partition_handling = pause_minority',
    'default_queue_type = quorum',
    'kind: Vhost',
    'name: "tenantwright"',
    'kind: PodDisruptionBudget',
    'minAvailable: 2',
    'kind: NetworkPolicy',
    'port: 5671',
    'port: 15671',
    'port: 15692',
    'kind: Policy',
    'overflow: reject-publish'
)
foreach ($evidence in $requiredEvidence) {
    if ($render.IndexOf($evidence, [StringComparison]::Ordinal) -lt 0) {
        throw "RabbitMQ platform render is missing required evidence: $evidence"
    }
}

$principalCount = ([regex]::Matches($render, '(?m)^kind: User$')).Count
$permissionCount = ([regex]::Matches($render, '(?m)^kind: Permission$')).Count
$expectedPrincipals = @(
    'tenantwright-auditlogs', 'tenantwright-auth', 'tenantwright-identity', 'tenantwright-notifications',
    'tenantwright-permissions', 'tenantwright-tenant', 'tenantwright-workflows'
)
if ($principalCount -ne $expectedPrincipals.Count -or $permissionCount -ne $expectedPrincipals.Count) {
    throw "RabbitMQ platform must render exactly $($expectedPrincipals.Count) current service users and permissions; found $principalCount users and $permissionCount permissions."
}
foreach ($principal in $expectedPrincipals) {
    if ($render.IndexOf("name: $principal", [StringComparison]::Ordinal) -lt 0) {
        throw "RabbitMQ platform render is missing current principal: $principal"
    }
}

if ($render -match 'bitnami|bitnamilegacy|username:\s*guest|port:\s*5672|disableNonTLSListeners:\s*false') {
    throw 'RabbitMQ platform render contains a forbidden legacy or insecure runtime setting.'
}

$null = [scriptblock]::Create((Get-Content -LiteralPath $operatorInstaller -Raw))
$topologyTlsSource = Get-Content -LiteralPath $topologyTlsConfigurator -Raw
$null = [scriptblock]::Create($topologyTlsSource)
foreach ($evidence in @(
    'jsonpath={.data.ca\.crt}',
    '/etc/ssl/certs/tenantwright-rabbitmq-ca.crt',
    'tenantwright.io/rabbitmq-ca-secret-resource-version',
    '--type strategic',
    '--patch-file'
)) {
    if ($topologyTlsSource.IndexOf($evidence, [StringComparison]::Ordinal) -lt 0) {
        throw "RabbitMQ topology TLS configurator is missing required evidence: $evidence"
    }
}
& powershell -ExecutionPolicy Bypass -File $topologyTlsConfigurator -VerifyOnly
if ($LASTEXITCODE -ne 0) {
    throw 'RabbitMQ topology TLS trust script verification failed.'
}
& powershell -ExecutionPolicy Bypass -File $operatorInstaller -VerifyOnly
if ($LASTEXITCODE -ne 0) {
    throw 'RabbitMQ operator manifest verification failed.'
}

Write-Host 'RabbitMQ operator platform verification passed.' -ForegroundColor Green
