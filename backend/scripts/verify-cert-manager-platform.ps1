[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$chart = Join-Path $repoRoot 'charts/tenantwright-cert-manager'

& helm dependency build $chart --skip-refresh
if ($LASTEXITCODE -ne 0) {
    throw 'cert-manager dependency build failed.'
}

& helm lint $chart
if ($LASTEXITCODE -ne 0) {
    throw 'cert-manager platform Helm lint failed.'
}

$render = (& helm template tenantwright-cert-manager $chart `
    --namespace tenantwright-cert-manager `
    --set clusterIssuer.enabled=true `
    --set clusterIssuer.email=operator@invalid.test `
    --set clusterIssuer.ingressClassName=nginx `
    2>&1) -join "`n"
if ($LASTEXITCODE -ne 0) {
    throw "cert-manager platform Helm rendering failed.`n$render"
}

foreach ($evidence in @(
    'kind: ClusterIssuer',
    'name: letsencrypt-prod',
    'email: "operator@invalid.test"',
    'server: "https://acme-v02.api.letsencrypt.org/directory"',
    'name: letsencrypt-prod-account-key',
    'ingressClassName: "nginx"'
)) {
    if ($render.IndexOf($evidence, [StringComparison]::Ordinal) -lt 0) {
        throw "cert-manager render is missing required ACME ownership evidence: $evidence"
    }
}

$priorErrorAction = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
$missingEmail = (& helm template tenantwright-cert-manager $chart `
    --namespace tenantwright-cert-manager `
    --set clusterIssuer.enabled=true `
    2>&1) -join "`n"
$missingEmailExitCode = $LASTEXITCODE
$ErrorActionPreference = $priorErrorAction
if ($missingEmailExitCode -eq 0 -or $missingEmail -notmatch 'clusterIssuer.email is required') {
    throw 'cert-manager chart must fail closed when ACME issuance is enabled without an account email.'
}

Write-Host 'cert-manager platform verification passed.' -ForegroundColor Green
