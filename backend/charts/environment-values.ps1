function Merge-EnvironmentValues {
    param([Parameter(Mandatory)][System.Collections.IDictionary]$Base,
          [Parameter(Mandatory)][System.Collections.IDictionary]$Overlay)

    $merged = @{}
    foreach ($key in $Base.Keys) { $merged[$key] = $Base[$key] }
    foreach ($key in $Overlay.Keys) {
        if ($merged[$key] -is [System.Collections.IDictionary] -and
            $Overlay[$key] -is [System.Collections.IDictionary]) {
            $merged[$key] = Merge-EnvironmentValues -Base $merged[$key] -Overlay $Overlay[$key]
        }
        else { $merged[$key] = $Overlay[$key] }
    }
    return $merged
}

function Get-EnvironmentValuesFiles {
    param([Parameter(Mandatory)][string]$EnvironmentRoot,
          [Parameter(Mandatory)][string]$ReleaseName,
          [switch]$Platform,
          [switch]$EnableMessaging)

    if ($ReleaseName -notmatch '^tenantwright-[a-z][a-z-]*$') {
        throw 'Invalid TenantWright release name.'
    }
    $component = $ReleaseName.Substring('tenantwright-'.Length)
    $group = if ($Platform) { 'platform' } else { 'backend' }
    $files = @()
    if (-not $Platform -and $component -ne 'gateway') {
        $common = Join-Path $EnvironmentRoot 'backend/common.values.yaml'
        if (Test-Path -LiteralPath $common -PathType Leaf) { $files += $common }
        if ($EnableMessaging) {
            $files += Join-Path $PSScriptRoot 'tenantwright-common/files/messaging.values.yaml'
            $messaging = Join-Path $EnvironmentRoot 'backend/messaging.values.yaml'
            if (Test-Path -LiteralPath $messaging -PathType Leaf) { $files += $messaging }
        }
    }
    $files += Join-Path $EnvironmentRoot "$group/$component.values.yaml"
    $private = Join-Path $EnvironmentRoot "private/$group/$component.values.local.yaml"
    if (Test-Path -LiteralPath $private -PathType Leaf) { $files += $private }
    foreach ($file in $files) {
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
            throw "Environment values file is missing for ${ReleaseName}: $file"
        }
    }
    return $files
}

function Read-EnvironmentValues {
    param([Parameter(Mandatory)][string[]]$Paths)

    $values = @{}
    foreach ($path in $Paths) {
        $text = Get-Content -LiteralPath $path -Raw
        if ($text -match '(?i)(example\.invalid|changeme|<[^>]+>)') {
            throw "Environment values contain a placeholder: $path"
        }
        try { $overlay = ConvertFrom-Yaml -Yaml $text }
        catch { throw "Environment values are not valid YAML: $path" }
        if ($overlay -isnot [System.Collections.IDictionary]) {
            throw "Environment values must contain a mapping: $path"
        }
        $values = Merge-EnvironmentValues -Base $values -Overlay $overlay
    }
    return $values
}
