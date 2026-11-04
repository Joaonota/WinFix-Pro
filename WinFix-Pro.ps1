#requires -Version 5.1
# WinFix Pro 0.2 - Windows 10/11, Windows PowerShell 5.1.

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
foreach ($module in @(
    'Core\HealthScore.psm1','Core\SystemInfo.psm1','Core\Logging.psm1',
    'Core\DiagnosticsEngine.psm1','Core\NetworkAnalyzer.psm1','Core\ServiceManager.psm1',
    'Core\EventAnalyzer.psm1','Core\SecurityAnalyzer.psm1','Core\PerformanceAnalyzer.psm1',
    'Core\Reports.psm1','Core\KnowledgeEngine.psm1','Core\UniversalDiagnostics.psm1',
    'Core\UniversalCollector.psm1','Repairs\RepairEngine.psm1'
)) {
    Import-Module (Join-Path $Root $module) -Force -ErrorAction Stop
}

Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
[xml]$xaml = Get-Content -LiteralPath (Join-Path $Root 'UI\MainWindow.xaml') -Raw
$reader = New-Object System.Xml.XmlNodeReader $xaml
$window = [Windows.Markup.XamlReader]::Load($reader)
$script:Controls = @{}
@('txtComputer','txtPrivilege','txtHealth','txtHealthRating','txtStatus','txtOutput','txtCpu','txtRam','txtDisk','txtUptime','txtReboot',
  'btnScan','btnNetwork','btnHardware','btnPerformance','btnServices','btnSecurity',
  'btnEvents','btnRepair','btnExport','cmbRepair','cmbFormat','chkSimulation',
  'tabs','txtVersion','txtSymptoms','btnUniversal','chkPlugins','txtCaseOutcome','txtCaseTests',
  'txtCaseSources','txtReviewNotes','cmbCaseStatus','btnRecordCase','btnPromoteCase','btnApproveCase','btnImportKnowledge','txtCaseId') |
  ForEach-Object {$script:Controls[$_] = $window.FindName($_)}
$script:LastDiagnostic = $null
$script:LastUniversal = $null
$script:LastCaseId = $null
$script:LastCandidateId = $null
$script:DataRoot = Join-Path $env:LOCALAPPDATA 'WinFixPro\0.2'
$script:BusyControls = @('btnScan','btnNetwork','btnHardware','btnPerformance','btnServices','btnSecurity','btnEvents','btnRepair','btnExport','btnUniversal')
$script:AsyncPowerShell = $null
$script:AsyncHandle = $null
$script:AsyncTimer = $null

function Add-UiMessage {
    param([string]$Message,[string]$Level='INFO')
    $stamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
    $Controls.txtOutput.AppendText("[$stamp] [$Level] $Message`r`n")
    $Controls.txtOutput.ScrollToEnd()
    try {Write-WinFixLog -Message $Message -Level $Level -LogDirectory (Join-Path $Root 'Logs')}
    catch {$Controls.txtOutput.AppendText("[ERROR] Não foi possível escrever o log: $($_.Exception.Message)`r`n")}
}

function Set-UiBusy {
    param([bool]$Busy,[string]$Text)
    foreach ($name in $script:BusyControls) {$Controls[$name].IsEnabled = -not $Busy}
    $Controls.txtStatus.Text = $Text
    $window.Cursor = if ($Busy) {[System.Windows.Input.Cursors]::Wait} else {[System.Windows.Input.Cursors]::Arrow}
}

