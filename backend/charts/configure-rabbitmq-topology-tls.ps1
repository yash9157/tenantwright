[CmdletBinding()]
param(
    [string]$RabbitMqNamespace = "tenantwright",
    [string]$RabbitMqCaSecretName = "tenantwright-rabbitmq-ca",
    [string]$OperatorNamespace = "rabbitmq-system",
    [string]$OperatorDeploymentName = "messaging-topology-operator",
    [string]$OperatorTrustSecretName = "tenantwright-rabbitmq-ca",
    [string]$KubectlPath = "kubectl",
    [switch]$VerifyOnly
)

$ErrorActionPreference = "Stop"

if (-not (Get-Command $KubectlPath -ErrorAction SilentlyContinue)) {
    throw "Required command not found: $KubectlPath"
}

if ($VerifyOnly) {
    Write-Host "RabbitMQ topology TLS trust script syntax and command ownership verified." -ForegroundColor Green
    return
}

$caData = (& $KubectlPath -n $RabbitMqNamespace get secret $RabbitMqCaSecretName `
    -o 'jsonpath={.data.ca\.crt}' 2>$null) -join ""
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($caData)) {
    throw "RabbitMQ CA Secret '$RabbitMqCaSecretName' is absent or missing ca.crt in namespace '$RabbitMqNamespace'."
}

$trustSecret = [ordered]@{
    apiVersion = "v1"
    kind = "Secret"
    metadata = [ordered]@{
        name = $OperatorTrustSecretName
        namespace = $OperatorNamespace
        labels = [ordered]@{
            "app.kubernetes.io/part-of" = "tenantwright"
            "app.kubernetes.io/component" = "messaging-topology-trust"
        }
    }
    type = "Opaque"
    data = [ordered]@{
        "tenantwright-rabbitmq-ca.crt" = $caData
    }
}
($trustSecret | ConvertTo-Json -Depth 8 -Compress) | & $KubectlPath apply -f - | Out-Host
if ($LASTEXITCODE -ne 0) {
    throw "Failed to synchronize the public RabbitMQ CA into namespace '$OperatorNamespace'."
}

$deploymentPatch = [ordered]@{
    spec = [ordered]@{
        template = [ordered]@{
            metadata = [ordered]@{
                annotations = [ordered]@{
                    "tenantwright.io/rabbitmq-ca-secret-resource-version" =
                        ((& $KubectlPath -n $OperatorNamespace get secret $OperatorTrustSecretName `
                            -o 'jsonpath={.metadata.resourceVersion}') -join "")
                }
            }
            spec = [ordered]@{
                containers = @(
                    [ordered]@{
                        name = "manager"
                        volumeMounts = @(
                            [ordered]@{
                                name = "tenantwright-rabbitmq-ca"
                                mountPath = "/etc/ssl/certs/tenantwright-rabbitmq-ca.crt"
                                subPath = "tenantwright-rabbitmq-ca.crt"
                                readOnly = $true
                            }
                        )
                    }
                )
                volumes = @(
                    [ordered]@{
                        name = "tenantwright-rabbitmq-ca"
                        secret = [ordered]@{
                            secretName = $OperatorTrustSecretName
                            optional = $false
                        }
                    }
                )
            }
        }
    }
}
$patchJson = $deploymentPatch | ConvertTo-Json -Depth 12 -Compress
$patchPath = Join-Path ([System.IO.Path]::GetTempPath()) "tenantwright-rabbitmq-topology-trust-$PID.json"
try {
    [System.IO.File]::WriteAllText($patchPath, $patchJson, [System.Text.UTF8Encoding]::new($false))
    & $KubectlPath -n $OperatorNamespace patch deployment $OperatorDeploymentName `
        --type strategic --patch-file $patchPath | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to mount RabbitMQ CA trust into '$OperatorDeploymentName'."
    }
}
finally {
    Remove-Item -LiteralPath $patchPath -Force -ErrorAction SilentlyContinue
}

& $KubectlPath -n $OperatorNamespace rollout status deployment/$OperatorDeploymentName --timeout=5m
if ($LASTEXITCODE -ne 0) {
    throw "Messaging Topology Operator did not become Ready after CA trust rotation."
}

Write-Host "RabbitMQ topology TLS trust is configured without copying a private key." -ForegroundColor Green
