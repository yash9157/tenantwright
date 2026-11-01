[CmdletBinding()]
param(
    [string]$AuthUrl = "https://localhost:7239",
    [string]$PermissionsUrl = "https://localhost:7163",
    [string]$IdentityUrl = "https://localhost:7161",
    [string]$TenantUrl = "https://localhost:7162",
    [string]$InternalClientId = "auth-internal",
    [string]$InternalClientSecret = "tenantwright-internal",
    [string]$PasswordClientId = "tenantwright-gateway",
    [string]$PasswordClientSecret = "",
    [string]$Username = $env:SMOKE_USERNAME,
    [string]$UserPassword = $env:SMOKE_PASSWORD,
    [string]$Tenant = $env:SMOKE_TENANT,
    [string]$TenantId = $env:SMOKE_TENANT_ID,
    [string]$UserId = $env:SMOKE_USER_ID,
    [string]$DeviceFingerprint = $env:SMOKE_DEVICE_FINGERPRINT,
    [bool]$RememberTrustedDevice = $false,
    [string]$RoleName = "admin",
    [string]$FeatureName = "Specialty.Cardiology",
    [string]$PermissionName = "Identity.Users",
    [string]$PasswordScopes = "openid profile roles identity tenant permissions notifications auditlogs workflows offline_access",
    [ValidateRange(1, 1000)]
    [int]$TenantApiBurstCalls = 10,
    [ValidateRange(0, 50000)]
    [int]$TenantApiLoadTotalCalls = 0,
    [ValidateRange(1, 200)]
    [int]$TenantApiLoadConcurrency = 40,
    [int]$TimeoutSec = 20,
    [int]$Iterations = 1,
    [ValidateRange(0, 1000)]
    [int]$WarmupIterations = 15,
    [int]$DelayMs = 200,
    [string]$AuthServiceLogPath = "artifacts/perf/service-logs/Auth.log",
    [string]$MetricsOutputPath = "",
    [switch]$Benchmark,
    [switch]$SkipAuthorizedApiCheck,
    [switch]$SkipNegativeChecks,
    [switch]$SkipDevProvision,
    [switch]$Basic
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$AuthBase = $AuthUrl.TrimEnd('/')
$PermissionsBase = $PermissionsUrl.TrimEnd('/')
$IdentityBase = $IdentityUrl.TrimEnd('/')
$TenantBase = $TenantUrl.TrimEnd('/')

$supportsSkipCertificateCheck = (Get-Command Invoke-WebRequest).Parameters.ContainsKey("SkipCertificateCheck")
$supportsConvertFromJsonDepth = (Get-Command ConvertFrom-Json).Parameters.ContainsKey("Depth")
$isLegacyWindowsPowerShell = ($PSVersionTable.PSVersion.Major -lt 6)
if (-not $supportsSkipCertificateCheck -and $isLegacyWindowsPowerShell) {
    Add-Type -AssemblyName System.Net.Http
    [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12
    [System.Net.ServicePointManager]::ServerCertificateValidationCallback = { $true }
}
$script:results = [System.Collections.Generic.List[object]]::new()
$script:failed = $false

function Add-Result {
    param(
        [string]$Step,
        [ValidateSet("PASS", "FAIL", "SKIP")]
        [string]$Status,
        [string]$Details,
        [int]$DurationMs = 0
    )

    if ($Status -eq "FAIL") {
        $script:failed = $true
    }

    $color = switch ($Status) {
        "PASS" { "Green" }
        "FAIL" { "Red" }
        default { "Yellow" }
    }

    $timeText = if ($DurationMs -gt 0) { " (${DurationMs}ms)" } else { "" }
    Write-Host ("[{0}] {1}{2} - {3}" -f $Status, $Step, $timeText, $Details) -ForegroundColor $color
    $script:results.Add([pscustomobject]@{
            Step = $Step
            Status = $Status
            Details = $Details
            DurationMs = $DurationMs
        }) | Out-Null
}

function Start-AuthTelemetryCapture {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return [pscustomobject]@{
            Enabled = $false
            Path = ""
            StartLength = 0
            StartedAtUtc = [DateTime]::UtcNow.ToString("o")
            Status = "disabled"
        }
    }

    $resolvedPath = [System.IO.Path]::GetFullPath($Path)
    if (-not (Test-Path $resolvedPath)) {
        return [pscustomobject]@{
            Enabled = $false
            Path = $resolvedPath
            StartLength = 0
            StartedAtUtc = [DateTime]::UtcNow.ToString("o")
            Status = "missing"
        }
    }

    $fileInfo = Get-Item -Path $resolvedPath
    return [pscustomobject]@{
        Enabled = $true
        Path = $resolvedPath
        StartLength = [long]$fileInfo.Length
        StartedAtUtc = [DateTime]::UtcNow.ToString("o")
        Status = "capturing"
    }
}

function Read-AuthTelemetryLogDelta {
    param([object]$Capture)

    if ($null -eq $Capture -or -not $Capture.Enabled -or [string]::IsNullOrWhiteSpace($Capture.Path)) {
        return ""
    }

    if (-not (Test-Path $Capture.Path)) {
        return ""
    }

    $fileInfo = Get-Item -Path $Capture.Path
    $currentLength = [long]$fileInfo.Length
    if ($currentLength -le 0) {
        return ""
    }

    $startLength = [long]$Capture.StartLength
    if ($startLength -lt 0) {
        $startLength = 0
    }
    if ($startLength -gt $currentLength) {
        $startLength = 0
    }

    if ($startLength -eq $currentLength) {
        return ""
    }

    $stream = $null
    $reader = $null
    try {
        $stream = [System.IO.File]::Open($Capture.Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
        [void]$stream.Seek($startLength, [System.IO.SeekOrigin]::Begin)
        $reader = New-Object System.IO.StreamReader($stream, [System.Text.Encoding]::UTF8, $true, 4096, $true)
        return $reader.ReadToEnd()
    }
    finally {
        if ($reader -ne $null) { $reader.Dispose() }
        if ($stream -ne $null) { $stream.Dispose() }
    }
}

function Get-AuthTelemetrySummary {
    param([object]$Capture)

    $summary = [ordered]@{
        enabled = [bool]($Capture -and $Capture.Enabled)
        path = if ($Capture) { [string]$Capture.Path } else { "" }
        status = if ($Capture) { [string]$Capture.Status } else { "disabled" }
        tokenRequestCount = 0
        tokenDurationAvgMs = 0
        tokenDurationP95Ms = 0
        tokenDurationP99Ms = 0
        tokenDbQueryAvg = 0
        tokenDbQueryP95 = 0
        tokenRateLimitRejectCount = 0
        tokenStatusCodes = @()
        tokenRejectPartitionHashes = @()
    }

    if (-not $summary.enabled) {
        return [pscustomobject]$summary
    }

    $deltaText = Read-AuthTelemetryLogDelta -Capture $Capture
    if ([string]::IsNullOrWhiteSpace($deltaText)) {
        $summary.status = "no_new_data"
        return [pscustomobject]$summary
    }

    $durations = New-Object System.Collections.Generic.List[int]
    $dbQueries = New-Object System.Collections.Generic.List[int]
    $statusCounts = @{}
    $partitionCounts = @{}
    $rejectionCount = 0

    $lines = @($deltaText -split "`r?`n")
    foreach ($line in $lines) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        $requestMatch = [regex]::Match($line, "Token request metrics\. StatusCode=(\d+), DurationMs=(\d+), DbQueryCount=(\d+)")
        if ($requestMatch.Success) {
            $statusCode = [int]$requestMatch.Groups[1].Value
            $durationMs = [int]$requestMatch.Groups[2].Value
            $dbQueryCount = [int]$requestMatch.Groups[3].Value

            $durations.Add($durationMs) | Out-Null
            $dbQueries.Add($dbQueryCount) | Out-Null

            $statusKey = [string]$statusCode
            if (-not $statusCounts.ContainsKey($statusKey)) {
                $statusCounts[$statusKey] = 0
            }
            $statusCounts[$statusKey]++
            continue
        }

        if ($line.Contains("Token rate limit rejected.") -or $line.Contains("Auth rate limit rejected.")) {
            $rejectionCount++
            $partitionMatch = [regex]::Match($line, "PartitionHash=([0-9a-f]+)")
            if ($partitionMatch.Success) {
                $partitionHash = [string]$partitionMatch.Groups[1].Value
                if (-not $partitionCounts.ContainsKey($partitionHash)) {
                    $partitionCounts[$partitionHash] = 0
                }
                $partitionCounts[$partitionHash]++
            }
        }
    }

    $durationValues = @($durations.ToArray())
    $dbQueryValues = @($dbQueries.ToArray())

    $summary.status = "captured"
    $summary.tokenRequestCount = $durationValues.Count
    $summary.tokenRateLimitRejectCount = $rejectionCount
    $summary.tokenDurationAvgMs = if ($durationValues.Count) { [int][Math]::Round((($durationValues | Measure-Object -Average).Average), 0) } else { 0 }
    $summary.tokenDurationP95Ms = Get-PercentileMs -Values $durationValues -Percentile 0.95
    $summary.tokenDurationP99Ms = Get-PercentileMs -Values $durationValues -Percentile 0.99
    $summary.tokenDbQueryAvg = if ($dbQueryValues.Count) { [double][Math]::Round((($dbQueryValues | Measure-Object -Average).Average), 2) } else { 0 }
    $summary.tokenDbQueryP95 = Get-PercentileMs -Values $dbQueryValues -Percentile 0.95
    $summary.tokenStatusCodes = @(
        $statusCounts.GetEnumerator() |
        Sort-Object -Property Name |
        ForEach-Object {
            [pscustomobject]@{
                statusCode = [int]$_.Key
                count = [int]$_.Value
            }
        }
    )
    $summary.tokenRejectPartitionHashes = @(
        $partitionCounts.GetEnumerator() |
        Sort-Object -Property Value -Descending |
        Select-Object -First 10 |
        ForEach-Object {
            [pscustomobject]@{
                partitionHash = [string]$_.Key
                count = [int]$_.Value
            }
        }
    )

    return [pscustomobject]$summary
}

function ConvertFrom-JsonSafe {
    param([string]$Content)

    if ([string]::IsNullOrWhiteSpace($Content)) {
        return $null
    }

    try {
        if ($supportsConvertFromJsonDepth) {
            return $Content | ConvertFrom-Json -Depth 30
        }
        return $Content | ConvertFrom-Json
    }
    catch {
        return $null
    }
}

function Invoke-HttpRequestLegacy {
    param(
        [string]$Method,
        [string]$Uri,
        [hashtable]$Headers,
        [object]$Body,
        [string]$ContentType
    )

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $tempBodyFile = $null
    try {
        $curlArgs = New-Object System.Collections.Generic.List[string]
        $curlArgs.Add("-s") | Out-Null
        $curlArgs.Add("-S") | Out-Null
        $curlArgs.Add("-k") | Out-Null
        $curlArgs.Add("-X") | Out-Null
        $curlArgs.Add($Method) | Out-Null

        $contentTypeHeaderAdded = $false
        if ($null -ne $Headers -and $Headers.Count -gt 0) {
            foreach ($headerKey in $Headers.Keys) {
                $headerValue = [string]$Headers[$headerKey]
                $curlArgs.Add("-H") | Out-Null
                $curlArgs.Add(("{0}: {1}" -f $headerKey, $headerValue)) | Out-Null
                if ([string]::Equals($headerKey, "Content-Type", [StringComparison]::OrdinalIgnoreCase)) {
                    $contentTypeHeaderAdded = $true
                }
            }
        }

        if (-not [string]::IsNullOrWhiteSpace($ContentType) -and -not $contentTypeHeaderAdded) {
            $curlArgs.Add("-H") | Out-Null
            $curlArgs.Add(("Content-Type: {0}" -f $ContentType)) | Out-Null
        }

        if ($null -ne $Body) {
            $bodyText = [string]$Body
            $isJsonBody = (-not [string]::IsNullOrWhiteSpace($ContentType)) -and $ContentType.StartsWith("application/json", [StringComparison]::OrdinalIgnoreCase)
            if ($isJsonBody) {
                # Windows PowerShell strips quotes in native args for JSON payloads.
                # Write the payload to a temp file and send it as binary data.
                $tempBodyFile = [System.IO.Path]::GetTempFileName()
                [System.IO.File]::WriteAllText($tempBodyFile, $bodyText, [System.Text.Encoding]::UTF8)
                $curlArgs.Add("--data-binary") | Out-Null
                $curlArgs.Add("@$tempBodyFile") | Out-Null
            }
            else {
                $curlArgs.Add("--data-raw") | Out-Null
                $curlArgs.Add($bodyText) | Out-Null
            }
        }

        $curlArgs.Add("--max-time") | Out-Null
        $curlArgs.Add([string]$TimeoutSec) | Out-Null
        $curlArgs.Add("-w") | Out-Null
        $curlArgs.Add("`n__STATUS__:%{http_code}") | Out-Null
        $curlArgs.Add($Uri) | Out-Null

        $rawOutput = & curl.exe @curlArgs 2>&1
        $curlExitCode = $LASTEXITCODE
        $sw.Stop()

        if ($curlExitCode -ne 0) {
            return [pscustomobject]@{
                StatusCode = 0
                Body = ""
                Json = $null
                Error = (($rawOutput | ForEach-Object { [string]$_ }) -join "`n")
                DurationMs = [int]$sw.ElapsedMilliseconds
            }
        }

        $text = ($rawOutput -join "`n")
        $marker = "__STATUS__:"
        $markerIndex = $text.LastIndexOf($marker, [StringComparison]::Ordinal)
        if ($markerIndex -lt 0) {
            return [pscustomobject]@{
                StatusCode = 0
                Body = $text
                Json = $null
                Error = "Unable to parse HTTP status marker from curl output."
                DurationMs = [int]$sw.ElapsedMilliseconds
            }
        }

        $bodyText = $text.Substring(0, $markerIndex).TrimEnd("`r", "`n")
        $statusText = $text.Substring($markerIndex + $marker.Length).Trim()
        $statusCode = 0
        [int]::TryParse($statusText, [ref]$statusCode) | Out-Null

        $json = ConvertFrom-JsonSafe -Content $bodyText

        return [pscustomobject]@{
            StatusCode = $statusCode
            Body = $bodyText
            Json = $json
            Error = $null
            DurationMs = [int]$sw.ElapsedMilliseconds
        }
    }
    catch {
        $sw.Stop()
        return [pscustomobject]@{
            StatusCode = 0
            Body = ""
            Json = $null
            Error = $_.Exception.Message
            DurationMs = [int]$sw.ElapsedMilliseconds
        }
    }
    finally {
        if ($tempBodyFile -and (Test-Path $tempBodyFile)) {
            Remove-Item -Path $tempBodyFile -Force -ErrorAction SilentlyContinue
        }
    }
}

function Invoke-HttpRequest {
    param(
        [string]$Method,
        [string]$Uri,
        [hashtable]$Headers,
        [object]$Body,
        [string]$ContentType
    )

    if (-not $supportsSkipCertificateCheck -and $isLegacyWindowsPowerShell) {
        return Invoke-HttpRequestLegacy `
            -Method $Method `
            -Uri $Uri `
            -Headers $Headers `
            -Body $Body `
            -ContentType $ContentType
    }

    $params = @{
        Uri = $Uri
        Method = $Method
        TimeoutSec = $TimeoutSec
    }

    if ($supportsSkipCertificateCheck -and $Uri.StartsWith("https://", [StringComparison]::OrdinalIgnoreCase)) {
        $params["SkipCertificateCheck"] = $true
    }
    if ($null -ne $Headers -and $Headers.Count -gt 0) {
        $params["Headers"] = $Headers
    }
    if ($null -ne $Body) {
        $params["Body"] = $Body
    }
    if (-not [string]::IsNullOrWhiteSpace($ContentType)) {
        $params["ContentType"] = $ContentType
    }

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $response = Invoke-WebRequest @params
        $sw.Stop()
        $json = ConvertFrom-JsonSafe -Content $response.Content

        return [pscustomobject]@{
            StatusCode = [int]$response.StatusCode
            Body = $response.Content
            Json = $json
            Error = $null
            DurationMs = [int]$sw.ElapsedMilliseconds
        }
    }
    catch {
        $sw.Stop()
        $webResponse = $null
        try {
            $webResponse = $_.Exception.Response
        }
        catch {
            $webResponse = $null
        }
        if ($webResponse -and $webResponse -is [System.Net.HttpWebResponse]) {
            $statusCode = [int]$webResponse.StatusCode
            $bodyText = ""

            try {
                $responseStream = $webResponse.GetResponseStream()
                if ($responseStream) {
                    $reader = New-Object System.IO.StreamReader($responseStream)
                    try {
                        $bodyText = $reader.ReadToEnd()
                    }
                    finally {
                        $reader.Dispose()
                        $responseStream.Dispose()
                    }
                }
            }
            catch {
                $bodyText = ""
            }

            $json = ConvertFrom-JsonSafe -Content $bodyText

            return [pscustomobject]@{
                StatusCode = $statusCode
                Body = $bodyText
                Json = $json
                Error = $null
                DurationMs = [int]$sw.ElapsedMilliseconds
            }
        }

        if ($webResponse -and $webResponse -is [System.Net.Http.HttpResponseMessage]) {
            $statusCode = [int]$webResponse.StatusCode
            $bodyText = ""
            if ($_.ErrorDetails -and -not [string]::IsNullOrWhiteSpace($_.ErrorDetails.Message)) {
                $bodyText = $_.ErrorDetails.Message
            }

            $json = ConvertFrom-JsonSafe -Content $bodyText

            return [pscustomobject]@{
                StatusCode = $statusCode
                Body = $bodyText
                Json = $json
                Error = $null
                DurationMs = [int]$sw.ElapsedMilliseconds
            }
        }

        return [pscustomobject]@{
            StatusCode = 0
            Body = ""
            Json = $null
            Error = $_.Exception.Message
            DurationMs = [int]$sw.ElapsedMilliseconds
        }
    }
}

function Request-Token {
    param([hashtable]$FormData)

    return Invoke-AuthFormPost -Path "/connect/token" -FormData $FormData
}

function Invoke-AuthFormPost {
    param(
        [string]$Path,
        [hashtable]$FormData
    )

    $pairs = foreach ($key in $FormData.Keys) {
        $value = $FormData[$key]
        if ($null -eq $value) {
            continue
        }

        $encodedKey = [System.Uri]::EscapeDataString([string]$key)
        $encodedValue = [System.Uri]::EscapeDataString([string]$value)
        "{0}={1}" -f $encodedKey, $encodedValue
    }

    $formBody = ($pairs -join "&")

    return Invoke-HttpRequest `
        -Method "POST" `
        -Uri ("{0}{1}" -f $AuthBase, $Path) `
        -Body $formBody `
        -ContentType "application/x-www-form-urlencoded"
}

function ConvertFrom-Base64Url {
    param([string]$Value)

    $normalized = $Value.Replace('-', '+').Replace('_', '/')
    switch ($normalized.Length % 4) {
        2 { $normalized += "==" }
        3 { $normalized += "=" }
        default { }
    }
    return [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($normalized))
}

function Get-JwtPayload {
    param([string]$Token)

    if ([string]::IsNullOrWhiteSpace($Token)) {
        return $null
    }

    $parts = $Token.Split('.')
    if ($parts.Length -lt 2) {
        return $null
    }

    try {
        $payloadJson = ConvertFrom-Base64Url -Value $parts[1]
        return ConvertFrom-JsonSafe -Content $payloadJson
    }
    catch {
        return $null
    }
}

function TryParse-Guid {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $null
    }

    $g = [Guid]::Empty
    if ([Guid]::TryParse($Value, [ref]$g)) {
        return $g
    }
    return $null
}

function Build-RefreshForm {
    param([string]$RefreshTokenValue)

    $form = @{
        grant_type = "refresh_token"
        client_id = $PasswordClientId
        refresh_token = $RefreshTokenValue
    }
    if (-not [string]::IsNullOrWhiteSpace($PasswordClientSecret)) {
        $form["client_secret"] = $PasswordClientSecret
    }
    return $form
}

function Build-RevokeForm {
    param([string]$TokenValue)

    $form = @{
        client_id = $PasswordClientId
        token = $TokenValue
        token_type_hint = "refresh_token"
    }
    if (-not [string]::IsNullOrWhiteSpace($PasswordClientSecret)) {
        $form["client_secret"] = $PasswordClientSecret
    }
    return $form
}

function Is-OAuthError {
    param(
        [object]$Response,
        [string]$ErrorCode
    )

    if ($null -eq $Response) {
        return $false
    }

    if ($Response.Json -and $Response.Json.error) {
        return ([string]$Response.Json.error -eq $ErrorCode)
    }

    if (-not [string]::IsNullOrWhiteSpace($Response.Body)) {
        return $Response.Body.Contains($ErrorCode)
    }

    return $false
}

function Is-OAuthErrorAny {
    param(
        [object]$Response,
        [string[]]$ErrorCodes
    )

    foreach ($errorCode in $ErrorCodes) {
        if (Is-OAuthError -Response $Response -ErrorCode $errorCode) {
            return $true
        }
    }

    return $false
}

function Invoke-GetLoadTest {
    param(
        [string]$Uri,
        [hashtable]$Headers,
        [int]$TotalCalls,
        [int]$Concurrency,
        [int]$ExpectedStatusCode = 200,
        [ValidateSet("GET", "POST")]
        [string]$Method = "GET",
        [object]$Body = $null,
        [string]$ContentType = ""
    )

    if ($TotalCalls -lt 1) {
        return $null
    }

    if ($Concurrency -lt 1) {
        $Concurrency = 1
    }
    if ($Concurrency -gt $TotalCalls) {
        $Concurrency = $TotalCalls
    }

    $parallelSupported = ($PSVersionTable.PSVersion.Major -ge 7) -and (Get-Command ForEach-Object).Parameters.ContainsKey("Parallel")
    $effectiveMode = if ($parallelSupported -and $Concurrency -gt 1) { "parallel" } else { "sequential" }
    $overallSw = [System.Diagnostics.Stopwatch]::StartNew()
    $resultList = [System.Collections.Generic.List[object]]::new()

    if ($effectiveMode -eq "parallel") {
        $targetUri = $Uri
        $requestHeaders = $Headers
        $requestTimeoutSec = $TimeoutSec
        $requestMethod = $Method
        $requestBody = $Body
        $requestContentType = $ContentType
        $skipCert = $Uri.StartsWith("https://", [StringComparison]::OrdinalIgnoreCase)
        $parallelResults = 1..$TotalCalls | ForEach-Object -ThrottleLimit $Concurrency -Parallel {
            $callSw = [System.Diagnostics.Stopwatch]::StartNew()
            try {
                $requestParams = @{
                    Uri = $using:targetUri
                    Method = $using:requestMethod
                    TimeoutSec = $using:requestTimeoutSec
                    Headers = $using:requestHeaders
                }
                if ($using:skipCert) {
                    $requestParams["SkipCertificateCheck"] = $true
                }
                if ($using:requestMethod -eq "POST") {
                    $requestParams["Body"] = $using:requestBody
                    if (-not [string]::IsNullOrWhiteSpace($using:requestContentType)) {
                        $requestParams["ContentType"] = $using:requestContentType
                    }
                }

                $response = Invoke-WebRequest @requestParams
                $callSw.Stop()

                [pscustomobject]@{
                    StatusCode = [int]$response.StatusCode
                    DurationMs = [int]$callSw.ElapsedMilliseconds
                    Error = ""
                }
            }
            catch {
                $callSw.Stop()
                $statusCode = 0
                try {
                    $response = $_.Exception.Response
                    if ($response -and $response -is [System.Net.HttpWebResponse]) {
                        $statusCode = [int]$response.StatusCode
                    }
                    elseif ($response -and $response -is [System.Net.Http.HttpResponseMessage]) {
                        $statusCode = [int]$response.StatusCode
                    }
                }
                catch {
                    $statusCode = 0
                }

                [pscustomobject]@{
                    StatusCode = $statusCode
                    DurationMs = [int]$callSw.ElapsedMilliseconds
                    Error = $_.Exception.Message
                }
            }
        }

        foreach ($item in $parallelResults) {
            $resultList.Add($item) | Out-Null
        }
    }
    else {
        for ($i = 1; $i -le $TotalCalls; $i++) {
            $response = Invoke-HttpRequest `
                -Method $Method `
                -Uri $Uri `
                -Headers $Headers `
                -Body $Body `
                -ContentType $ContentType

            $statusCode = [int]$response.StatusCode
            $errorText = if ($response.Error) { [string]$response.Error } else { "" }
            $resultList.Add([pscustomobject]@{
                    StatusCode = $statusCode
                    DurationMs = [int]$response.DurationMs
                    Error = $errorText
                }) | Out-Null
        }
    }

    $overallSw.Stop()
    $results = @($resultList)
    $durationValues = @($results | ForEach-Object { [int]$_.DurationMs })
    $successCount = @($results | Where-Object { $_.StatusCode -eq $ExpectedStatusCode }).Count
    $failedCount = $TotalCalls - $successCount
    $statusSummary = ($results | Group-Object -Property StatusCode | Sort-Object Name | ForEach-Object { "{0}={1}" -f $_.Name, $_.Count }) -join ", "

    $errorSamples = @(
        $results |
        Where-Object { $_.StatusCode -ne $ExpectedStatusCode } |
        Select-Object -ExpandProperty Error -First 3 |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    )

    $totalSeconds = [Math]::Max($overallSw.Elapsed.TotalSeconds, 0.001)
    $rps = [Math]::Round(($TotalCalls / $totalSeconds), 2)

    return [pscustomobject]@{
        Mode = $effectiveMode
        Method = $Method
        TotalCalls = $TotalCalls
        Concurrency = $Concurrency
        SuccessCalls = $successCount
        FailedCalls = $failedCount
        MinMs = if ($durationValues.Count) { ($durationValues | Measure-Object -Minimum).Minimum } else { 0 }
        AvgMs = if ($durationValues.Count) { [Math]::Round((($durationValues | Measure-Object -Average).Average), 0) } else { 0 }
        P50Ms = Get-PercentileMs -Values $durationValues -Percentile 0.50
        P95Ms = Get-PercentileMs -Values $durationValues -Percentile 0.95
        MaxMs = if ($durationValues.Count) { ($durationValues | Measure-Object -Maximum).Maximum } else { 0 }
        TotalMs = [int]$overallSw.ElapsedMilliseconds
        RequestsPerSecond = $rps
        StatusSummary = if ([string]::IsNullOrWhiteSpace($statusSummary)) { "none" } else { $statusSummary }
        ErrorSample = if ($errorSamples.Count -gt 0) { $errorSamples -join " | " } else { "" }
    }
}