function Start-BackgroundOperation {
    param([Parameter(Mandatory)][string]$Operation,[string]$RepairId,[string[]]$Symptoms=@(),[bool]$AllowPlugins=$false)
    if ($script:AsyncHandle) {return}
    $script:ActiveOperation = $Operation
    $script:ActiveRepairId = $RepairId
    Set-UiBusy $true "A executar: $Operation..."
    $workerScript = @'
param($Root,$Operation,$RepairId,$Simulation,$Symptoms,$AllowPlugins)
$ErrorActionPreference = 'Stop'
foreach ($module in @(
  'Core\HealthScore.psm1','Core\SystemInfo.psm1','Core\DiagnosticsEngine.psm1',
  'Core\NetworkAnalyzer.psm1','Core\ServiceManager.psm1','Core\EventAnalyzer.psm1',
  'Core\SecurityAnalyzer.psm1',  'Core\PerformanceAnalyzer.psm1','Core\KnowledgeEngine.psm1',
  'Core\UniversalDiagnostics.psm1','Core\UniversalCollector.psm1','Repairs\RepairEngine.psm1'
)) { Import-Module (Join-Path $Root $module) -Force -ErrorAction Stop }
switch ($Operation) {
  'Diagnostics' { Invoke-WinFixDiagnostics }
  'Network' { @(Get-WinFixNetworkDiagnostic) }
  'Hardware' { Get-WinFixHardwareInventory }
  'Performance' { @(Get-WinFixTopProcesses -Count 15) }
  'Services' { @(Get-WinFixServiceInventory) }
  'Security' { @(Get-WinFixSecurityStatus) }
  'Events' { @(Get-WinFixRecentEvents -LogName 'System' -Hours 24 -Maximum 100) }
  'Universal' {
    $context = Get-WinFixUniversalEvidence -Root $Root
    $analysis = Invoke-WinFixUniversalAnalysis -Context $context `
      -RulesPath (Join-Path $Root 'Rules\Issues.json') `
      -KnowledgePath (Join-Path $env:LOCALAPPDATA 'WinFixPro\0.2\Knowledge\knowledge.json') `
      -PluginDirectory (Join-Path $Root 'Plugins') -Symptoms $Symptoms -AllowPlugins:$AllowPlugins `
      -Baseline $context.Baseline
    [pscustomobject]@{Context=$context;Analysis=$analysis}
  }
  'Repair' {
    $baseline = Get-WinFixSystemSummary
    $repairResult = Invoke-WinFixRepair -RepairId $RepairId -Confirmed -Simulation:$Simulation
    $repairResult | Add-Member -NotePropertyName Baseline -NotePropertyValue $baseline
    $repairResult
  }
}
'@
    $runspace = [runspacefactory]::CreateRunspace()
    $runspace.Open()
    $powershell = [powershell]::Create()
    $powershell.Runspace = $runspace
    [void]$powershell.AddScript($workerScript).AddArgument($Root).AddArgument($Operation).AddArgument($RepairId).AddArgument([bool]$Controls.chkSimulation.IsChecked).AddArgument([string[]]$Symptoms).AddArgument($AllowPlugins)
    $script:AsyncPowerShell = $powershell
    $script:AsyncHandle = $powershell.BeginInvoke()
    $script:AsyncTimer = New-Object Windows.Threading.DispatcherTimer
    $script:AsyncTimer.Interval = [TimeSpan]::FromMilliseconds(300)
    $script:AsyncTimer.Add_Tick({
        if ($script:AsyncHandle -and $script:AsyncHandle.IsCompleted) {
            $script:AsyncTimer.Stop()
            $operationResult = $null
            $errorText = $null
            try {
                $operationResult = @($script:AsyncPowerShell.EndInvoke($script:AsyncHandle))
                if ($script:AsyncPowerShell.Streams.Error.Count -gt 0) {
                    $errorText = (($script:AsyncPowerShell.Streams.Error | ForEach-Object {$_.ToString()}) -join "`n")
                }
            } catch {$errorText = $_.Exception.Message}
            finally {
                $script:AsyncPowerShell.Runspace.Close()
                $script:AsyncPowerShell.Runspace.Dispose()
                $script:AsyncPowerShell.Dispose()
                $script:AsyncPowerShell = $null
                $script:AsyncHandle = $null
            }
            Set-UiBusy $false $(if ($errorText) {'Falhou'} else {'Concluído'})
            if ($errorText) {
                Add-UiMessage $errorText 'ERROR'
                if ($script:ActiveOperation -eq 'Repair') {
                    try {
                        $history = Join-Path $Root 'Reports\History'
                        if (-not (Test-Path -LiteralPath $history)) {New-Item -ItemType Directory -Path $history -Force | Out-Null}
                        [pscustomobject]@{RepairId=$script:ActiveRepairId;Status='Failed';FinishedAt=(Get-Date).ToString('o');Error=$errorText} |
                            ConvertTo-Json -Depth 6 -Compress | Add-Content -LiteralPath (Join-Path $history 'Repairs.jsonl') -Encoding UTF8
                    } catch {Add-UiMessage "Falha ao guardar o histórico da reparação: $($_.Exception.Message)" 'ERROR'}
                }
                $script:ActiveOperation = $null
                $script:ActiveRepairId = $null
                [System.Windows.MessageBox]::Show($errorText,'WinFix Pro - erro','OK','Error') | Out-Null
                return
            }
            if ($script:ActiveOperation -eq 'Diagnostics' -and $operationResult.Count -gt 0) {
                $script:LastDiagnostic = $operationResult[0]
                $Controls.txtHealth.Text = [string]$script:LastDiagnostic.Health.Score
                $Controls.txtHealthRating.Text = [string]$script:LastDiagnostic.Health.Rating
                $summary = $script:LastDiagnostic.Summary
                $Controls.txtCpu.Text = "CPU $($summary.CpuLoadPercent)%"
                $Controls.txtRam.Text = "Memory $($summary.MemoryUsedPercent)%"
                $Controls.txtDisk.Text = "$($summary.SystemDrive) $($summary.DiskFreeGB) GB free"
                $Controls.txtUptime.Text = "Uptime $($summary.Uptime)"
                $Controls.txtReboot.Text = "Pending reboot $($summary.PendingReboot)"
                $history = Join-Path $Root 'Reports\History'
                if (-not (Test-Path -LiteralPath $history)) {New-Item -ItemType Directory -Path $history -Force | Out-Null}
                $script:LastDiagnostic | ConvertTo-Json -Depth 8 -Compress | Add-Content -LiteralPath (Join-Path $history 'Diagnostics.jsonl') -Encoding UTF8
            }
            if ($script:ActiveOperation -eq 'Repair' -and $operationResult.Count -gt 0) {
                $history = Join-Path $Root 'Reports\History'
                if (-not (Test-Path -LiteralPath $history)) {New-Item -ItemType Directory -Path $history -Force | Out-Null}
                $operationResult[0] | ConvertTo-Json -Depth 6 -Compress | Add-Content -LiteralPath (Join-Path $history 'Repairs.jsonl') -Encoding UTF8
            }
            if ($script:ActiveOperation -eq 'Universal' -and $operationResult.Count -gt 0) {
                $script:LastUniversal = $operationResult[0]
                $summary = $script:LastUniversal.Context.Summary
                $health = Get-WinFixHealthScore -Findings @($script:LastUniversal.Context.Findings)
                $Controls.txtHealth.Text = [string]$health.Score
                $Controls.txtHealthRating.Text = $health.Rating
                $Controls.txtCpu.Text = "CPU $($summary.CpuLoadPercent)%"
                $Controls.txtRam.Text = "Memory $($summary.MemoryUsedPercent)%"
                $Controls.txtDisk.Text = "$($summary.SystemDrive) $($summary.DiskFreeGB) GB free"
                $Controls.txtUptime.Text = "Uptime $($summary.Uptime)"
                $Controls.txtReboot.Text = "Pending reboot $($summary.PendingReboot)"
                $history = Join-Path $script:DataRoot 'History'
                if (-not (Test-Path -LiteralPath $history)) {New-Item -ItemType Directory -Path $history -Force | Out-Null}
                $record = [pscustomobject]@{CreatedAt=$script:LastUniversal.Analysis.CreatedAt;Summary=$summary;Analysis=$script:LastUniversal.Analysis}
                $record | ConvertTo-Json -Depth 10 -Compress | Add-Content -LiteralPath (Join-Path $history 'UniversalAnalysis.jsonl') -Encoding UTF8
            }
            if ($script:ActiveOperation -eq 'Universal' -and $script:LastUniversal) {
                $analysis = $script:LastUniversal.Analysis
                $lines = New-Object 'System.Collections.Generic.List[string]'
                $lines.Add("Universal analysis: $($analysis.Status)")
                $lines.Add("Findings: $($analysis.EvidenceSummary.FindingsCount) | Events: $($analysis.EvidenceSummary.EventsCount) | Problem devices: $($analysis.EvidenceSummary.HardwareDevicesWithProblems)")
                $lines.Add("Collection: $($analysis.EvidenceSummary.DataCompleteness)")
                foreach ($errorItem in $analysis.CollectionErrors) {$lines.Add("Collection warning ($($errorItem.Check)): $($errorItem.Error)")}
                foreach ($rule in $analysis.RuleFindings) {$lines.Add("RULE [$($rule.Severity)] $($rule.Title) | $($rule.Recommendation)")}
                foreach ($item in $analysis.KnowledgeMatches) {$lines.Add("KNOWLEDGE candidate: $($item.Entry.title) | $($item.Assessment)")}
                foreach ($item in $analysis.Anomalies) {$lines.Add("ANOMALY $($item.Metric): $($item.Evidence)")}
                foreach ($item in $analysis.Correlations) {$lines.Add("CORRELATION $($item.Signature): $($item.Claim)")}
                foreach ($item in $analysis.Hypotheses) {
                    $lines.Add("HYPOTHESIS [$($item.ConfidenceLabel), confidence $($item.Confidence)] $($item.Title)")
                    $lines.Add("  Assessment: $($item.Assessment)")
                    foreach ($evidence in $item.SupportingEvidence) {$lines.Add("  Evidence: $evidence")}
                    foreach ($evidence in $item.ContradictoryEvidence) {$lines.Add("  Contradictory evidence: $evidence")}
                    foreach ($alternative in $item.Alternatives) {$lines.Add("  Alternative: $alternative")}
                    foreach ($test in $item.RecommendedTests) {$lines.Add("  Next test: $test")}
                }
                foreach ($symptom in $analysis.UnknownSymptoms) {$lines.Add("UNMATCHED SYMPTOM: $symptom (no approved compatible local knowledge match)")}
                foreach ($plugin in $analysis.PluginResults) {
                    $lines.Add("PLUGIN $($plugin.Plugin): $($plugin.Status) $($plugin.Error)")
                    if ($plugin.Result) {$lines.Add("  $($plugin.Result.Severity) $($plugin.Result.Area): $($plugin.Result.Finding)")}
                }
                $Controls.txtOutput.Text = $lines -join [Environment]::NewLine
            } elseif ($script:ActiveOperation -eq 'Diagnostics' -and $operationResult.Count -gt 0) {
                $formattedLines = foreach ($finding in $script:LastDiagnostic.Findings) {
                    "[$($finding.Severity)] $($finding.Area): $($finding.Finding)"
                    "  Evidence: $($finding.Evidence)"
                    "  Recommendation: $($finding.Recommendation)"
                    ''
                }
                $formatted = $formattedLines -join [Environment]::NewLine
            } elseif ($script:ActiveOperation -eq 'Repair' -and $operationResult.Count -gt 0) {
                $formatted = $operationResult[0] | Format-List | Out-String
            } elseif ($operationResult.Count -gt 1) {
                $formatted = $operationResult | Format-Table -AutoSize -Wrap | Out-String
            } elseif ($operationResult.Count -eq 1) {
                $formatted = $operationResult[0] | ConvertTo-Json -Depth 8
            } else {
                $formatted = 'No results returned.'
            }
            $Controls.txtOutput.Text = $formatted
            Add-UiMessage "$script:ActiveOperation terminado."
            $script:ActiveOperation = $null
            $script:ActiveRepairId = $null
        }
    })
    $script:AsyncTimer.Start()
}

