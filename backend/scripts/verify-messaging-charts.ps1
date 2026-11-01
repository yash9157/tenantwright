[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$services = @(
    'auditlogs', 'auth', 'identity', 'notifications', 'permissions', 'tenant', 'workflows'
)
$trustCatalog = Join-Path $repoRoot 'charts/tenantwright-common/files/messaging.values.yaml'
$verificationDigest = 'sha256:' + ('0' * 64)

function Assert-WorkloadRender {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Render
    )

    $requiredEvidence = @(
        'replicas: 1',
        'kind: PodDisruptionBudget',
        'minAvailable: 1',
        'kind: NetworkPolicy',
        'terminationGracePeriodSeconds: 60',
        'readOnlyRootFilesystem: true',
        'runAsNonRoot: true',
        'topologySpreadConstraints:',
        'checksum/config:',
        'OTEL_EXPORTER_OTLP_ENDPOINT',
        'tenantwright-otel-collector.tenantwright-telemetry.svc:4317',
        '"helm.sh/hook": test',
        'mountPath: /tmp'
    )

    foreach ($evidence in $requiredEvidence) {
        if ($Render.IndexOf($evidence, [StringComparison]::Ordinal) -lt 0) {
            throw "$Name render is missing production workload evidence: $evidence"
        }
    }

    if ($Render -match 'image:\s+"[^"]+:latest"') {
        throw "$Name renders a mutable latest image."
    }
    if ($Render.IndexOf('kind: HorizontalPodAutoscaler', [StringComparison]::Ordinal) -ge 0) {
        throw "$Name unexpectedly renders an HPA while chart autoscaling is disabled."
    }
}

function Assert-SmtpEgressBoundary {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Render,
        [Parameter(Mandatory = $true)][bool]$Expected
    )

    $networkPolicyMatch = [regex]::Match(
        $Render,
        '(?ms)# Source: tenantwright-[^/]+/templates/networkpolicy\.yaml\r?\n(?<policy>.*?)(?=\r?\n---|\z)')
    if (-not $networkPolicyMatch.Success) {
        throw "$Name render is missing the NetworkPolicy manifest."
    }

    $networkPolicy = $networkPolicyMatch.Groups['policy'].Value
    foreach ($port in @(465, 587, 2525)) {
        $hasPort = $networkPolicy -match "(?m)^\s*-?\s*port:\s+$port\s*$"
        if ($hasPort -ne $Expected) {
            $expectation = if ($Expected) { 'retain' } else { 'deny' }
            throw "$Name must $expectation Notifications-owned SMTP port $port."
        }
    }
}

