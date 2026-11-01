[CmdletBinding()]
param(
    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$Namespace = 'tenantwright',

    [Parameter()]
    [ValidateRange(1, 1000)]
    [int]$MaximumPoolSize = 4,

    [Parameter()]
    [ValidateRange(0, 1000)]
    [int]$MinimumPoolSize = 0,

    [Parameter()]
    [ValidateRange(1, 3600)]
    [int]$ConnectionIdleLifetimeSeconds = 60,

    [Parameter()]
    [ValidateRange(1, 3600)]
    [int]$ConnectionPruningIntervalSeconds = 10,

    [Parameter()]
    [ValidateRange(1, 86400)]
    [int]$ConnectionLifetimeSeconds = 300,

    [Parameter()]
    [ValidateRange(1, 300)]
    [int]$ConnectionTimeoutSeconds = 15,

    [Parameter()]
    [ValidateRange(1, 600)]
    [int]$CommandTimeoutSeconds = 30,

    [Parameter()]
    [ValidateSet('Direct', 'TransactionPool', 'SessionPool')]
    [string]$ConnectionMode = 'Direct',

    [Parameter()]
    [ValidateRange(0, 65535)]
    [int]$ExpectedPort = 0,

    [Parameter()]
    [switch]$Apply
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$databaseSecretNames = @(
    'tenantwright-auditlogs-db',
    'tenantwright-auth-db',
    'tenantwright-identity-db',
    'tenantwright-notifications-db',
    'tenantwright-permissions-db',
    'tenantwright-tenant-db',
    'tenantwright-workflows-db'
)

function Invoke-Kubectl {
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments,

        [Parameter()]
        [AllowNull()]
        [string]$StandardInput
    )

    if ($null -eq $StandardInput) {
        $output = & kubectl @Arguments 2>&1
    }
    else {
        $output = $StandardInput | & kubectl @Arguments 2>&1
    }

    if ($LASTEXITCODE -ne 0) {
        throw "kubectl failed: $($output -join [Environment]::NewLine)"
    }

    return $output
}

function Get-ConnectionStringParts {
    param(
        [Parameter(Mandatory)]
        [string]$ConnectionString
    )

    if ($ConnectionString -match '^\s*postgres(?:ql)?://') {
        throw 'URI-form PostgreSQL connection strings are not supported by this operator script. Convert the Secret to Npgsql key/value form first.'
    }

    return @(
        $ConnectionString.Split(';', [StringSplitOptions]::RemoveEmptyEntries) |
            ForEach-Object { $_.Trim() } |
            Where-Object { $_ }
    )
}

function Get-ConnectionSetting {
    param(
        [Parameter(Mandatory)]
        [string[]]$Parts,

        [Parameter(Mandatory)]
        [string[]]$Names
    )

    foreach ($part in $Parts) {
        $separator = $part.IndexOf('=')
        if ($separator -le 0) {
            continue
        }

        $name = $part.Substring(0, $separator).Trim()
        if ($Names -contains $name) {
            return $part.Substring($separator + 1).Trim()
        }
    }

    return $null
}

function Remove-PoolSettings {
    param(
        [Parameter(Mandatory)]
        [string[]]$Parts
    )

    $poolSettingNames = @(
        'Maximum Pool Size',
        'MaxPoolSize',
        'Minimum Pool Size',
        'MinPoolSize',
        'Pooling',
        'Connection Idle Lifetime',
        'Connection Pruning Interval',
        'Connection Lifetime',
        'Timeout',
        'Connection Timeout',
        'Command Timeout'
    )

    return @(
        foreach ($part in $Parts) {
            $separator = $part.IndexOf('=')
            if ($separator -le 0) {
                $part
                continue
            }

            $name = $part.Substring(0, $separator).Trim()
            if ($poolSettingNames -notcontains $name) {
                $part
            }
        }
    )
}

