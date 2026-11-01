# TenantWright End-to-End Security Verification Script
# This script performs comprehensive E2E tests for Auth and Identity services.

$ErrorActionPreference = "Continue"

$authUrl = "http://localhost:5234"
$identityUrl = "https://localhost:7161"

Write-Host "--- Starting E2E Security Verification ---" -ForegroundColor Cyan

# Helper function for POST requests
function Test-AuthEndpoint($path, $body, $description) {
    Write-Host "`nTesting: $description..." -NoNewline
    try {
        $response = Invoke-WebRequest -Uri "$authUrl$path" -Method Post -Body $body -ContentType "application/x-www-form-urlencoded" -SkipHttpErrorCheck -TimeoutSec 5
        return $response
    } catch {
        Write-Host " [ERROR] Connection failed" -ForegroundColor Red
        return $null
    }
}

# 1. Verify Client Validation (Wrong Client ID)
$body = @{
    grant_type = "client_credentials"
    client_id = "wrong-client-id"
    client_secret = "any-secret"
}
$res = Test-AuthEndpoint "/connect/token" $body "Client Credentials with Wrong Client ID"
if ($res) {
    if ($res.StatusCode -eq 400 -and $res.Content -like "*invalid_client*") {
        Write-Host " [PASS] Correctly rejected invalid client" -ForegroundColor Green
    } else {
        Write-Host " [FAIL] Unexpected status: $($res.StatusCode)" -ForegroundColor Red
    }
}

# 2. Verify Client Validation (Right Client ID, Wrong Secret)
$body = @{
    grant_type = "client_credentials"
    client_id = "auth-internal"
    client_secret = "wrong-secret"
}
$res = Test-AuthEndpoint "/connect/token" $body "Client Credentials with Wrong Secret"
if ($res) {
    if ($res.StatusCode -eq 400 -and $res.Content -like "*invalid_client*") {
        Write-Host " [PASS] Correctly rejected invalid secret" -ForegroundColor Green
    } else {
        Write-Host " [FAIL] Unexpected status: $($res.StatusCode)" -ForegroundColor Red
    }
}

# 3. Verify Client Validation (Right Client ID, Right Secret)
$body = @{
    grant_type = "client_credentials"
    client_id = "auth-internal"
    client_secret = "tenantwright-internal" # Default from data seed
}
$res = Test-AuthEndpoint "/connect/token" $body "Client Credentials Success Flow"
if ($res) {
    if ($res.StatusCode -eq 200 -and $res.Content -like "*access_token*") {
        Write-Host " [PASS] Successfully issued M2M token" -ForegroundColor Green
    } else {
        Write-Host " [FAIL] Token issuance failed: $($res.StatusCode)" -ForegroundColor Red
    }
}

# 4. Verify Authorize Endpoint (Wrong Client ID)
Write-Host "`nTesting: Authorize Endpoint with Wrong Client ID..." -NoNewline
try {
    $uri = "$authUrl/connect/authorize?client_id=wrong-client&response_type=code&scope=openid&redirect_uri=http://localhost:5000"
    $res = Invoke-WebRequest -Uri $uri -Method Get -SkipHttpErrorCheck -TimeoutSec 5
    if ($res.Content -like "*invalid_client*" -or $res.StatusCode -eq 400) {
        Write-Host " [PASS] Rejected invalid client in authorize flow" -ForegroundColor Green
    } else {
        Write-Host " [FAIL] Unexpected response: $($res.StatusCode)" -ForegroundColor Red
    }
} catch {
    Write-Host " [SKIP] Server unreachable" -ForegroundColor Yellow
}

# 5. Verify Password Flow (if enabled in Dev)
$body = @{
    grant_type = "password"
    username = "admin"
    password = "wrong-password"
    client_id = "tenantwright-web" # Example client
}
$res = Test-AuthEndpoint "/connect/token" $body "Password Flow with Wrong Password"
if ($res) {
    if ($res.StatusCode -eq 400 -and $res.Content -like "*invalid_grant*") {
        Write-Host " [PASS] Correctly rejected wrong password" -ForegroundColor Green
    } elseif ($res.Content -like "*unsupported_grant_type*") {
        Write-Host " [INFO] Password flow disabled (expected in Production)" -ForegroundColor Cyan
    } else {
        Write-Host " [FAIL] Unexpected behavior: $($res.StatusCode)" -ForegroundColor Red
    }
}

Write-Host "`n--- E2E Verification Complete ---" -ForegroundColor Cyan