foreach ($service in $services) {
    $chart = Join-Path $repoRoot "charts/tenantwright-$service"
    & helm lint $chart --set "image.digest=$verificationDigest"
    if ($LASTEXITCODE -ne 0) {
        throw "Helm lint failed for tenantwright-$service."
    }

    $defaultRender = (& helm template "tenantwright-$service" $chart --namespace tenantwright `
        --set "image.digest=$verificationDigest" 2>&1) -join "`n"
    if ($LASTEXITCODE -ne 0) {
        throw "Default Helm rendering failed for tenantwright-$service.`n$defaultRender"
    }
    Assert-WorkloadRender -Name "tenantwright-$service" -Render $defaultRender
    Assert-SmtpEgressBoundary `
        -Name "tenantwright-$service (default)" `
        -Render $defaultRender `
        -Expected:($service -eq 'notifications')

    $environmentRender = (& helm template "tenantwright-$service-environment" $chart --namespace tenantwright `
        --set networkPolicy.ingressController.namespace=kube-system `
        --set networkPolicy.postgresql.port=24816 `
        --set "image.digest=$verificationDigest" `
        --set-string "networkPolicy.postgresql.cidrs[0]=192.0.2.10/32" `
        2>&1) -join "`n"
    if ($LASTEXITCODE -ne 0) {
        throw "Environment-owned NetworkPolicy rendering failed for tenantwright-$service.`n$environmentRender"
    }
    foreach ($evidence in @(
        'kubernetes.io/metadata.name: "kube-system"',
        'cidr: "192.0.2.10/32"',
        'port: 24816'
    )) {
        if ($environmentRender.IndexOf($evidence, [StringComparison]::Ordinal) -lt 0) {
            throw "tenantwright-$service does not render environment-owned NetworkPolicy evidence: $evidence"
        }
    }

    if ($defaultRender -match 'MT_LICENSE|materialize-messaging-trust|tenantwright-[a-z]+-rabbitmq|Messaging__') {
        throw "tenantwright-$service exposes messaging configuration while messaging.provider is None."
    }

    if ($service -eq 'workflows') {
        $temporalEvidence = @(
            'Temporal__TargetHost: "tenantwright-temporal-frontend.tenantwright-temporal.svc:7233"',
            'Temporal__Namespace: "tenantwright-production"',
            'Temporal__UseTls: "true"',
            'Temporal__ServerRootCaCertificatePath: "/app/temporal-secrets/tls/ca.crt"',
            'Temporal__ClientCertificatePath: "/app/temporal-secrets/tls/tls.crt"',
            'Temporal__ClientPrivateKeyPath: "/app/temporal-secrets/tls/tls.key"',
            'Temporal__ApiKeyPath: "/app/temporal-secrets/api/api-key"',
            'secretName: tenantwright-workflows-temporal-client-tls',
            'secretName: tenantwright-workflows-temporal-api-key',
            'mountPath: /app/temporal-secrets/tls',
            'mountPath: /app/temporal-secrets/api',
            'kubernetes.io/metadata.name: tenantwright-temporal',
            'port: 7233'
        )
        foreach ($evidence in $temporalEvidence) {
            if ($defaultRender.IndexOf($evidence, [StringComparison]::Ordinal) -lt 0) {
                throw "tenantwright-workflows render is missing Temporal custody evidence: $evidence"
            }
        }
    }

    $priorErrorAction = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $missingCatalogRender = (& helm template "tenantwright-$service" $chart --namespace tenantwright `
        --set "image.digest=$verificationDigest" `
        --set messaging.provider=MassTransit 2>&1) -join "`n"
    $missingCatalogExitCode = $LASTEXITCODE
    $ErrorActionPreference = $priorErrorAction
    if ($missingCatalogExitCode -eq 0 -or
        $missingCatalogRender -notmatch 'messaging\.trust\.publicKeys must contain') {
        throw "tenantwright-$service must fail rendering when a messaging provider has no verification-key catalog."
    }

    $enabledRender = (& helm template "tenantwright-$service" $chart --namespace tenantwright `
        --values $trustCatalog `
        --set "image.digest=$verificationDigest" `
        --set messaging.provider=MassTransit `
        2>&1) -join "`n"
    if ($LASTEXITCODE -ne 0) {
        throw "Enabled Helm rendering failed for tenantwright-$service.`n$enabledRender"
    }
    Assert-WorkloadRender -Name "tenantwright-$service (MassTransit enabled)" -Render $enabledRender
    Assert-SmtpEgressBoundary `
        -Name "tenantwright-$service (MassTransit enabled)" `
        -Render $enabledRender `
        -Expected:($service -eq 'notifications')

    $requiredMessagingEvidence = @(
        'name: materialize-messaging-trust',
        'image: "busybox:1.37.0@sha256:9532d8c39891ca2ecde4d30d7710e01fb739c87a8b9299685c63704296b16028"',
        'cp -L "/projected-private/private.pem" /regular/private.pem',
        'cp -L "/projected-rabbitmq-ca/ca.crt" /regular/rabbitmq-ca.crt',
        'name: Messaging__RabbitMq__TrustedCaCertificatePath',
        'value: /app/messaging-trust/rabbitmq-ca.crt',
        'name: Messaging__Trust__PrivateKeyPath',
        'value: /app/messaging-trust/private.pem',
        'name: messaging-trust-regular-files',
        'medium: Memory',
        "name: `"tenantwright-$service-rabbitmq`"",
        'value: "tenantwright-messaging-replay"'
    )
    foreach ($evidence in $requiredMessagingEvidence + @('name: MT_LICENSE', 'name: "tenantwright-masstransit-license"')) {
        if ($enabledRender.IndexOf($evidence, [StringComparison]::Ordinal) -lt 0) {
            throw "tenantwright-$service render is missing required MassTransit messaging evidence: $evidence"
        }
    }
    if ($enabledRender -notmatch '(?m)name: Messaging__Provider\r?\n\s+value: "MassTransit"') {
        throw "tenantwright-$service must select Messaging__Provider=MassTransit when messaging.provider=MassTransit."
    }

    # Every provider shares the neutral connection and trust material; only MassTransit mounts its licence.
    $wolverineRender = (& helm template "tenantwright-$service" $chart --namespace tenantwright `
        --values $trustCatalog `
        --set "image.digest=$verificationDigest" `
        --set messaging.provider=Wolverine `
        2>&1) -join "`n"
    if ($LASTEXITCODE -ne 0) {
        throw "Wolverine Helm rendering failed for tenantwright-$service.`n$wolverineRender"
    }
    foreach ($evidence in $requiredMessagingEvidence) {
        if ($wolverineRender.IndexOf($evidence, [StringComparison]::Ordinal) -lt 0) {
            throw "tenantwright-$service Wolverine render is missing required messaging evidence: $evidence"
        }
    }
    if ($wolverineRender -match 'MT_LICENSE|tenantwright-masstransit-license') {
        throw "tenantwright-$service must not mount the MassTransit licence when messaging.provider=Wolverine."
    }

    $priorErrorAction = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $unknownProviderRender = (& helm template "tenantwright-$service" $chart --namespace tenantwright `
        --values $trustCatalog `
        --set "image.digest=$verificationDigest" `
        --set messaging.provider=Kafka 2>&1) -join "`n"
    $unknownProviderExitCode = $LASTEXITCODE
    $ErrorActionPreference = $priorErrorAction
    if ($unknownProviderExitCode -eq 0) {
        throw "tenantwright-$service must fail rendering for an unknown messaging.provider."
    }
}

$gatewayChart = Join-Path $repoRoot 'charts/tenantwright-gateway'
& helm lint $gatewayChart --set "image.digest=$verificationDigest"
if ($LASTEXITCODE -ne 0) {
    throw 'Helm lint failed for tenantwright-gateway.'
}
$gatewayRender = (& helm template tenantwright-gateway $gatewayChart --namespace tenantwright `
    --set "image.digest=$verificationDigest" 2>&1) -join "`n"
if ($LASTEXITCODE -ne 0) {
    throw "Gateway Helm rendering failed.`n$gatewayRender"
}
Assert-WorkloadRender -Name 'tenantwright-gateway' -Render $gatewayRender
if ($gatewayRender -match 'MT_LICENSE|Messaging__') {
    throw 'Gateway must not receive messaging configuration or MassTransit license material.'
}
if ($gatewayRender -match '(?m)^\s+port:\s+5432$') {
    throw 'Gateway must not receive PostgreSQL egress without a database owner.'
}

Write-Host 'Messaging chart trust, workload-hardening, and inventory verification passed.'