$Controls.txtComputer.Text = $env:COMPUTERNAME
$Controls.txtPrivilege.Text = if (Test-WinFixAdmin) {'ADMINISTRATOR'} else {'STANDARD USER'}
$Controls.txtVersion.Text = 'PowerShell 5.1 | WPF'
$Controls.chkSimulation.IsChecked = $true
try {
    $summary = Get-WinFixSystemSummary
    $Controls.txtVersion.Text = "$($summary.Caption) | Build $($summary.BuildNumber)"
    $Controls.txtCpu.Text = "CPU $($summary.CpuLoadPercent)%"
    $Controls.txtRam.Text = "Memory $($summary.MemoryUsedPercent)%"
    $Controls.txtDisk.Text = "$($summary.SystemDrive) $($summary.DiskFreeGB) GB free"
    $Controls.txtUptime.Text = "Uptime $($summary.Uptime)"
    $Controls.txtReboot.Text = "Pending reboot $($summary.PendingReboot)"
    Add-UiMessage "WinFix Pro 0.2 iniciado em $env:COMPUTERNAME."
} catch {
    Add-UiMessage "Falha ao carregar o resumo do sistema: $($_.Exception.Message)" 'ERROR'
}

$Controls.btnScan.Add_Click({Start-BackgroundOperation -Operation 'Diagnostics'})
$Controls.btnNetwork.Add_Click({Start-BackgroundOperation -Operation 'Network'})
$Controls.btnHardware.Add_Click({Start-BackgroundOperation -Operation 'Hardware'})
$Controls.btnPerformance.Add_Click({Start-BackgroundOperation -Operation 'Performance'})
$Controls.btnServices.Add_Click({Start-BackgroundOperation -Operation 'Services'})
$Controls.btnSecurity.Add_Click({Start-BackgroundOperation -Operation 'Security'})
$Controls.btnEvents.Add_Click({Start-BackgroundOperation -Operation 'Events'})
$Controls.btnUniversal.Add_Click({
    $allowPlugins = [bool]$Controls.chkPlugins.IsChecked
    if ($allowPlugins) {
        $confirmation = [System.Windows.MessageBox]::Show(
            "Local PowerShell plugins execute with your account's permissions. Run only organization-reviewed modules. Continue?",
            'Confirm trusted plugin execution','YesNo','Warning')
        if ($confirmation -ne 'Yes') {return}
    }
    $symptoms = @(([string]$Controls.txtSymptoms.Text -split '[\r\n]+' | ForEach-Object {$_.Trim()} | Where-Object {$_}))
    Start-BackgroundOperation -Operation 'Universal' -Symptoms $symptoms -AllowPlugins $allowPlugins
})
$Controls.btnRecordCase.Add_Click({
    if (-not $script:LastUniversal) {
        [System.Windows.MessageBox]::Show('Run Universal Diagnostics before recording a case.','Case Intelligence','OK','Information') | Out-Null
        return
    }
    $sources = @(([string]$Controls.txtCaseSources.Text -split '[\r\n]+' | ForEach-Object {$_.Trim()} | Where-Object {$_}))
    $analysis = $script:LastUniversal.Analysis
    $hypothesisText = @($analysis.Hypotheses | ForEach-Object {"$($_.Title) [$($_.ConfidenceLabel), $($_.Confidence)]"})
    $evidenceText = "Findings=$($analysis.EvidenceSummary.FindingsCount); Events=$($analysis.EvidenceSummary.EventsCount); PnP problem devices=$($analysis.EvidenceSummary.HardwareDevicesWithProblems); collection=$($analysis.EvidenceSummary.DataCompleteness)"
    try {
        $case = Add-WinFixCase -CasesPath (Join-Path $script:DataRoot 'Knowledge\cases.json') `
            -Symptoms ([string]$Controls.txtSymptoms.Text) -Outcome ([string]$Controls.txtCaseOutcome.Text) `
            -EvidenceSummary $evidenceText -Hypotheses ($hypothesisText -join '; ') `
            -Tests ([string]$Controls.txtCaseTests.Text) -Status ([string]$Controls.cmbCaseStatus.SelectedValue) `
            -WindowsVersion ([string]$script:LastUniversal.Context.Summary.Version) `
            -WindowsBuild ([string]$script:LastUniversal.Context.Summary.BuildNumber) -Sources $sources
        $script:LastCaseId = $case.id
        $Controls.txtCaseId.Text = "Saved local case $($case.id) ($($case.status)). Computer/user identifiers are not included."
        Add-UiMessage "Local case recorded: $($case.id)" 'AUDIT'
    } catch {Add-UiMessage "Could not record case: $($_.Exception.Message)" 'ERROR'; [System.Windows.MessageBox]::Show($_.Exception.Message,'Case error','OK','Error') | Out-Null}
})
$Controls.btnPromoteCase.Add_Click({
    if (-not $script:LastCaseId) {
        [System.Windows.MessageBox]::Show('Record a case first.','Knowledge Center','OK','Information') | Out-Null
        return
    }
    try {
        $candidate = ConvertTo-WinFixKnowledgeCandidate -CasesPath (Join-Path $script:DataRoot 'Knowledge\cases.json') `
            -KnowledgePath (Join-Path $script:DataRoot 'Knowledge\knowledge.json') -CaseId $script:LastCaseId `
            -ReviewedBy 'Local technician' -ExpiresAt (Get-Date).AddDays(180).ToString('o')
        $script:LastCandidateId = $candidate.id
        $Controls.txtCaseId.Text = "Knowledge candidate $($candidate.id) is pending review. Add at least one source URL before approval."
        Add-UiMessage "Knowledge candidate submitted for review: $($candidate.id)" 'AUDIT'
    } catch {Add-UiMessage "Could not propose knowledge candidate: $($_.Exception.Message)" 'ERROR'; [System.Windows.MessageBox]::Show($_.Exception.Message,'Knowledge error','OK','Error') | Out-Null}
})
$Controls.btnApproveCase.Add_Click({
    if (-not $script:LastCandidateId) {
        [System.Windows.MessageBox]::Show('Propose a resolved case as a knowledge candidate first.','Knowledge Center','OK','Information') | Out-Null
        return
    }
    $answer = [System.Windows.MessageBox]::Show(
        "Approving this entry will allow it to appear as a compatible local knowledge match until its expiry. Verify its source and reproduction steps first. Approve?",
        'Review knowledge candidate','YesNo','Warning')
    if ($answer -ne 'Yes') {return}
    try {
        $entry = Set-WinFixKnowledgeReview -KnowledgePath (Join-Path $script:DataRoot 'Knowledge\knowledge.json') `
            -EntryId $script:LastCandidateId -ReviewStatus Approved -ReviewedBy 'Local technician' `
            -ReviewNotes ([string]$Controls.txtReviewNotes.Text)
        Add-UiMessage "Knowledge candidate approved: $($entry.id)" 'AUDIT'
        $Controls.txtCaseId.Text = "Approved local knowledge $($entry.id), expires $($entry.expiresAt)."
    } catch {Add-UiMessage "Could not approve candidate: $($_.Exception.Message)" 'ERROR'; [System.Windows.MessageBox]::Show($_.Exception.Message,'Review error','OK','Error') | Out-Null}
})
$Controls.btnImportKnowledge.Add_Click({
    $dialog = New-Object Microsoft.Win32.OpenFileDialog
    $dialog.Filter = 'WinFix knowledge bundles (*.json)|*.json'
    $dialog.Multiselect = $false
    if ($dialog.ShowDialog() -ne $true) {return}
    try {
        $imported = @(Import-WinFixKnowledgeBundle -SourcePath $dialog.FileName `
            -KnowledgePath (Join-Path $script:DataRoot 'Knowledge\knowledge.json') -ImportedBy 'Local technician')
        $Controls.txtCaseId.Text = "Imported $($imported.Count) knowledge candidate(s). All require review before matching."
        Add-UiMessage "Imported $($imported.Count) local knowledge candidate(s) from $($dialog.FileName)" 'AUDIT'
    } catch {Add-UiMessage "Could not import knowledge bundle: $($_.Exception.Message)" 'ERROR'; [System.Windows.MessageBox]::Show($_.Exception.Message,'Knowledge import error','OK','Error') | Out-Null}
})
$Controls.btnRepair.Add_Click({
    $repairId = [string]$Controls.cmbRepair.SelectedValue
    $repair = Get-WinFixRepairCatalog | Where-Object {$_.Id -eq $repairId}
    if (-not $repair) {return}
    $simulation = [bool]$Controls.chkSimulation.IsChecked
    $mode = if ($simulation) {'SIMULAÇÃO — não serão efetuadas alterações.'} else {'EXECUÇÃO REAL — a operação pode alterar o Windows.'}
    $message = "$($repair.Name)`n`n$($repair.Description)`n`nRisco: $($repair.Risk)`nRequer administrador: $($repair.RequiresAdmin)`nImpacto/reinício: $($repair.MayRestart)`nBackup: $($repair.Backup)`nValidação: $($repair.Validation)`n`n$mode`n`nPretende continuar?"
    $answer = [System.Windows.MessageBox]::Show($message,'Confirmar reparação','YesNo','Warning')
    if ($answer -ne 'Yes') {Add-UiMessage "Reparação $repairId cancelada pelo utilizador." 'AUDIT'; return}
    Add-UiMessage "Reparação autorizada: $repairId | simulation=$simulation" 'AUDIT'
    Start-BackgroundOperation -Operation 'Repair' -RepairId $repairId
})
$Controls.btnExport.Add_Click({
    if (-not $script:LastDiagnostic) {
        [System.Windows.MessageBox]::Show('Execute primeiro um diagnóstico para gerar um relatório.','Relatório','OK','Information') | Out-Null
        return
    }
    try {
        $path = Export-WinFixReport -Data $script:LastDiagnostic -Format ([string]$Controls.cmbFormat.SelectedValue) -ReportsDirectory (Join-Path $Root 'Reports')
        Add-UiMessage "Relatório exportado: $path"
        [System.Windows.MessageBox]::Show("Relatório guardado em:`n$path",'Exportação concluída','OK','Information') | Out-Null
    } catch {Add-UiMessage "Falha ao exportar: $($_.Exception.Message)" 'ERROR'}
})
$null = $window.ShowDialog()
