param(
    [string]$OutputDir = "app/Auth/Auth/certs",
    [string]$Password = ""
)

$ErrorActionPreference = "Stop"

$fullOutputDir = Resolve-Path -Path $OutputDir -ErrorAction SilentlyContinue
if (-not $fullOutputDir) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
    $fullOutputDir = Resolve-Path -Path $OutputDir
}

if ([string]::IsNullOrEmpty($Password)) {
    $pwd = New-Object System.Security.SecureString
} else {
    $pwd = ConvertTo-SecureString -String $Password -AsPlainText -Force
}

$signingCert = New-SelfSignedCertificate `
    -Subject "CN=TenantWright Auth Signing" `
    -CertStoreLocation "Cert:\\CurrentUser\\My" `
    -KeyExportPolicy Exportable `
    -KeyAlgorithm RSA `
    -KeyLength 2048 `
    -HashAlgorithm SHA256 `
    -KeyUsage DigitalSignature `
    -NotAfter (Get-Date).AddYears(2)

$signingPath = Join-Path $fullOutputDir "auth-signing.pfx"
Export-PfxCertificate -Cert $signingCert -FilePath $signingPath -Password $pwd | Out-Null

$encryptionCert = New-SelfSignedCertificate `
    -Subject "CN=TenantWright Auth Encryption" `
    -CertStoreLocation "Cert:\\CurrentUser\\My" `
    -KeyExportPolicy Exportable `
    -KeyAlgorithm RSA `
    -KeyLength 2048 `
    -HashAlgorithm SHA256 `
    -KeyUsage KeyEncipherment,DataEncipherment `
    -NotAfter (Get-Date).AddYears(2)

$encryptionPath = Join-Path $fullOutputDir "auth-encryption.pfx"
Export-PfxCertificate -Cert $encryptionCert -FilePath $encryptionPath -Password $pwd | Out-Null

Write-Host "Created:"
Write-Host $signingPath
Write-Host $encryptionPath
