[CmdletBinding()]
param(
    [string] $DockerPath = 'docker',
    [string] $PostgreSqlImage = 'postgres:17.5-alpine'
)

$ErrorActionPreference = 'Stop'
$containerName = "tenantwright-postgresql-restore-$([Guid]::NewGuid().ToString('N'))"
$databaseUser = 'tenantwright_restore_verifier'
$passwordBytes = [byte[]]::new(24)
$random = [Security.Cryptography.RandomNumberGenerator]::Create()
try {
    $random.GetBytes($passwordBytes)
}
finally {
    $random.Dispose()
}
$databasePassword = -join ($passwordBytes | ForEach-Object { $_.ToString('x2') })

function Invoke-Docker {
    param([Parameter(Mandatory)][string[]] $Arguments)

    $output = (& $DockerPath @Arguments 2>&1) -join [Environment]::NewLine
    if ($LASTEXITCODE -ne 0) {
        throw "PostgreSQL backup/restore Docker step failed without exposing command output."
    }
    return $output
}

function Invoke-Psql {
    param(
        [Parameter(Mandatory)][string] $Database,
        [Parameter(Mandatory)][string] $Sql
    )

    return Invoke-Docker @(
        'exec', '-e', "PGPASSWORD=$databasePassword", $containerName,
        'psql', '--no-psqlrc', '--set', 'ON_ERROR_STOP=1', '--tuples-only', '--no-align',
        '--username', $databaseUser, '--dbname', $Database, '--command', $Sql)
}

function Get-Checksum {
    param([Parameter(Mandatory)][string] $Value)

    $bytes = [Text.Encoding]::UTF8.GetBytes($Value.Replace("`r`n", "`n").Trim())
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        return -join ($algorithm.ComputeHash($bytes) | ForEach-Object { $_.ToString('x2') })
    }
    finally {
        $algorithm.Dispose()
    }
}

try {
    $null = Invoke-Docker @(
        'run', '--detach', '--name', $containerName,
        '--env', "POSTGRES_USER=$databaseUser",
        '--env', "POSTGRES_PASSWORD=$databasePassword",
        '--env', 'POSTGRES_DB=source_tenantwright',
        '--health-cmd', "pg_isready -U $databaseUser -d source_tenantwright",
        '--health-interval', '2s', '--health-timeout', '5s', '--health-retries', '30',
        $PostgreSqlImage)

    $healthy = $false
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        $status = Invoke-Docker @('inspect', '--format={{if .State.Health}}{{.State.Health.Status}}{{end}}', $containerName)
        if ($status.Trim() -eq 'healthy') {
            $healthy = $true
            break
        }
        Start-Sleep -Milliseconds 500
    }
    if (-not $healthy) {
        throw 'The isolated PostgreSQL verifier did not become healthy within 30 seconds.'
    }

    $null = Invoke-Psql -Database 'source_tenantwright' -Sql @'
CREATE SCHEMA restore_verification;
CREATE TABLE restore_verification.restore_evidence (
    tenant_id uuid NOT NULL,
    evidence_id uuid NOT NULL,
    revision bigint NOT NULL CHECK (revision > 0),
    payload jsonb NOT NULL,
    recorded_at_utc timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, evidence_id)
);
CREATE INDEX ix_restore_evidence_tenant_revision
    ON restore_verification.restore_evidence (tenant_id, revision);
INSERT INTO restore_verification.restore_evidence VALUES
('11111111-1111-1111-1111-111111111111','aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa1',1,jsonb_build_object('state','active','items',jsonb_build_array(1,2)),'2026-08-29T00:00:00Z'),
('11111111-1111-1111-1111-111111111111','aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa2',2,jsonb_build_object('state','reconciled','items',jsonb_build_array()),'2026-08-29T00:01:00Z'),
('22222222-2222-2222-2222-222222222222','bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbb1',7,jsonb_build_object('state','held','reason','policy'),'2026-08-29T00:02:00Z');
'@
    $canonicalSql = @'
SELECT tenant_id::text || '|' || evidence_id::text || '|' || revision::text || '|' || payload::text || '|' ||
       to_char(recorded_at_utc AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US')
FROM restore_verification.restore_evidence
ORDER BY tenant_id, evidence_id;
'@
    $sourceRows = Invoke-Psql -Database 'source_tenantwright' -Sql $canonicalSql
    $sourceSchema = Invoke-Psql -Database 'source_tenantwright' -Sql @'
SELECT table_schema || '|' || table_name || '|' || column_name || '|' || data_type || '|' || is_nullable
FROM information_schema.columns
WHERE table_schema = 'restore_verification'
ORDER BY table_name, ordinal_position;
'@
    $sourceChecksum = Get-Checksum "$sourceSchema`n$sourceRows"

    $null = Invoke-Docker @(
        'exec', '-e', "PGPASSWORD=$databasePassword", $containerName,
        'pg_dump', '--username', $databaseUser, '--dbname', 'source_tenantwright',
        '--format', 'custom', '--file', '/tmp/tenantwright-restore-verification.dump')
    $null = Invoke-Docker @('restart', $containerName)
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        $status = Invoke-Docker @('inspect', '--format={{if .State.Health}}{{.State.Health.Status}}{{end}}', $containerName)
        if ($status.Trim() -eq 'healthy') { break }
        Start-Sleep -Milliseconds 500
    }
    $null = Invoke-Psql -Database 'source_tenantwright' -Sql 'CREATE DATABASE restored_tenantwright;'
    $null = Invoke-Docker @(
        'exec', '-e', "PGPASSWORD=$databasePassword", $containerName,
        'pg_restore', '--exit-on-error', '--no-owner', '--no-privileges',
        '--username', $databaseUser, '--dbname', 'restored_tenantwright',
        '/tmp/tenantwright-restore-verification.dump')

    $restoredRows = Invoke-Psql -Database 'restored_tenantwright' -Sql $canonicalSql
    $restoredSchema = Invoke-Psql -Database 'restored_tenantwright' -Sql @'
SELECT table_schema || '|' || table_name || '|' || column_name || '|' || data_type || '|' || is_nullable
FROM information_schema.columns
WHERE table_schema = 'restore_verification'
ORDER BY table_name, ordinal_position;
'@
    $restoredChecksum = Get-Checksum "$restoredSchema`n$restoredRows"
    if ($sourceChecksum -ne $restoredChecksum) {
        throw 'PostgreSQL restore checksum reconciliation failed.'
    }

    Write-Host "PostgreSQL backup/restore checksum reconciliation passed ($sourceChecksum)."
}
finally {
    & $DockerPath rm --force --volumes $containerName *> $null
}