foreach ($secretName in $databaseSecretNames) {
    $secretJson = Invoke-Kubectl -Arguments @(
        'get', 'secret', $secretName,
        '--namespace', $Namespace,
        '--output', 'json'
    )
    $secret = ($secretJson -join [Environment]::NewLine) | ConvertFrom-Json
    $encodedConnectionString = $secret.data.'connection-string'

    if ([string]::IsNullOrWhiteSpace($encodedConnectionString)) {
        throw "Secret '$secretName' is missing data key 'connection-string'."
    }

    $connectionString = [Text.Encoding]::UTF8.GetString(
        [Convert]::FromBase64String($encodedConnectionString))
    $parts = Get-ConnectionStringParts -ConnectionString $connectionString
    $actualPort = Get-ConnectionSetting -Parts $parts -Names @('Port')
    if ($ExpectedPort -gt 0 -and $actualPort -ne [string]$ExpectedPort) {
        throw "Secret '$secretName' does not use the reviewed PostgreSQL endpoint port for $ConnectionMode mode."
    }

    if (-not $Apply) {
        $actualPooling = Get-ConnectionSetting -Parts $parts -Names @('Pooling')
        $actualMaximum = Get-ConnectionSetting -Parts $parts -Names @('Maximum Pool Size', 'MaxPoolSize')
        $actualMinimum = Get-ConnectionSetting -Parts $parts -Names @('Minimum Pool Size', 'MinPoolSize')
        $actualIdle = Get-ConnectionSetting -Parts $parts -Names @('Connection Idle Lifetime')
        $actualPruning = Get-ConnectionSetting -Parts $parts -Names @('Connection Pruning Interval')
        $actualLifetime = Get-ConnectionSetting -Parts $parts -Names @('Connection Lifetime')
        $actualTimeout = Get-ConnectionSetting -Parts $parts -Names @('Timeout', 'Connection Timeout')
        $actualCommandTimeout = Get-ConnectionSetting -Parts $parts -Names @('Command Timeout')

        if (-not [string]::Equals($actualPooling, 'true', [StringComparison]::OrdinalIgnoreCase) -or
            $actualMaximum -ne [string]$MaximumPoolSize -or
            $actualMinimum -ne [string]$MinimumPoolSize -or
            $actualIdle -ne [string]$ConnectionIdleLifetimeSeconds -or
            $actualPruning -ne [string]$ConnectionPruningIntervalSeconds -or
            $actualLifetime -ne [string]$ConnectionLifetimeSeconds -or
            $actualTimeout -ne [string]$ConnectionTimeoutSeconds -or
            $actualCommandTimeout -ne [string]$CommandTimeoutSeconds) {
            throw "Secret '$secretName' does not match the reviewed PostgreSQL pool budget."
        }

        Write-Output "${secretName}: PostgreSQL $ConnectionMode client-pool budget verified."
        continue
    }

    $retainedParts = Remove-PoolSettings -Parts $parts
    $boundedConnectionString = @(
        $retainedParts
        'Pooling=true'
        "Maximum Pool Size=$MaximumPoolSize"
        "Minimum Pool Size=$MinimumPoolSize"
        "Connection Idle Lifetime=$ConnectionIdleLifetimeSeconds"
        "Connection Pruning Interval=$ConnectionPruningIntervalSeconds"
        "Connection Lifetime=$ConnectionLifetimeSeconds"
        "Timeout=$ConnectionTimeoutSeconds"
        "Command Timeout=$CommandTimeoutSeconds"
    ) -join ';'

    $secret.data.'connection-string' = [Convert]::ToBase64String(
        [Text.Encoding]::UTF8.GetBytes($boundedConnectionString))
    $replacementJson = $secret | ConvertTo-Json -Depth 100 -Compress

    Invoke-Kubectl -Arguments @('replace', '--filename', '-') -StandardInput $replacementJson | Out-Null
    Write-Output "${secretName}: PostgreSQL $ConnectionMode client-pool budget applied."

    $connectionString = $null
    $boundedConnectionString = $null
    $encodedConnectionString = $null
    $replacementJson = $null
}

Write-Output 'PostgreSQL connection strings were not printed.'
