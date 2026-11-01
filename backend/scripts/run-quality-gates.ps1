param(
    [switch]$RunTests = $true,
    [switch]$RunSmoke = $false,
    [switch]$VerifyEtoPlacement = $true,
    [string]$SmokeScriptPath = "scripts/run-auth-token-permission-smoke.ps1",
    [string]$EtoPlacementScriptPath = "scripts/verify-eto-placement.ps1",
    [string]$PerfReportPath = "",
    [bool]$RequirePerfReport = $true,
    [int]$TokenP95BudgetMs = 120,
    [int]$TokenP99BudgetMs = 250,
    [int]$TokenHttp429Budget = 0,
    [int]$TokenColdDbQueryBudget = 3,
    [int]$TokenWarmDbQueryBudget = 1,
    [int]$SessionValkeyReadBudget = 1,
    [int]$SessionDbReadBudget = 0,
    [double]$ContainerBuildRegressionBudgetPct = 10,
    [double]$ScopedResolveAllocRegressionBudgetPct = 10,
    [double]$AuthErrorRateBudgetPct = 0.5,
    [double]$P99LatencyRegressionBudgetPct = 20,
    [double]$CacheMissAmplificationBudget = 2,
    [int]$NotificationApiPathProviderCallBudget = 0,
    [int]$NotificationDeliveryP95BudgetMs = 80,
    [int]$NotificationTemplateRenderP95BudgetMs = 20,
    [int]$NotificationQueueLagP95BudgetMs = 30000,
    [double]$NotificationDuplicateScheduleRatioBudgetPct = 5,
    [double]$AuditProducerOverheadP95BudgetMs = 5,
    [double]$AuditIngestionP95BudgetMs = 60,
    [double]$AuditQueueLagP95BudgetMs = 10000,
    [double]$AuditListQueryP95BudgetMs = 120,
    [double]$AuditEntityChangeQueryP95BudgetMs = 150,
    [double]$AuditDroppedEventsRatioBudgetPct = 0.1,
    [double]$AuditExportGenerationP95BudgetMs = 800,
    [double]$AuditExportQueueLagP95BudgetMs = 30000,
    [double]$AuditExportDownloadP95BudgetMs = 500,
    [double]$AuditExportFailureRatioBudgetPct = 1,
    [double]$AuditArchivePartitionProcessP95BudgetMs = 2000,
    [double]$AuditArchiveUploadP95BudgetMs = 3000,
    [double]$AuditArchiveDeleteP95BudgetMs = 3000,
    [double]$AuditArchiveRestoreP95BudgetMs = 5000,
    [double]$AuditArchiveFailureRatioBudgetPct = 2
)

$ErrorActionPreference = "Stop"

function Invoke-Step {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][scriptblock]$Action
    )

    Write-Host ""
    Write-Host "==> $Name" -ForegroundColor Cyan
    & $Action
}

function Assert-LastExitCode {
    param([string]$StepName)
    if ($LASTEXITCODE -ne 0) {
        throw "$StepName failed with exit code $LASTEXITCODE."
    }
}

function Assert-Lte {
    param(
        [string]$Name,
        [double]$Actual,
        [double]$Budget
    )
    Write-Host "$($Name): $Actual (budget: <= $Budget)"
    if ($Actual -gt $Budget) {
        throw "$Name budget failed: $Actual > $Budget"
    }
}

function Assert-Gt {
    param(
        [string]$Name,
        [double]$Actual,
        [double]$MinimumExclusive
    )
    Write-Host "$($Name): $Actual (required: > $MinimumExclusive)"
    if ($Actual -le $MinimumExclusive) {
        throw "$Name requirement failed: $Actual <= $MinimumExclusive"
    }
}

function Assert-Gte {
    param(
        [string]$Name,
        [double]$Actual,
        [double]$MinimumInclusive
    )
    Write-Host "$($Name): $Actual (required: >= $MinimumInclusive)"
    if ($Actual -lt $MinimumInclusive) {
        throw "$Name requirement failed: $Actual < $MinimumInclusive"
    }
}

Invoke-Step -Name "Build full solution" -Action {
    dotnet build TenantWright.slnx --nologo
    Assert-LastExitCode -StepName "dotnet build TenantWright.slnx"
}

if ($VerifyEtoPlacement) {
    if (-not (Test-Path $EtoPlacementScriptPath)) {
        throw "ETO placement script not found: $EtoPlacementScriptPath"
    }

    Invoke-Step -Name "Verify ETO placement" -Action {
        & powershell -NoProfile -ExecutionPolicy Bypass -File $EtoPlacementScriptPath
        Assert-LastExitCode -StepName "verify-eto-placement"
    }
}

