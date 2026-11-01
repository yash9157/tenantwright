#Requires -Version 7.0
[CmdletBinding()]
param(
    [string] $PostgresHost = 'localhost',
    [int] $PostgresPort = 5432,
    [string] $PostgresUser = 'postgres',
    [string] $PostgresPassword = '1234'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$sqlScript = Join-Path $PSScriptRoot 'reset-local-databases.sql'

$psqlPath = if (Get-Command psql -ErrorAction SilentlyContinue) {
    'psql'
}
elseif (Test-Path 'C:\Program Files\PostgreSQL\18\bin\psql.exe') {
    'C:\Program Files\PostgreSQL\18\bin\psql.exe'
}
else {
    $found = Get-ChildItem -Path 'C:\Program Files\PostgreSQL' -Filter psql.exe -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName
    if ($found) { $found } else { $null }
}

if (-not $psqlPath) {
    throw "psql executable was not found. Please run 'scripts/reset-local-databases.sql' in pgAdmin or DBeaver."
}

Write-Output "Executing database reset via $psqlPath..."
$env:PGPASSWORD = $PostgresPassword
& $psqlPath -h $PostgresHost -p $PostgresPort -U $PostgresUser -d postgres -f $sqlScript

Write-Output "All 7 template development databases have been recreated successfully."
Write-Output "Now run TenantWright.AppHost; each service auto-migrates and seeds its own database."