function Invoke-SmokeOnce {
    param([int]$Iteration, [int]$Total)

    $script:results = [System.Collections.Generic.List[object]]::new()
    $script:failed = $false

    Write-Host "Running auth/token/permission smoke test..." -ForegroundColor Cyan
    Write-Host "Auth: $AuthBase"
    Write-Host "Permissions: $PermissionsBase"
    Write-Host "Identity: $IdentityBase"
    Write-Host "Tenant: $TenantBase"
    $modeText = if ($Basic) { "Basic" } elseif ($Benchmark) { "Benchmark" } else { "Full" }
    Write-Host "Mode: $modeText"
    if ($Total -gt 1) {
        Write-Host ("Iteration: {0}/{1}" -f $Iteration, $Total) -ForegroundColor DarkCyan
    }

    $featureTokenResponse = Request-Token -FormData @{
        grant_type = "client_credentials"
        client_id = $InternalClientId
        client_secret = $InternalClientSecret
        scope = "features.check"
    }

    if ($featureTokenResponse.StatusCode -eq 200 -and $featureTokenResponse.Json -and $featureTokenResponse.Json.access_token) {
        Add-Result -Step "Token (features.check)" -Status "PASS" -Details "Access token issued." -DurationMs $featureTokenResponse.DurationMs
        $featureAccessToken = [string]$featureTokenResponse.Json.access_token
    }
    else {
        $detail = if ($featureTokenResponse.Error) { $featureTokenResponse.Error } else { "Status=$($featureTokenResponse.StatusCode) Body=$($featureTokenResponse.Body)" }
        Add-Result -Step "Token (features.check)" -Status "FAIL" -Details $detail -DurationMs $featureTokenResponse.DurationMs
        $featureAccessToken = ""
    }

    if ([string]::IsNullOrWhiteSpace($featureAccessToken)) {
        Add-Result -Step "Feature check call" -Status "SKIP" -Details "Skipped because feature token was not issued."
    }
    else {
        $tenantGuid = TryParse-Guid -Value $TenantId
        $featureBody = @{
            tenantId = $tenantGuid
            featureNames = @($FeatureName)
        } | ConvertTo-Json -Depth 10

        $featureCheckResponse = Invoke-HttpRequest `
            -Method "POST" `
            -Uri "$PermissionsBase/api/permissions/feature-check/batch" `
            -Headers @{ Authorization = "Bearer $featureAccessToken" } `
            -Body $featureBody `
            -ContentType "application/json"

        if ($featureCheckResponse.StatusCode -eq 200 -and $featureCheckResponse.Json -and $featureCheckResponse.Json.results) {
            Add-Result -Step "Feature check call" -Status "PASS" -Details "Feature endpoint responded with results." -DurationMs $featureCheckResponse.DurationMs
        }
        else {
            $detail = if ($featureCheckResponse.Error) { $featureCheckResponse.Error } else { "Status=$($featureCheckResponse.StatusCode) Body=$($featureCheckResponse.Body)" }
            Add-Result -Step "Feature check call" -Status "FAIL" -Details $detail -DurationMs $featureCheckResponse.DurationMs
        }
    }

    if (-not $SkipDevProvision) {
        if ([string]::IsNullOrWhiteSpace($featureAccessToken)) {
            Add-Result -Step "Dev provision (admin grants)" -Status "SKIP" -Details "Skipped because feature token was not issued."
        }
        else {
            $tenantGuid = TryParse-Guid -Value $TenantId
            if (-not $tenantGuid) {
                $tenantGuid = [Guid]::Parse("11111111-1111-1111-1111-111111111111")
            }

            $provisionBody = @{ tenantId = $tenantGuid } | ConvertTo-Json -Depth 10
            $provisionResponse = Invoke-HttpRequest `
                -Method "POST" `
                -Uri "$PermissionsBase/api/permissions/dev/provision/default-admin-grants" `
                -Headers @{ Authorization = "Bearer $featureAccessToken" } `
                -Body $provisionBody `
                -ContentType "application/json"

            if ($provisionResponse.StatusCode -eq 200) {
                Add-Result -Step "Dev provision (admin grants)" -Status "PASS" -Details "Ensured default admin grants." -DurationMs $provisionResponse.DurationMs
            }
            elseif ($provisionResponse.StatusCode -eq 404) {
                Add-Result -Step "Dev provision (admin grants)" -Status "SKIP" -Details "Endpoint not available in this environment." -DurationMs $provisionResponse.DurationMs
            }
            else {
                $detail = if ($provisionResponse.Error) { $provisionResponse.Error } else { "Status=$($provisionResponse.StatusCode) Body=$($provisionResponse.Body)" }
                Add-Result -Step "Dev provision (admin grants)" -Status "FAIL" -Details $detail -DurationMs $provisionResponse.DurationMs
                return $script:results
            }
        }
    }

    if ($Basic) {
        Add-Result -Step "Full flow (password/refresh/revoke)" -Status "SKIP" -Details "Skipped in Basic mode."
        return $script:results
    }

    $missing = @()
    if ([string]::IsNullOrWhiteSpace($Username)) { $missing += "Username (or SMOKE_USERNAME)" }
    if ([string]::IsNullOrWhiteSpace($UserPassword)) { $missing += "UserPassword (or SMOKE_PASSWORD)" }
    if ([string]::IsNullOrWhiteSpace($Tenant) -and [string]::IsNullOrWhiteSpace($TenantId)) { $missing += "Tenant or TenantId (or SMOKE_TENANT / SMOKE_TENANT_ID)" }

    if ($missing.Count -gt 0) {
        Add-Result -Step "Password flow prerequisites" -Status "FAIL" -Details ("Missing: " + ($missing -join ", "))
        return $script:results
    }

    $passwordForm = @{
        grant_type = "password"
        client_id = $PasswordClientId
        username = $Username
        password = $UserPassword
        scope = $PasswordScopes
    }
    if ([string]::IsNullOrWhiteSpace($DeviceFingerprint)) {
        $DeviceFingerprint = "smoke-device-fingerprint"
    }
    $passwordForm["device_fingerprint"] = $DeviceFingerprint
    $passwordForm["remember_trusted_device"] = if ($RememberTrustedDevice) { "true" } else { "false" }
    if (-not [string]::IsNullOrWhiteSpace($PasswordClientSecret)) {
        $passwordForm["client_secret"] = $PasswordClientSecret
    }
    if (-not [string]::IsNullOrWhiteSpace($TenantId)) {
        $passwordForm["tenantid"] = $TenantId
    }
    elseif (-not [string]::IsNullOrWhiteSpace($Tenant)) {
        $passwordForm["tenant"] = $Tenant
    }

    $passwordTokenResponse = Request-Token -FormData $passwordForm
    if ($passwordTokenResponse.StatusCode -eq 400 -and (Is-OAuthError -Response $passwordTokenResponse -ErrorCode "invalid_scope")) {
        $scopeParts = @(([string]$passwordForm["scope"]).Split(" ", [StringSplitOptions]::RemoveEmptyEntries))
        if ($scopeParts -contains "tenant") {
            $fallbackScope = (($scopeParts | Where-Object { $_ -ne "tenant" }) -join " ").Trim()
            if (-not [string]::IsNullOrWhiteSpace($fallbackScope)) {
                $passwordForm["scope"] = $fallbackScope
                $passwordTokenResponse = Request-Token -FormData $passwordForm
                $fallbackStatus = if ($passwordTokenResponse.StatusCode -eq 200 -and $passwordTokenResponse.Json -and $passwordTokenResponse.Json.access_token) { "PASS" } else { "FAIL" }
                Add-Result -Step "Password scope fallback" -Status $fallbackStatus -Details ("Retried without tenant scope: {0}" -f $fallbackScope)
            }
        }
    }
    $passwordAccessToken = ""
    $refreshToken = ""
    $jwtPayload = $null

    if ($passwordTokenResponse.StatusCode -eq 200 -and $passwordTokenResponse.Json -and $passwordTokenResponse.Json.access_token) {
        $passwordAccessToken = [string]$passwordTokenResponse.Json.access_token
        if ($passwordTokenResponse.Json.refresh_token) {
            $refreshToken = [string]$passwordTokenResponse.Json.refresh_token
        }
        $jwtPayload = Get-JwtPayload -Token $passwordAccessToken
        Add-Result -Step "Token (password)" -Status "PASS" -Details "Password token issued." -DurationMs $passwordTokenResponse.DurationMs
    }
    else {
        $detail = if ($passwordTokenResponse.Error) { $passwordTokenResponse.Error } else { "Status=$($passwordTokenResponse.StatusCode) Body=$($passwordTokenResponse.Body)" }
        Add-Result -Step "Token (password)" -Status "FAIL" -Details $detail -DurationMs $passwordTokenResponse.DurationMs
        return $script:results
    }

    $resolvedUserId = $null
    if ($jwtPayload -and $jwtPayload.PSObject.Properties.Name -contains "sub") {
        $resolvedUserId = TryParse-Guid -Value ([string]$jwtPayload.sub)
    }
    if (-not $resolvedUserId) {
        $resolvedUserId = TryParse-Guid -Value $UserId
    }

    $resolvedSessionId = ""
    if ($jwtPayload -and $jwtPayload.PSObject.Properties.Name -contains "session_id") {
        $resolvedSessionId = [string]$jwtPayload.session_id
    }

    $resolvedTenantId = TryParse-Guid -Value $TenantId
    if (-not $resolvedTenantId -and $jwtPayload -and $jwtPayload.PSObject.Properties.Name -contains "tenantid") {
        $resolvedTenantId = TryParse-Guid -Value ([string]$jwtPayload.tenantid)
    }

    $resolvedRoles = @($RoleName)
    if ($jwtPayload -and $jwtPayload.PSObject.Properties.Name -contains "role") {
        if ($jwtPayload.role -is [System.Array]) {
            if ($jwtPayload.role.Count -gt 0) {
                $resolvedRoles = @([string]$jwtPayload.role[0])
            }
        }
        elseif (-not [string]::IsNullOrWhiteSpace([string]$jwtPayload.role)) {
            $resolvedRoles = @([string]$jwtPayload.role)
        }
    }

    if (-not $SkipNegativeChecks) {
        $invalidTenantForm = @{
            grant_type = "password"
            client_id = $PasswordClientId
            username = $Username
            password = $UserPassword
            scope = $PasswordScopes
            tenantid = [Guid]::NewGuid().ToString()
        }
        if (-not [string]::IsNullOrWhiteSpace($PasswordClientSecret)) {
            $invalidTenantForm["client_secret"] = $PasswordClientSecret
        }

        $invalidTenantTokenResponse = Request-Token -FormData $invalidTenantForm
        $invalidTenantDenied = $false
        if ($invalidTenantTokenResponse.StatusCode -ne 200) {
            $invalidTenantDenied = Is-OAuthErrorAny -Response $invalidTenantTokenResponse -ErrorCodes @("invalid_grant", "invalid_request")
            if (-not $invalidTenantDenied -and [string]::IsNullOrWhiteSpace($invalidTenantTokenResponse.Body)) {
                $invalidTenantDenied = $true
            }
        }

        if ($invalidTenantDenied) {
            Add-Result -Step "Token (password invalid tenant)" -Status "PASS" -Details "Invalid tenant request denied as expected." -DurationMs $invalidTenantTokenResponse.DurationMs
        }
        else {
            $detail = if ($invalidTenantTokenResponse.Error) { $invalidTenantTokenResponse.Error } else { "Status=$($invalidTenantTokenResponse.StatusCode) Body=$($invalidTenantTokenResponse.Body)" }
            Add-Result -Step "Token (password invalid tenant)" -Status "FAIL" -Details $detail -DurationMs $invalidTenantTokenResponse.DurationMs
        }

        $negativeFeatureBody = @{
            tenantId = $resolvedTenantId
            featureNames = @($FeatureName)
        } | ConvertTo-Json -Depth 10

        $featureWithUserTokenResponse = Invoke-HttpRequest `
            -Method "POST" `
            -Uri "$PermissionsBase/api/permissions/feature-check/batch" `
            -Headers @{ Authorization = "Bearer $passwordAccessToken" } `
            -Body $negativeFeatureBody `
            -ContentType "application/json"

        if ($featureWithUserTokenResponse.StatusCode -eq 401 -or $featureWithUserTokenResponse.StatusCode -eq 403) {
            Add-Result -Step "Feature check with user token (should deny)" -Status "PASS" -Details "Endpoint denied non-M2M token as expected." -DurationMs $featureWithUserTokenResponse.DurationMs
        }
        else {
            $detail = if ($featureWithUserTokenResponse.Error) { $featureWithUserTokenResponse.Error } else { "Status=$($featureWithUserTokenResponse.StatusCode) Body=$($featureWithUserTokenResponse.Body)" }
            Add-Result -Step "Feature check with user token (should deny)" -Status "FAIL" -Details $detail -DurationMs $featureWithUserTokenResponse.DurationMs
        }
    }
    else {
        Add-Result -Step "Token (password invalid tenant)" -Status "SKIP" -Details "Skipped by -SkipNegativeChecks."
        Add-Result -Step "Feature check with user token (should deny)" -Status "SKIP" -Details "Skipped by -SkipNegativeChecks."
    }

    if (-not $SkipAuthorizedApiCheck) {
        $usersHeaders = @{
            Authorization = "Bearer $passwordAccessToken"
        }
        if ($resolvedTenantId) {
            $usersHeaders["X-Tenant-Id"] = $resolvedTenantId.ToString()
        }

        $usersApiResponse = Invoke-HttpRequest `
            -Method "GET" `
            -Uri "$IdentityBase/api/identity/sessions/me" `
            -Headers $usersHeaders `
            -Body $null `
            -ContentType ""

        if ($usersApiResponse.StatusCode -eq 200) {
            Add-Result -Step "API (Identity.Sessions.Me)" -Status "PASS" -Details "Authorized user API responded 200." -DurationMs $usersApiResponse.DurationMs
        }
        else {
            $detail = if ($usersApiResponse.Error) { $usersApiResponse.Error } else { "Status=$($usersApiResponse.StatusCode) Body=$($usersApiResponse.Body)" }
            Add-Result -Step "API (Identity.Sessions.Me)" -Status "FAIL" -Details $detail -DurationMs $usersApiResponse.DurationMs
            return $script:results
        }
    }

    if (-not $resolvedTenantId) {
        Add-Result -Step "API (Tenant.Internal) burst" -Status "FAIL" -Details "Resolved tenant id is unavailable."
        return $script:results
    }

    $tenantInternalTokenResponse = Request-Token -FormData @{
        grant_type = "client_credentials"
        client_id = $InternalClientId
        client_secret = $InternalClientSecret
        scope = "tenant-internal"
    }

    $tenantInternalAccessToken = ""
    if ($tenantInternalTokenResponse.StatusCode -eq 200 -and $tenantInternalTokenResponse.Json -and $tenantInternalTokenResponse.Json.access_token) {
        $tenantInternalAccessToken = [string]$tenantInternalTokenResponse.Json.access_token
        Add-Result -Step "Token (tenant-internal)" -Status "PASS" -Details "Internal tenant token issued." -DurationMs $tenantInternalTokenResponse.DurationMs
    }
    else {
        $detail = if ($tenantInternalTokenResponse.Error) { $tenantInternalTokenResponse.Error } else { "Status=$($tenantInternalTokenResponse.StatusCode) Body=$($tenantInternalTokenResponse.Body)" }
        Add-Result -Step "Token (tenant-internal)" -Status "FAIL" -Details $detail -DurationMs $tenantInternalTokenResponse.DurationMs
        return $script:results
    }

    $tenantHeaders = @{
        Authorization = "Bearer $tenantInternalAccessToken"
        "X-Tenant-Id" = $resolvedTenantId.ToString()
    }

    $tenantApiFailed = $false
    $tenantApiDurations = New-Object System.Collections.Generic.List[int]
    for ($tenantCallIndex = 1; $tenantCallIndex -le $TenantApiBurstCalls; $tenantCallIndex++) {
        $tenantApiResponse = Invoke-HttpRequest `
            -Method "GET" `
            -Uri "$TenantBase/api/tenant/internal/tenants/$($resolvedTenantId.ToString())" `
            -Headers $tenantHeaders `
            -Body $null `
            -ContentType ""

        $tenantApiDurations.Add([int]$tenantApiResponse.DurationMs) | Out-Null
        if ($tenantApiResponse.StatusCode -ne 200) {
            $detail = if ($tenantApiResponse.Error) { $tenantApiResponse.Error } else { "Status=$($tenantApiResponse.StatusCode) Body=$($tenantApiResponse.Body)" }
            Add-Result -Step ("API (Tenant.Internal) call {0}/{1}" -f $tenantCallIndex, $TenantApiBurstCalls) -Status "FAIL" -Details $detail -DurationMs $tenantApiResponse.DurationMs
            $tenantApiFailed = $true
            break
        }
    }

    if (-not $tenantApiFailed) {
        $avgTenantMs = if ($tenantApiDurations.Count -gt 0) { [Math]::Round((($tenantApiDurations | Measure-Object -Average).Average), 0) } else { 0 }
        Add-Result -Step ("API (Tenant.Internal) burst x{0}" -f $TenantApiBurstCalls) -Status "PASS" -Details ("All calls returned 200. Avg={0}ms" -f $avgTenantMs)
    }
    else {
        return $script:results
    }

    if ($TenantApiLoadTotalCalls -gt 0) {
        $tenantLoadResult = Invoke-GetLoadTest `
            -Uri "$TenantBase/api/tenant/tenants" `
            -Headers $tenantHeaders `
            -TotalCalls $TenantApiLoadTotalCalls `
            -Concurrency $TenantApiLoadConcurrency `
            -ExpectedStatusCode 200

        if ($tenantLoadResult) {
            $loadDetail = "mode={0}, method={1}, total={2}, concurrency={3}, success={4}, failed={5}, min/avg/p50/p95/max={6}/{7}/{8}/{9}/{10}ms, totalMs={11}, rps={12}, statuses={13}" -f `
                $tenantLoadResult.Mode, `
                $tenantLoadResult.Method, `
                $tenantLoadResult.TotalCalls, `
                $tenantLoadResult.Concurrency, `
                $tenantLoadResult.SuccessCalls, `
                $tenantLoadResult.FailedCalls, `
                $tenantLoadResult.MinMs, `
                $tenantLoadResult.AvgMs, `
                $tenantLoadResult.P50Ms, `
                $tenantLoadResult.P95Ms, `
                $tenantLoadResult.MaxMs, `
                $tenantLoadResult.TotalMs, `
                $tenantLoadResult.RequestsPerSecond, `
                $tenantLoadResult.StatusSummary

            if ($tenantLoadResult.FailedCalls -eq 0) {
                Add-Result -Step "API Load (Tenant.Management)" -Status "PASS" -Details $loadDetail -DurationMs $tenantLoadResult.TotalMs
            }
            else {
                if (-not [string]::IsNullOrWhiteSpace($tenantLoadResult.ErrorSample)) {
                    $loadDetail = "{0}, errors={1}" -f $loadDetail, $tenantLoadResult.ErrorSample
                }
                Add-Result -Step "API Load (Tenant.Management)" -Status "FAIL" -Details $loadDetail -DurationMs $tenantLoadResult.TotalMs
                return $script:results
            }
        }
    }

    if ($TenantApiLoadTotalCalls -gt 0) {
        if ([string]::IsNullOrWhiteSpace($passwordAccessToken)) {
            Add-Result -Step "API Load (Identity.Sessions UserToken)" -Status "SKIP" -Details "Skipped because password access token is unavailable."
        }
        else {
            $identitySessionsLoadResult = Invoke-GetLoadTest `
                -Uri "$IdentityBase/api/identity/sessions" `
                -Headers @{ Authorization = "Bearer $passwordAccessToken" } `
                -TotalCalls $TenantApiLoadTotalCalls `
                -Concurrency $TenantApiLoadConcurrency `
                -ExpectedStatusCode 200 `
                -Method "GET"

            if ($identitySessionsLoadResult) {
                $loadDetail = "mode={0}, method={1}, total={2}, concurrency={3}, success={4}, failed={5}, min/avg/p50/p95/max={6}/{7}/{8}/{9}/{10}ms, totalMs={11}, rps={12}, statuses={13}" -f `
                    $identitySessionsLoadResult.Mode, `
                    $identitySessionsLoadResult.Method, `
                    $identitySessionsLoadResult.TotalCalls, `
                    $identitySessionsLoadResult.Concurrency, `
                    $identitySessionsLoadResult.SuccessCalls, `
                    $identitySessionsLoadResult.FailedCalls, `
                    $identitySessionsLoadResult.MinMs, `
                    $identitySessionsLoadResult.AvgMs, `
                    $identitySessionsLoadResult.P50Ms, `
                    $identitySessionsLoadResult.P95Ms, `
                    $identitySessionsLoadResult.MaxMs, `
                    $identitySessionsLoadResult.TotalMs, `
                    $identitySessionsLoadResult.RequestsPerSecond, `
                    $identitySessionsLoadResult.StatusSummary

                if ($identitySessionsLoadResult.FailedCalls -eq 0) {
                    Add-Result -Step "API Load (Identity.Sessions UserToken)" -Status "PASS" -Details $loadDetail -DurationMs $identitySessionsLoadResult.TotalMs
                }
                else {
                    if (-not [string]::IsNullOrWhiteSpace($identitySessionsLoadResult.ErrorSample)) {
                        $loadDetail = "{0}, errors={1}" -f $loadDetail, $identitySessionsLoadResult.ErrorSample
                    }
                    Add-Result -Step "API Load (Identity.Sessions UserToken)" -Status "FAIL" -Details $loadDetail -DurationMs $identitySessionsLoadResult.TotalMs
                    return $script:results
                }
            }
        }

        if (-not $resolvedTenantId) {
            Add-Result -Step "Token (tenant-internal)" -Status "SKIP" -Details "Skipped because resolved tenant id is unavailable."
            Add-Result -Step "API Load (Tenant.Internal M2M)" -Status "SKIP" -Details "Skipped because resolved tenant id is unavailable."
        }
        else {
            $tenantInternalTokenResponse = Request-Token -FormData @{
                grant_type = "client_credentials"
                client_id = $InternalClientId
                client_secret = $InternalClientSecret
                scope = "tenant-internal"
            }

            $tenantInternalAccessToken = ""
            if ($tenantInternalTokenResponse.StatusCode -eq 200 -and $tenantInternalTokenResponse.Json -and $tenantInternalTokenResponse.Json.access_token) {
                $tenantInternalAccessToken = [string]$tenantInternalTokenResponse.Json.access_token
                Add-Result -Step "Token (tenant-internal)" -Status "PASS" -Details "Internal tenant token issued." -DurationMs $tenantInternalTokenResponse.DurationMs
            }
            else {
                $detail = if ($tenantInternalTokenResponse.Error) { $tenantInternalTokenResponse.Error } else { "Status=$($tenantInternalTokenResponse.StatusCode) Body=$($tenantInternalTokenResponse.Body)" }
                Add-Result -Step "Token (tenant-internal)" -Status "FAIL" -Details $detail -DurationMs $tenantInternalTokenResponse.DurationMs
            }

            if ([string]::IsNullOrWhiteSpace($tenantInternalAccessToken)) {
                Add-Result -Step "API Load (Tenant.Internal M2M)" -Status "SKIP" -Details "Skipped because tenant-internal token was not issued."
            }
            else {
                $tenantInternalLoadResult = Invoke-GetLoadTest `
                    -Uri "$TenantBase/api/tenant/internal/tenants/$($resolvedTenantId.ToString())" `
                    -Headers @{
                        Authorization = "Bearer $tenantInternalAccessToken"
                        "X-Tenant-Id" = $resolvedTenantId.ToString()
                    } `
                    -TotalCalls $TenantApiLoadTotalCalls `
                    -Concurrency $TenantApiLoadConcurrency `
                    -ExpectedStatusCode 200 `
                    -Method "GET"

                if ($tenantInternalLoadResult) {
                    $loadDetail = "mode={0}, method={1}, total={2}, concurrency={3}, success={4}, failed={5}, min/avg/p50/p95/max={6}/{7}/{8}/{9}/{10}ms, totalMs={11}, rps={12}, statuses={13}" -f `
                        $tenantInternalLoadResult.Mode, `
                        $tenantInternalLoadResult.Method, `
                        $tenantInternalLoadResult.TotalCalls, `
                        $tenantInternalLoadResult.Concurrency, `
                        $tenantInternalLoadResult.SuccessCalls, `
                        $tenantInternalLoadResult.FailedCalls, `
                        $tenantInternalLoadResult.MinMs, `
                        $tenantInternalLoadResult.AvgMs, `
                        $tenantInternalLoadResult.P50Ms, `
                        $tenantInternalLoadResult.P95Ms, `
                        $tenantInternalLoadResult.MaxMs, `
                        $tenantInternalLoadResult.TotalMs, `
                        $tenantInternalLoadResult.RequestsPerSecond, `
                        $tenantInternalLoadResult.StatusSummary

                    if ($tenantInternalLoadResult.FailedCalls -eq 0) {
                        Add-Result -Step "API Load (Tenant.Internal M2M)" -Status "PASS" -Details $loadDetail -DurationMs $tenantInternalLoadResult.TotalMs
                    }
                    else {
                        if (-not [string]::IsNullOrWhiteSpace($tenantInternalLoadResult.ErrorSample)) {
                            $loadDetail = "{0}, errors={1}" -f $loadDetail, $tenantInternalLoadResult.ErrorSample
                        }
                        Add-Result -Step "API Load (Tenant.Internal M2M)" -Status "FAIL" -Details $loadDetail -DurationMs $tenantInternalLoadResult.TotalMs
                        return $script:results
                    }
                }
            }
        }

        if ([string]::IsNullOrWhiteSpace($featureAccessToken)) {
            Add-Result -Step "API Load (FeatureCheck M2M)" -Status "SKIP" -Details "Skipped because features.check token was not issued."
        }
        else {
            $featureLoadBody = @{
                tenantId = $resolvedTenantId
                featureNames = @($FeatureName)
            } | ConvertTo-Json -Depth 10

            $featureLoadResult = Invoke-GetLoadTest `
                -Uri "$PermissionsBase/api/permissions/feature-check/batch" `
                -Headers @{ Authorization = "Bearer $featureAccessToken" } `
                -TotalCalls $TenantApiLoadTotalCalls `
                -Concurrency $TenantApiLoadConcurrency `
                -ExpectedStatusCode 200 `
                -Method "POST" `
                -Body $featureLoadBody `
                -ContentType "application/json"

            if ($featureLoadResult) {
                $loadDetail = "mode={0}, method={1}, total={2}, concurrency={3}, success={4}, failed={5}, min/avg/p50/p95/max={6}/{7}/{8}/{9}/{10}ms, totalMs={11}, rps={12}, statuses={13}" -f `
                    $featureLoadResult.Mode, `
                    $featureLoadResult.Method, `
                    $featureLoadResult.TotalCalls, `
                    $featureLoadResult.Concurrency, `
                    $featureLoadResult.SuccessCalls, `
                    $featureLoadResult.FailedCalls, `
                    $featureLoadResult.MinMs, `
                    $featureLoadResult.AvgMs, `
                    $featureLoadResult.P50Ms, `
                    $featureLoadResult.P95Ms, `
                    $featureLoadResult.MaxMs, `
                    $featureLoadResult.TotalMs, `
                    $featureLoadResult.RequestsPerSecond, `
                    $featureLoadResult.StatusSummary

                if ($featureLoadResult.FailedCalls -eq 0) {
                    Add-Result -Step "API Load (FeatureCheck M2M)" -Status "PASS" -Details $loadDetail -DurationMs $featureLoadResult.TotalMs
                }
                else {
                    if (-not [string]::IsNullOrWhiteSpace($featureLoadResult.ErrorSample)) {
                        $loadDetail = "{0}, errors={1}" -f $loadDetail, $featureLoadResult.ErrorSample
                    }
                    Add-Result -Step "API Load (FeatureCheck M2M)" -Status "FAIL" -Details $loadDetail -DurationMs $featureLoadResult.TotalMs
                    return $script:results
                }
            }
        }
    }

    if ($Benchmark) {
        return $script:results
    }

    if ([string]::IsNullOrWhiteSpace($passwordAccessToken) -or -not $resolvedUserId) {
        Add-Result -Step "Permission evaluation call" -Status "SKIP" -Details "Skipped because password token or user id is unavailable."
    }
    else {
        $evaluationResponse = Invoke-HttpRequest `
            -Method "GET" `
            -Uri "$PermissionsBase/api/permissions/permission-evaluation/me" `
            -Headers @{ Authorization = "Bearer $passwordAccessToken" } `
            -Body $null

        if ($evaluationResponse.StatusCode -eq 200 -and $evaluationResponse.Json -and $evaluationResponse.Json.permissions -ne $null) {
            Add-Result -Step "Permission evaluation call" -Status "PASS" -Details "Permission evaluation endpoint responded with results." -DurationMs $evaluationResponse.DurationMs
        }
        else {
            $detail = if ($evaluationResponse.Error) { $evaluationResponse.Error } else { "Status=$($evaluationResponse.StatusCode) Body=$($evaluationResponse.Body)" }
            Add-Result -Step "Permission evaluation call" -Status "FAIL" -Details $detail -DurationMs $evaluationResponse.DurationMs
        }
    }

    $refreshTokenForPostRevoke = $refreshToken
    if ([string]::IsNullOrWhiteSpace($refreshToken)) {
        Add-Result -Step "Refresh token (before revoke)" -Status "SKIP" -Details "Skipped because refresh token is unavailable."
        Add-Result -Step "Refresh token replay (same token)" -Status "SKIP" -Details "Skipped because refresh token is unavailable."
    }
    else {
        $originalRefreshToken = $refreshToken
        $refreshResponse = Request-Token -FormData (Build-RefreshForm -RefreshTokenValue $originalRefreshToken)
        if ($refreshResponse.StatusCode -eq 200 -and $refreshResponse.Json -and $refreshResponse.Json.access_token) {
            if ($refreshResponse.Json.refresh_token) {
                $refreshTokenForPostRevoke = [string]$refreshResponse.Json.refresh_token
            }
            Add-Result -Step "Refresh token (before revoke)" -Status "PASS" -Details "Refresh succeeded." -DurationMs $refreshResponse.DurationMs

            $replayResponse = Request-Token -FormData (Build-RefreshForm -RefreshTokenValue $originalRefreshToken)
            $isReplayRejected = $false
            if ($replayResponse.StatusCode -eq 400) {
                $isReplayRejected = Is-OAuthErrorAny -Response $replayResponse -ErrorCodes @("invalid_grant", "invalid_token")
            }

            if ($isReplayRejected) {
                Add-Result -Step "Refresh token replay (same token)" -Status "PASS" -Details "Replay rejected as expected." -DurationMs $replayResponse.DurationMs
            }
            else {
                $detail = if ($replayResponse.Error) { $replayResponse.Error } else { "Status=$($replayResponse.StatusCode) Body=$($replayResponse.Body)" }
                Add-Result -Step "Refresh token replay (same token)" -Status "FAIL" -Details $detail -DurationMs $replayResponse.DurationMs
            }
        }
        else {
            $detail = if ($refreshResponse.Error) { $refreshResponse.Error } else { "Status=$($refreshResponse.StatusCode) Body=$($refreshResponse.Body)" }
            Add-Result -Step "Refresh token (before revoke)" -Status "FAIL" -Details $detail -DurationMs $refreshResponse.DurationMs
            Add-Result -Step "Refresh token replay (same token)" -Status "SKIP" -Details "Skipped because initial refresh failed."
        }
    }

    $identityInternalToken = ""
    $internalTokenResponse = Request-Token -FormData @{
        grant_type = "client_credentials"
        client_id = $InternalClientId
        client_secret = $InternalClientSecret
        scope = "identity-internal"
    }

    if ($internalTokenResponse.StatusCode -eq 200 -and $internalTokenResponse.Json -and $internalTokenResponse.Json.access_token) {
        $identityInternalToken = [string]$internalTokenResponse.Json.access_token
        Add-Result -Step "Token (identity-internal)" -Status "PASS" -Details "Internal token issued." -DurationMs $internalTokenResponse.DurationMs
    }
    else {
        $detail = if ($internalTokenResponse.Error) { $internalTokenResponse.Error } else { "Status=$($internalTokenResponse.StatusCode) Body=$($internalTokenResponse.Body)" }
        Add-Result -Step "Token (identity-internal)" -Status "FAIL" -Details $detail -DurationMs $internalTokenResponse.DurationMs
    }

    if ([string]::IsNullOrWhiteSpace($identityInternalToken) -or -not $resolvedUserId -or [string]::IsNullOrWhiteSpace($resolvedSessionId)) {
        Add-Result -Step "Revoke session" -Status "SKIP" -Details "Skipped because internal token, user id, or session id is unavailable."
    }
    else {
        $headers = @{
            Authorization = "Bearer $identityInternalToken"
            "Idempotency-Key" = [Guid]::NewGuid().ToString()
        }
        if ($resolvedTenantId) {
            $headers["X-Tenant-Id"] = $resolvedTenantId.ToString()
        }

        $revokeBody = @{
            userId = $resolvedUserId
            sessionId = $resolvedSessionId
            tenantId = $resolvedTenantId
        } | ConvertTo-Json -Depth 10

        $revokeResponse = Invoke-HttpRequest `
            -Method "POST" `
            -Uri "$IdentityBase/api/identity/internal/sessions/revoke" `
            -Headers $headers `
            -Body $revokeBody `
            -ContentType "application/json"

        if ($revokeResponse.StatusCode -eq 200) {
            Add-Result -Step "Revoke session" -Status "PASS" -Details "Session revoke request succeeded." -DurationMs $revokeResponse.DurationMs
        }
        else {
            $detail = if ($revokeResponse.Error) { $revokeResponse.Error } else { "Status=$($revokeResponse.StatusCode) Body=$($revokeResponse.Body)" }
            Add-Result -Step "Revoke session" -Status "FAIL" -Details $detail -DurationMs $revokeResponse.DurationMs
        }
    }

    if ([string]::IsNullOrWhiteSpace($refreshTokenForPostRevoke)) {
        Add-Result -Step "Refresh after session revoke" -Status "SKIP" -Details "Skipped because refresh token is unavailable."
    }
    else {
        $refreshAfterRevoke = Request-Token -FormData (Build-RefreshForm -RefreshTokenValue $refreshTokenForPostRevoke)
        $isInvalidGrant = $false

        if ($refreshAfterRevoke.Json -and $refreshAfterRevoke.Json.error) {
            $isInvalidGrant = ([string]$refreshAfterRevoke.Json.error -eq "invalid_grant")
        }
        if (-not $isInvalidGrant -and -not [string]::IsNullOrWhiteSpace($refreshAfterRevoke.Body)) {
            $isInvalidGrant = $refreshAfterRevoke.Body.Contains("invalid_grant")
        }

        if ($refreshAfterRevoke.StatusCode -eq 400 -and $isInvalidGrant) {
            Add-Result -Step "Refresh after session revoke" -Status "PASS" -Details "Expected invalid_grant returned." -DurationMs $refreshAfterRevoke.DurationMs
        }
        else {
            $detail = if ($refreshAfterRevoke.Error) { $refreshAfterRevoke.Error } else { "Status=$($refreshAfterRevoke.StatusCode) Body=$($refreshAfterRevoke.Body)" }
            Add-Result -Step "Refresh after session revoke" -Status "FAIL" -Details $detail -DurationMs $refreshAfterRevoke.DurationMs
        }
    }

    # Validate OpenIddict revocation endpoint independently from session revoke.
    $passwordTokenForRevoke = Request-Token -FormData $passwordForm
    $refreshTokenForAuthRevoke = ""
    if ($passwordTokenForRevoke.StatusCode -eq 200 -and $passwordTokenForRevoke.Json -and $passwordTokenForRevoke.Json.refresh_token) {
        $refreshTokenForAuthRevoke = [string]$passwordTokenForRevoke.Json.refresh_token
        Add-Result -Step "Token (password for connect/revoke)" -Status "PASS" -Details "Issued token for connect/revoke test." -DurationMs $passwordTokenForRevoke.DurationMs
    }
    else {
        $detail = if ($passwordTokenForRevoke.Error) { $passwordTokenForRevoke.Error } else { "Status=$($passwordTokenForRevoke.StatusCode) Body=$($passwordTokenForRevoke.Body)" }
        Add-Result -Step "Token (password for connect/revoke)" -Status "FAIL" -Details $detail -DurationMs $passwordTokenForRevoke.DurationMs
    }

    if ([string]::IsNullOrWhiteSpace($refreshTokenForAuthRevoke)) {
        Add-Result -Step "Token revoke (connect/revoke)" -Status "SKIP" -Details "Skipped because refresh token is unavailable."
        Add-Result -Step "Refresh after connect/revoke" -Status "SKIP" -Details "Skipped because revoke step was skipped."
    }
    else {
        $revokeTokenResponse = Invoke-AuthFormPost -Path "/connect/revoke" -FormData (Build-RevokeForm -TokenValue $refreshTokenForAuthRevoke)
        if ($revokeTokenResponse.StatusCode -ge 200 -and $revokeTokenResponse.StatusCode -lt 300) {
            Add-Result -Step "Token revoke (connect/revoke)" -Status "PASS" -Details "Refresh token revoked by OpenIddict endpoint." -DurationMs $revokeTokenResponse.DurationMs
        }
        else {
            $detail = if ($revokeTokenResponse.Error) { $revokeTokenResponse.Error } else { "Status=$($revokeTokenResponse.StatusCode) Body=$($revokeTokenResponse.Body)" }
            Add-Result -Step "Token revoke (connect/revoke)" -Status "FAIL" -Details $detail -DurationMs $revokeTokenResponse.DurationMs
        }

        $refreshAfterConnectRevoke = Request-Token -FormData (Build-RefreshForm -RefreshTokenValue $refreshTokenForAuthRevoke)
        $isInvalidGrantAfterConnectRevoke = $false
        if ($refreshAfterConnectRevoke.Json -and $refreshAfterConnectRevoke.Json.error) {
            $errorCode = [string]$refreshAfterConnectRevoke.Json.error
            $isInvalidGrantAfterConnectRevoke = ($errorCode -eq "invalid_grant" -or $errorCode -eq "invalid_token")
        }
        if (-not $isInvalidGrantAfterConnectRevoke -and -not [string]::IsNullOrWhiteSpace($refreshAfterConnectRevoke.Body)) {
            $isInvalidGrantAfterConnectRevoke = ($refreshAfterConnectRevoke.Body.Contains("invalid_grant") -or $refreshAfterConnectRevoke.Body.Contains("invalid_token"))
        }

        if ($refreshAfterConnectRevoke.StatusCode -eq 400 -and $isInvalidGrantAfterConnectRevoke) {
            Add-Result -Step "Refresh after connect/revoke" -Status "PASS" -Details "Expected invalid_grant returned." -DurationMs $refreshAfterConnectRevoke.DurationMs
        }
        else {
            $detail = if ($refreshAfterConnectRevoke.Error) { $refreshAfterConnectRevoke.Error } else { "Status=$($refreshAfterConnectRevoke.StatusCode) Body=$($refreshAfterConnectRevoke.Body)" }
            Add-Result -Step "Refresh after connect/revoke" -Status "FAIL" -Details $detail -DurationMs $refreshAfterConnectRevoke.DurationMs
        }
    }

    return $script:results
}

function Get-PercentileMs {
    param([int[]]$Values, [double]$Percentile)
    $valueArray = @($Values)
    if ($valueArray.Count -eq 0) { return 0 }
    $sorted = @($valueArray | Sort-Object)
    $idx = [Math]::Ceiling($Percentile * $sorted.Count) - 1
    if ($idx -lt 0) { $idx = 0 }
    if ($idx -ge $sorted.Count) { $idx = $sorted.Count - 1 }
    return [int]$sorted[$idx]
}

function Get-StepTimingSummary {
    param(
        [object[]]$Runs,
        [string]$StepName
    )

    $times = @($Runs | Where-Object { $_.Step -eq $StepName -and $_.DurationMs -gt 0 } | ForEach-Object { [int]$_.DurationMs })
    return [pscustomobject]@{
        Step = $StepName
        Count = $times.Count
        MinMs = if ($times.Count) { ($times | Measure-Object -Minimum).Minimum } else { 0 }
        AvgMs = if ($times.Count) { [Math]::Round((($times | Measure-Object -Average).Average), 0) } else { 0 }
        P95Ms = Get-PercentileMs -Values $times -Percentile 0.95
        P99Ms = Get-PercentileMs -Values $times -Percentile 0.99
        MaxMs = if ($times.Count) { ($times | Measure-Object -Maximum).Maximum } else { 0 }
    }
}

function New-TimingSummary {
    param([object[]]$Runs)

    return @(
        Get-StepTimingSummary -Runs $Runs -StepName "Token (features.check)"
        Get-StepTimingSummary -Runs $Runs -StepName "Feature check call"
        Get-StepTimingSummary -Runs $Runs -StepName "Token (password)"
    )
}

function Get-StepDurationOrZero {
    param(
        [object[]]$Runs,
        [string]$StepName
    )

    $entry = $Runs | Where-Object { $_.Step -eq $StepName } | Select-Object -First 1
    if ($null -eq $entry) {
        return 0
    }

    $durationProp = $entry.PSObject.Properties | Where-Object { $_.Name -eq "DurationMs" } | Select-Object -First 1
    if ($null -eq $durationProp -or $null -eq $durationProp.Value) {
        return 0
    }

    return [int]$durationProp.Value
}

function Get-StepRequestSummary {
    param(
        [object[]]$Runs,
        [string]$StepName
    )

    $stepRuns = @($Runs | Where-Object { $_.Step -eq $StepName })
    $http429Count = @($stepRuns | Where-Object {
            $details = [string]$_.Details
            $details.Contains("Status=429") -or $details.Contains("Too Many Requests")
        }).Count

    return [pscustomobject]@{
        Step = $StepName
        TotalRequests = $stepRuns.Count
        SuccessRequests = @($stepRuns | Where-Object { $_.Status -eq "PASS" }).Count
        FailedRequests = @($stepRuns | Where-Object { $_.Status -eq "FAIL" }).Count
        SkippedRequests = @($stepRuns | Where-Object { $_.Status -eq "SKIP" }).Count
        Http429Count = $http429Count
    }
}

function Write-MetricsOutput {
    param(
        [string]$Path,
        [object[]]$Runs,
        [int]$TotalIterations,
        [string]$Mode,
        [int]$WarmupRunCount = 0,
        [object]$AuthTelemetry = $null
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return
    }

    $summary = New-TimingSummary -Runs $Runs
    $tokenFeatureSummary = $summary | Where-Object { $_.Step -eq "Token (features.check)" } | Select-Object -First 1
    $tokenPasswordSummary = $summary | Where-Object { $_.Step -eq "Token (password)" } | Select-Object -First 1
    $featureCheckSummary = $summary | Where-Object { $_.Step -eq "Feature check call" } | Select-Object -First 1
    $tokenFeatureRequests = Get-StepRequestSummary -Runs $Runs -StepName "Token (features.check)"
    $tokenPasswordRequests = Get-StepRequestSummary -Runs $Runs -StepName "Token (password)"

    $metrics = [ordered]@{
        generatedAtUtc = [DateTime]::UtcNow.ToString("o")
        iterations = $TotalIterations
        warmupIterations = $WarmupRunCount
        mode = $Mode
        token = [ordered]@{
            totalRequests = [int]$tokenFeatureRequests.TotalRequests
            successRequests = [int]$tokenFeatureRequests.SuccessRequests
            http429Count = [int]$tokenFeatureRequests.Http429Count
            featureClientCredentials = [ordered]@{
                count = [int]$tokenFeatureSummary.Count
                totalRequests = [int]$tokenFeatureRequests.TotalRequests
                successRequests = [int]$tokenFeatureRequests.SuccessRequests
                failedRequests = [int]$tokenFeatureRequests.FailedRequests
                skippedRequests = [int]$tokenFeatureRequests.SkippedRequests
                http429Count = [int]$tokenFeatureRequests.Http429Count
                minMs = [int]$tokenFeatureSummary.MinMs
                avgMs = [int]$tokenFeatureSummary.AvgMs
                p95Ms = [int]$tokenFeatureSummary.P95Ms
                p99Ms = [int]$tokenFeatureSummary.P99Ms
                maxMs = [int]$tokenFeatureSummary.MaxMs
            }
            password = [ordered]@{
                count = [int]$tokenPasswordSummary.Count
                totalRequests = [int]$tokenPasswordRequests.TotalRequests
                successRequests = [int]$tokenPasswordRequests.SuccessRequests
                failedRequests = [int]$tokenPasswordRequests.FailedRequests
                skippedRequests = [int]$tokenPasswordRequests.SkippedRequests
                http429Count = [int]$tokenPasswordRequests.Http429Count
                minMs = [int]$tokenPasswordSummary.MinMs
                avgMs = [int]$tokenPasswordSummary.AvgMs
                p95Ms = [int]$tokenPasswordSummary.P95Ms
                p99Ms = [int]$tokenPasswordSummary.P99Ms
                maxMs = [int]$tokenPasswordSummary.MaxMs
            }
        }
        featureCheck = [ordered]@{
            count = [int]$featureCheckSummary.Count
            minMs = [int]$featureCheckSummary.MinMs
            avgMs = [int]$featureCheckSummary.AvgMs
            p95Ms = [int]$featureCheckSummary.P95Ms
            p99Ms = [int]$featureCheckSummary.P99Ms
            maxMs = [int]$featureCheckSummary.MaxMs
        }
        authTelemetry = if ($null -ne $AuthTelemetry) { $AuthTelemetry } else { [ordered]@{
                enabled = $false
                status = "disabled"
                tokenRequestCount = 0
                tokenDurationAvgMs = 0
                tokenDurationP95Ms = 0
                tokenDurationP99Ms = 0
                tokenDbQueryAvg = 0
                tokenDbQueryP95 = 0
                tokenRateLimitRejectCount = 0
                tokenStatusCodes = @()
                tokenRejectPartitionHashes = @()
            } }
    }

    $directory = Split-Path -Path $Path -Parent
    if (-not [string]::IsNullOrWhiteSpace($directory) -and -not (Test-Path $directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    $metrics | ConvertTo-Json -Depth 10 | Set-Content -Path $Path -Encoding UTF8
    Write-Host ("Metrics output written: {0}" -f $Path) -ForegroundColor DarkGray
}

if ($Iterations -lt 1) { $Iterations = 1 }
$effectiveWarmupIterations = if ($Benchmark) { [Math]::Max(0, $WarmupIterations) } else { 0 }
$allRuns = New-Object System.Collections.Generic.List[object]
$modeText = if ($Basic) { "Basic" } elseif ($Benchmark) { "Benchmark" } else { "Full" }
$overallFailed = $false
$authTelemetryCapture = Start-AuthTelemetryCapture -Path $AuthServiceLogPath

if (-not $Benchmark -and $WarmupIterations -gt 0) {
    Write-Host "Warmup iterations are only applied in Benchmark mode." -ForegroundColor DarkYellow
}

if ($effectiveWarmupIterations -gt 0) {
    Write-Host ("Running warmup iterations (excluded from measured metrics): {0}" -f $effectiveWarmupIterations) -ForegroundColor DarkCyan
    for ($w = 1; $w -le $effectiveWarmupIterations; $w++) {
        [void](Invoke-SmokeOnce -Iteration $w -Total $effectiveWarmupIterations)
        if ($script:failed) {
            $overallFailed = $true
            Write-Host ("Warmup failed at iteration {0}/{1}." -f $w, $effectiveWarmupIterations) -ForegroundColor Red
            break
        }

        if ($w -lt $effectiveWarmupIterations -and $DelayMs -gt 0) {
            Start-Sleep -Milliseconds $DelayMs
        }
    }
}

if (-not $overallFailed) {
    for ($i = 1; $i -le $Iterations; $i++) {
        $runResults = @(
            Invoke-SmokeOnce -Iteration $i -Total $Iterations
        )
        foreach ($runResult in $runResults) {
            $allRuns.Add($runResult) | Out-Null
        }
        if ($script:failed) {
            $overallFailed = $true
        }

        if ($Iterations -gt 1) {
            $tokenMs = Get-StepDurationOrZero -Runs $runResults -StepName "Token (features.check)"
            $featureMs = Get-StepDurationOrZero -Runs $runResults -StepName "Feature check call"
            $loginMs = Get-StepDurationOrZero -Runs $runResults -StepName "Token (password)"
            Write-Host ("Times: token={0}ms, feature={1}ms, login={2}ms" -f $tokenMs, $featureMs, $loginMs) -ForegroundColor Gray

            if ($script:failed) {
                Write-Host "Smoke test finished with failures." -ForegroundColor Red
                break
            }

            if ($i -lt $Iterations -and $DelayMs -gt 0) {
                Start-Sleep -Milliseconds $DelayMs
            }
            Write-Host ""
        }
    }
}

$allRunsArray = [object[]]($allRuns.ToArray())
$summary = New-TimingSummary -Runs $allRunsArray
$authTelemetrySummary = Get-AuthTelemetrySummary -Capture $authTelemetryCapture

if ($Iterations -eq 1) {
    Write-Host ""
    Write-Host "Smoke Test Summary" -ForegroundColor Cyan
    $allRunsArray | Format-Table -AutoSize
}
else {
    Write-Host "Timing Summary (ms)" -ForegroundColor Cyan
    $summary | Format-Table -AutoSize
}

Write-MetricsOutput `
    -Path $MetricsOutputPath `
    -Runs $allRunsArray `
    -TotalIterations $Iterations `
    -Mode $modeText `
    -WarmupRunCount $effectiveWarmupIterations `
    -AuthTelemetry $authTelemetrySummary

if ($overallFailed) {
    Write-Host "Smoke test finished with failures." -ForegroundColor Red
    exit 1
}

Write-Host "Smoke test passed." -ForegroundColor Green
exit 0