if ($RunTests) {
    $testProjects = @(
        git ls-files "tests/**/*.csproj" |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
            Sort-Object
    )
    Assert-LastExitCode -StepName "discover tracked test projects"

    if ($testProjects.Count -eq 0) {
        throw "No tracked test projects were discovered under tests/."
    }

    [xml]$solution = Get-Content -LiteralPath "TenantWright.slnx" -Raw
    $solutionProjects = @(
        $solution.SelectNodes("//Project") |
            ForEach-Object { $_.Path.Replace('\\', '/') }
    )
    $missingFromSolution = @($testProjects | Where-Object { $_ -notin $solutionProjects })
    if ($missingFromSolution.Count -gt 0) {
        throw "Tracked test projects missing from TenantWright.slnx: $($missingFromSolution -join ', ')"
    }

    foreach ($project in $testProjects) {
        if (-not (Test-Path -LiteralPath $project)) {
            throw "Tracked test project is missing from disk: $project"
        }

        Invoke-Step -Name "Test $project" -Action {
            dotnet test $project --nologo
            Assert-LastExitCode -StepName "dotnet test $project"
        }
    }
}

if ($RunSmoke) {
    if (-not (Test-Path $SmokeScriptPath)) {
        throw "Smoke script not found: $SmokeScriptPath"
    }

    Invoke-Step -Name "Smoke script" -Action {
        & powershell -NoProfile -ExecutionPolicy Bypass -File $SmokeScriptPath
        Assert-LastExitCode -StepName "smoke script"
    }
}

