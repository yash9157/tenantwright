[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^https?://')]
    [string] $GatewayBaseUrl,

    [ValidateRange(1024, 65536)]
    [int] $AuthorizationFieldBudgetBytes = 8192,

    [switch] $SkipCertificateCheck
)

$ErrorActionPreference = 'Stop'

$handler = [System.Net.Http.HttpClientHandler]::new()
if ($SkipCertificateCheck) {
    $handler.ServerCertificateCustomValidationCallback = {
        param($requestMessage, $certificate, $chain, $sslPolicyErrors)
        return $true
    }
}

$client = [System.Net.Http.HttpClient]::new($handler)
$client.Timeout = [TimeSpan]::FromSeconds(30)

$headerEnvelopeBytes = [System.Text.Encoding]::UTF8.GetByteCount("Authorization: `r`n")
$bearerPrefix = 'Bearer '
$acceptedValueBytes = $AuthorizationFieldBudgetBytes - $headerEnvelopeBytes
if ($acceptedValueBytes -le $bearerPrefix.Length) {
    throw 'Authorization field budget is too small to construct the verification request.'
}

$endpoint = "{0}/g3/permission-evaluation/me" -f $GatewayBaseUrl.TrimEnd('/')

function Invoke-BudgetProbe {
    param(
        [Parameter(Mandatory = $true)]
        [int] $ValueBytes
    )

    $request = [System.Net.Http.HttpRequestMessage]::new(
        [System.Net.Http.HttpMethod]::Get,
        $endpoint)
    $authorizationValue = $bearerPrefix + ('a' * ($ValueBytes - $bearerPrefix.Length))
    if (-not $request.Headers.TryAddWithoutValidation('Authorization', $authorizationValue)) {
        throw 'The HTTP client refused the Authorization test field.'
    }

    try {
        $response = $client.Send($request)
        return [int] $response.StatusCode
    }
    finally {
        $request.Dispose()
        if ($null -ne $response) {
            $response.Dispose()
        }
    }
}

try {
    $acceptedStatus = Invoke-BudgetProbe -ValueBytes $acceptedValueBytes
    if ($acceptedStatus -ne 401) {
        throw "Expected an exactly $AuthorizationFieldBudgetBytes-byte Authorization field to reach gateway authentication and return 401; received HTTP $acceptedStatus. The ingress/load-balancer path does not prove the approved budget."
    }

    $rejectedStatus = Invoke-BudgetProbe -ValueBytes ($acceptedValueBytes + 1)
    if ($rejectedStatus -ne 431) {
        throw "Expected an Authorization field one byte over budget to return 431; received HTTP $rejectedStatus. The deployed path is not enforcing the gateway contract."
    }

    Write-Output "PASS: external gateway path accepts exactly $AuthorizationFieldBudgetBytes bytes (401 from auth) and rejects $($AuthorizationFieldBudgetBytes + 1) bytes (431)."
}
finally {
    $client.Dispose()
    $handler.Dispose()
}
