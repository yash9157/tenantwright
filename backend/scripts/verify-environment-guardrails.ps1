[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$chartsRoot = Join-Path $repoRoot 'charts'
$temporaryParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$fixtureRoot = Join-Path $temporaryParent ('tenantwright-guards-' + [guid]::NewGuid().ToString('N'))
$global:TenantWrightEnvironmentGuardCalls = [Collections.Generic.List[object]]::new()

# No actual Kubernetes process is started by these operational regressions.
function kubectl {
    $arguments = @($args)
    $global:TenantWrightEnvironmentGuardCalls.Add($arguments)
    $global:LASTEXITCODE = if ($arguments[0] -eq 'get') { 1 } else { 0 }
}
function Assert-Condition {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}
function Assert-Rejected {
    param([scriptblock]$Action, [string]$ExpectedMessage)
    $rejected = $false
    $failure = ''
    try { & $Action *> $null }
    catch { $failure = $_.Exception.Message; $rejected = $failure -match $ExpectedMessage }
    Assert-Condition $rejected "Expected rejection: $ExpectedMessage; encountered: $failure"
}

try {
    foreach ($directory in @('backend', 'platform', 'private/platform', 'private/postgresql-ca')) {
        New-Item -ItemType Directory -Path (Join-Path $fixtureRoot $directory) -Force | Out-Null
    }
    $invalidSecrets = Join-Path $fixtureRoot 'invalid-secrets.yaml'
    Set-Content $invalidSecrets @'
apiVersion: v1
kind: Secret
metadata:
  name: missing-required-keys
  namespace: tenantwright
type: Opaque
stringData:
  fixture: synthetic
'@
    Assert-Rejected {
        & (Join-Path $chartsRoot 'apply-secrets.ps1') -SecretsFile $invalidSecrets -CreateNamespace -DryRun
    } 'missing required secret'
    Assert-Condition ($global:TenantWrightEnvironmentGuardCalls.Count -eq 0) 'Invalid Secret input must not create namespaces or apply anything.'

    $validSecrets = Join-Path $fixtureRoot 'observability-secrets.yaml'
    $example = Get-Content (Join-Path $chartsRoot 'environments/production/examples/secrets/observability-secrets.yaml.example') -Raw
    Set-Content $validSecrets ([regex]::Replace($example, '<[^>]+>', 'synthetic-fixture-value'))
    & (Join-Path $chartsRoot 'apply-secrets.ps1') -Profile observability -Namespace tenantwright-observability `
        -SecretsFile $validSecrets -CreateNamespace -DryRun *> $null
    Assert-Condition ($global:TenantWrightEnvironmentGuardCalls.Count -eq 1 -and
        $global:TenantWrightEnvironmentGuardCalls[0] -contains '--dry-run=client') 'Valid dry-run may only perform client-side apply validation.'

    $assignments = [ordered]@{
        'application-a' = @('auth', 'identity'); 'application-b' = @('tenant')
        'application-c' = @('permissions', 'auditlogs'); 'application-d' = @('notifications', 'workflows')
        'temporal' = @('temporal', 'temporal-visibility')
    }
    $topology = [ordered]@{
        providerConnectionLimit = 20; providerReserve = 4
        maximumApplicationPoolSize = 4; maximumApplicationReplicas = 2; slots = [ordered]@{}
    }
    foreach ($slot in $assignments.Keys) {
        $services = [ordered]@{}
        foreach ($service in $assignments[$slot]) {
            $services[$service] = @{ database = $service; runtimeSecret = "$service-db"; schemaSecret = "$service-schema"; caSecret = "$service-ca" }
            if ($slot -ne 'temporal') {
                Set-Content (Join-Path $fixtureRoot "backend/$service.values.yaml") @'
networkPolicy:
  postgresql:
    enabled: true
    port: 6432
    cidrs: [192.0.2.10/32]
'@
            }
        }
        $topology.slots[$slot] = @{ host = "$slot.postgresql.fixture.test"; port = 6432; egressCidr = '192.0.2.10/32'; caFile = 'postgresql-ca/fixture.pem'; services = $services }
    }
    Set-Content (Join-Path $fixtureRoot 'private/postgresql.local.yaml') (ConvertTo-Yaml $topology)
    Set-Content (Join-Path $fixtureRoot 'private/postgresql-ca/fixture.pem') 'synthetic-ca-existence-fixture'
    Copy-Item (Join-Path $chartsRoot 'environments/dev/backend/common.values.yaml') (Join-Path $fixtureRoot 'backend/common.values.yaml')
    Set-Content (Join-Path $fixtureRoot 'platform/temporal.values.yaml') @'
production: {enabled: false}
networkPolicy:
  postgresqlPort: 6432
  postgresqlCidrs: [192.0.2.10/32]
'@
    $verifier = Join-Path $chartsRoot 'verify-direct-connection-profile.ps1'
    & $verifier -EnvironmentValuesDirectory $fixtureRoot -SkipEgressCidrCheck *> $null
    Set-Content (Join-Path $fixtureRoot 'private/platform/temporal.values.local.yaml') @'
temporal:
  server:
    config:
      persistence:
        datastores:
          default:
            sql:
              maxConns: 80
'@
    Assert-Rejected { & $verifier -EnvironmentValuesDirectory $fixtureRoot -SkipEgressCidrCheck } 'Temporal persistence maximum is incorrect'
    Remove-Item -LiteralPath (Join-Path $fixtureRoot 'private/platform/temporal.values.local.yaml')
    $authValuesPath = Join-Path $fixtureRoot 'backend/auth.values.yaml'
    Add-Content $authValuesPath @'
env:
  PostgreSql__Runtime__ClientPool__MaximumPoolSize: 40
'@
    Assert-Rejected { & $verifier -EnvironmentValuesDirectory $fixtureRoot -SkipEgressCidrCheck } 'direct connection cap is incorrect'
    $authValues = Get-Content $authValuesPath -Raw | ConvertFrom-Yaml
    $authValues.env.PostgreSql__Runtime__ClientPool__MaximumPoolSize = 4
    $authValues.networkPolicy.postgresql.port = 6543
    Set-Content $authValuesPath (ConvertTo-Yaml $authValues)
    Assert-Rejected { & $verifier -EnvironmentValuesDirectory $fixtureRoot -SkipEgressCidrCheck } 'PostgreSQL egress must match'

    # Invoke the actual installer validator without executing its deployment body.
    $tokens = $null; $parseErrors = $null
    $installerAst = [Management.Automation.Language.Parser]::ParseFile(
        (Join-Path $chartsRoot 'helm-install-all.ps1'), [ref]$tokens, [ref]$parseErrors)
    foreach ($name in @('Get-PropertyValue', 'Assert-NetworkPolicyEnvironmentOwnership')) {
        $definition = $installerAst.Find({ param($node)
            $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
        }, $false)
        Invoke-Expression $definition.Extent.Text
    }
    $policy = @{ networkPolicy = @{ enabled = $true; ingressController = @{ enabled = $true; namespace = 'kube-system' }; postgresql = @{ enabled = $true; port = 6432; cidrs = @('192.0.2.10/32') } } }
    Assert-NetworkPolicyEnvironmentOwnership -ValuesDocument $policy -ReleaseName 'tenantwright-auth' -RequiresPostgresql $true
    $policy.networkPolicy.enabled = $false
    Assert-Rejected { Assert-NetworkPolicyEnvironmentOwnership -ValuesDocument $policy -ReleaseName 'tenantwright-auth' -RequiresPostgresql $true } 'NetworkPolicy must remain enabled'
    $policy.networkPolicy.enabled = $true
    $policy.networkPolicy.ingressController.enabled = $false
    Assert-Rejected { Assert-NetworkPolicyEnvironmentOwnership -ValuesDocument $policy -ReleaseName 'tenantwright-auth' -RequiresPostgresql $true } 'ingress controller policy must remain enabled'
    Write-Host 'Secret mutation ordering, dry-run isolation and effective provider-budget guardrails passed.' -ForegroundColor Green
}
finally {
    $resolvedFixture = [IO.Path]::GetFullPath($fixtureRoot)
    if (-not $resolvedFixture.StartsWith($temporaryParent.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing to remove guardrail fixtures outside the temporary directory.'
    }
    if (Test-Path -LiteralPath $resolvedFixture) { Remove-Item -LiteralPath $resolvedFixture -Recurse -Force }
    Remove-Variable -Name TenantWrightEnvironmentGuardCalls -Scope Global -ErrorAction SilentlyContinue
}