if (-not [string]::IsNullOrWhiteSpace($PerfReportPath)) {
    if (-not (Test-Path $PerfReportPath)) {
        throw "Performance report not found: $PerfReportPath"
    }

    $report = Get-Content -Path $PerfReportPath -Raw | ConvertFrom-Json

    Assert-Lte -Name "token.p95Ms" -Actual ([double]$report.token.p95Ms) -Budget $TokenP95BudgetMs
    Assert-Lte -Name "token.p99Ms" -Actual ([double]$report.token.p99Ms) -Budget $TokenP99BudgetMs
    if ($report.token.PSObject.Properties.Name -contains "totalRequests") {
        Assert-Gt -Name "token.totalRequests" -Actual ([double]$report.token.totalRequests) -MinimumExclusive 0
    }
    else {
        throw "token.totalRequests is required in performance report."
    }
    if ($report.token.PSObject.Properties.Name -contains "successRequests") {
        Assert-Gt -Name "token.successRequests" -Actual ([double]$report.token.successRequests) -MinimumExclusive 0
    }
    else {
        throw "token.successRequests is required in performance report."
    }
    if ($report.token.PSObject.Properties.Name -contains "http429Count") {
        Assert-Lte -Name "token.http429Count" -Actual ([double]$report.token.http429Count) -Budget $TokenHttp429Budget
    }
    else {
        throw "token.http429Count is required in performance report."
    }
    Assert-Lte -Name "token.dbQueriesColdPath" -Actual ([double]$report.token.dbQueriesColdPath) -Budget $TokenColdDbQueryBudget
    Assert-Lte -Name "token.dbQueriesWarmPath" -Actual ([double]$report.token.dbQueriesWarmPath) -Budget $TokenWarmDbQueryBudget
    Assert-Lte -Name "session.valkeyReadsPerRequest" -Actual ([double]$report.session.valkeyReadsPerRequest) -Budget $SessionValkeyReadBudget
    Assert-Lte -Name "session.dbReadsPerRequest" -Actual ([double]$report.session.dbReadsPerRequest) -Budget $SessionDbReadBudget
    Assert-Lte -Name "container.buildP95RegressionPct" -Actual ([double]$report.container.buildP95RegressionPct) -Budget $ContainerBuildRegressionBudgetPct
    Assert-Lte -Name "container.scopedResolveAllocRegressionPct" -Actual ([double]$report.container.scopedResolveAllocRegressionPct) -Budget $ScopedResolveAllocRegressionBudgetPct
    Assert-Lte -Name "rollout.authErrorRatePct" -Actual ([double]$report.rollout.authErrorRatePct) -Budget $AuthErrorRateBudgetPct
    Assert-Lte -Name "rollout.p99LatencyRegressionPct" -Actual ([double]$report.rollout.p99LatencyRegressionPct) -Budget $P99LatencyRegressionBudgetPct
    Assert-Lte -Name "rollout.cacheMissAmplificationFactor" -Actual ([double]$report.rollout.cacheMissAmplificationFactor) -Budget $CacheMissAmplificationBudget

    if (-not ($report.PSObject.Properties.Name -contains "notifications")) {
        throw "notifications section is required in performance report."
    }

    Assert-Lte -Name "notifications.apiPathProviderCalls" -Actual ([double]$report.notifications.apiPathProviderCalls) -Budget $NotificationApiPathProviderCallBudget
    Assert-Lte -Name "notifications.deliveryP95Ms" -Actual ([double]$report.notifications.deliveryP95Ms) -Budget $NotificationDeliveryP95BudgetMs
    Assert-Lte -Name "notifications.templateRenderP95Ms" -Actual ([double]$report.notifications.templateRenderP95Ms) -Budget $NotificationTemplateRenderP95BudgetMs
    Assert-Lte -Name "notifications.queueLagP95Ms" -Actual ([double]$report.notifications.queueLagP95Ms) -Budget $NotificationQueueLagP95BudgetMs
    Assert-Lte -Name "notifications.duplicateScheduleRatioPct" -Actual ([double]$report.notifications.duplicateScheduleRatioPct) -Budget $NotificationDuplicateScheduleRatioBudgetPct

    if (-not ($report.notifications.PSObject.Properties.Name -contains "emailSendP95Ms")) {
        throw "notifications.emailSendP95Ms is required in performance report."
    }

    Assert-Gte -Name "notifications.emailSendP95Ms" -Actual ([double]$report.notifications.emailSendP95Ms) -MinimumInclusive 0

    if (-not ($report.PSObject.Properties.Name -contains "audit")) {
        throw "audit section is required in performance report."
    }

    Assert-Lte -Name "audit.producerOverheadP95Ms" -Actual ([double]$report.audit.producerOverheadP95Ms) -Budget $AuditProducerOverheadP95BudgetMs
    Assert-Lte -Name "audit.ingestionP95Ms" -Actual ([double]$report.audit.ingestionP95Ms) -Budget $AuditIngestionP95BudgetMs
    Assert-Lte -Name "audit.queueLagP95Ms" -Actual ([double]$report.audit.queueLagP95Ms) -Budget $AuditQueueLagP95BudgetMs
    Assert-Lte -Name "audit.listQueryP95Ms" -Actual ([double]$report.audit.listQueryP95Ms) -Budget $AuditListQueryP95BudgetMs
    Assert-Lte -Name "audit.entityChangeQueryP95Ms" -Actual ([double]$report.audit.entityChangeQueryP95Ms) -Budget $AuditEntityChangeQueryP95BudgetMs
    Assert-Lte -Name "audit.droppedEventsRatioPct" -Actual ([double]$report.audit.droppedEventsRatioPct) -Budget $AuditDroppedEventsRatioBudgetPct
    Assert-Lte -Name "audit.exportGenerationP95Ms" -Actual ([double]$report.audit.exportGenerationP95Ms) -Budget $AuditExportGenerationP95BudgetMs
    Assert-Lte -Name "audit.exportQueueLagP95Ms" -Actual ([double]$report.audit.exportQueueLagP95Ms) -Budget $AuditExportQueueLagP95BudgetMs
    Assert-Lte -Name "audit.exportDownloadP95Ms" -Actual ([double]$report.audit.exportDownloadP95Ms) -Budget $AuditExportDownloadP95BudgetMs
    Assert-Lte -Name "audit.exportFailureRatioPct" -Actual ([double]$report.audit.exportFailureRatioPct) -Budget $AuditExportFailureRatioBudgetPct

    if ($report.audit.PSObject.Properties.Name -contains "archivePartitionProcessP95Ms") {
        Assert-Lte -Name "audit.archivePartitionProcessP95Ms" -Actual ([double]$report.audit.archivePartitionProcessP95Ms) -Budget $AuditArchivePartitionProcessP95BudgetMs
    }
    if ($report.audit.PSObject.Properties.Name -contains "archiveUploadP95Ms") {
        Assert-Lte -Name "audit.archiveUploadP95Ms" -Actual ([double]$report.audit.archiveUploadP95Ms) -Budget $AuditArchiveUploadP95BudgetMs
    }
    if ($report.audit.PSObject.Properties.Name -contains "archiveDeleteP95Ms") {
        Assert-Lte -Name "audit.archiveDeleteP95Ms" -Actual ([double]$report.audit.archiveDeleteP95Ms) -Budget $AuditArchiveDeleteP95BudgetMs
    }
    if ($report.audit.PSObject.Properties.Name -contains "archiveRestoreP95Ms") {
        Assert-Lte -Name "audit.archiveRestoreP95Ms" -Actual ([double]$report.audit.archiveRestoreP95Ms) -Budget $AuditArchiveRestoreP95BudgetMs
    }
    if ($report.audit.PSObject.Properties.Name -contains "archiveFailureRatioPct") {
        Assert-Lte -Name "audit.archiveFailureRatioPct" -Actual ([double]$report.audit.archiveFailureRatioPct) -Budget $AuditArchiveFailureRatioBudgetPct
    }
}
else {
    if ($RequirePerfReport) {
        throw "PerfReportPath is required. Generate a hyperscale performance report from a load-test run and pass -PerfReportPath."
    }
    Write-Warning "No PerfReportPath supplied. Hyperscale budget validation was skipped because RequirePerfReport=false."
}

Write-Host ""
Write-Host "Quality gates passed." -ForegroundColor Green
