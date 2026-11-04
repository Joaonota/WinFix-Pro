param(
    [Parameter(Mandatory)]
    [ValidateSet('Dashboard','Universal','Diagnostics','Hardware','Performance','Network','WindowsUpdate','Security','Services','Drivers','CommonIssues','Knowledge','Unknown','Repair','SaveCase','ImportKnowledge','ReviewKnowledge')]
    [string]$Operation,
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$PayloadPath
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$engineRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$payload = Get-Content -LiteralPath $PayloadPath -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
$dataRoot = Join-Path $env:LOCALAPPDATA 'WinFixPro\0.2'
$knowledgePath = Join-Path $dataRoot 'Knowledge\knowledge.json'
$modules = @(
    'HealthScore.psm1','SystemInfo.psm1','Logging.psm1','DiagnosticsEngine.psm1',
    'NetworkAnalyzer.psm1','ServiceManager.psm1','EventAnalyzer.psm1','SecurityAnalyzer.psm1',
    'PerformanceAnalyzer.psm1','Reports.psm1','KnowledgeEngine.psm1','UniversalDiagnostics.psm1',
    'UniversalCollector.psm1'
)
foreach ($module in $modules) {
    Import-Module (Join-Path $engineRoot "Core\$module") -Force -ErrorAction Stop
}
Import-Module (Join-Path $engineRoot 'Repairs\RepairEngine.psm1') -Force -ErrorAction Stop

function Write-ProgressFrame {
    param([int]$Percent,[string]$Message)
    [Console]::Out.WriteLine((@{type='progress';percent=$Percent;message=$Message} | ConvertTo-Json -Compress))
    [Console]::Out.Flush()
}

try {
    Write-ProgressFrame 5 "Starting $Operation"
    $result = switch ($Operation) {
        'Dashboard' {
            Write-ProgressFrame 35 'Collecting live system summary'
            $summary = Get-WinFixSystemSummary
            Write-ProgressFrame 75 'Collecting hardware summary'
            [pscustomobject]@{Summary=$summary;Hardware=Get-WinFixHardwareInventory}
        }
        'Diagnostics' {
            Write-ProgressFrame 40 'Checking operating system, services and event log'
            Invoke-WinFixDiagnostics
        }
        'Universal' {
            $profile = Get-WinFixDiagnosticProfile -Path (Join-Path $engineRoot 'Profiles\default.json')
            Write-ProgressFrame 10 'Collecting OS, hardware and performance evidence'
            $context = Get-WinFixUniversalEvidence -Root $engineRoot `
                -EventLookbackDays ([int]$profile.eventLookbackDays) `
                -MaximumEventsPerLog ([int]$profile.maxEventsPerLog)
            Write-ProgressFrame 78 'Correlating findings and evaluating hypotheses'
            $analysis = Invoke-WinFixUniversalAnalysis -Context $context `
                -RulesPath (Join-Path $engineRoot 'Rules\Issues.json') `
                -KnowledgePath $knowledgePath `
                -PluginDirectory (Join-Path $engineRoot 'Plugins') `
                -Symptoms @($payload.Symptoms) -AllowPlugins:([bool]$payload.AllowPlugins) `
                -Baseline @($context.Baseline)
            [pscustomobject]@{Context=$context;Analysis=$analysis}
        }
        'Hardware' {
            Write-ProgressFrame 40 'Reading hardware and Plug and Play inventory'
            [pscustomobject]@{Hardware=Get-WinFixHardwareInventory;Drivers=@(Get-CimInstance Win32_PnPSignedDriver -ErrorAction Stop | Select-Object DeviceName,DriverVersion,DriverDate,Manufacturer,IsSigned);PhysicalDisks=if (Get-Command Get-PhysicalDisk -ErrorAction SilentlyContinue) {@(Get-PhysicalDisk -ErrorAction Stop | Select-Object FriendlyName,MediaType,HealthStatus,OperationalStatus,Size)} else {@()}}
        }
        'Performance' {
            Write-ProgressFrame 35 'Sampling top processes'
            [pscustomobject]@{Summary=Get-WinFixSystemSummary;Processes=@(Get-WinFixTopProcesses -Count 30);Startup=@(Get-WinFixStartupInventory)}
        }
        'Network' {
            Write-ProgressFrame 35 'Checking adapters, gateway, DNS and HTTPS'
            [pscustomobject]@{Configuration=@(Get-NetIPConfiguration -ErrorAction Stop);Tests=@(Get-WinFixNetworkDiagnostic)}
        }
        'WindowsUpdate' {
            Write-ProgressFrame 35 'Collecting installed update and service information'
            [pscustomobject]@{InstalledUpdates=@(Get-HotFix -ErrorAction Stop | Sort-Object InstalledOn -Descending | Select-Object -First 200);Services=@(Get-CimInstance Win32_Service -Filter "Name='wuauserv' OR Name='BITS' OR Name='UsoSvc'" -ErrorAction Stop | Select-Object Name,DisplayName,State,StartMode)}
        }
        'Security' {
            Write-ProgressFrame 35 'Checking Defender, firewall and UAC'
            Get-WinFixSecurityStatus
        }
        'Services' {
            Write-ProgressFrame 35 'Reading service and scheduled-task inventory'
            [pscustomobject]@{Services=@(Get-WinFixServiceInventory);ScheduledTasks=@(Get-ScheduledTask -ErrorAction Stop | Select-Object TaskPath,TaskName,State)}
        }
        'Drivers' {
            Write-ProgressFrame 35 'Reading signed drivers and problem devices'
            [pscustomobject]@{Drivers=@(Get-CimInstance Win32_PnPSignedDriver -ErrorAction Stop | Select-Object DeviceName,DriverVersion,DriverDate,Manufacturer,IsSigned,DeviceID);ProblemDevices=@(Get-CimInstance Win32_PnPEntity -Filter 'ConfigManagerErrorCode <> 0' -ErrorAction Stop | Select-Object Name,PNPDeviceID,ConfigManagerErrorCode)}
        }
        'CommonIssues' {
            Write-ProgressFrame 30 'Evaluating compatible local rules'
            $summary = Get-WinFixSystemSummary
            $hardware = Get-WinFixHardwareInventory
            $context = [pscustomobject]@{Summary=$summary;Hardware=$hardware}
            $rules = @(Get-WinFixRules -RulesPath (Join-Path $engineRoot 'Rules\Issues.json') -WindowsBuild $summary.BuildNumber)
            [pscustomobject]@{CompatibleRules=$rules;MatchedRules=@(Invoke-WinFixRules -Context $context -Rules $rules);Summary=$summary}
        }
        'Knowledge' {
            Write-ProgressFrame 30 'Searching local, approved and compatible knowledge'
            if (-not (Test-Path -LiteralPath $knowledgePath)) {
                [pscustomobject]@{Active=@();Excluded=@();Message='No local knowledge entries are installed.'}
            } else {
                $summary = Get-WinFixSystemSummary
                $knowledge = Get-WinFixKnowledgeEntries -Path $knowledgePath -WindowsVersion $summary.Version -WindowsBuild $summary.BuildNumber
                $query = [string]$payload.KnowledgeQuery
                $matches = @($knowledge.Active | Where-Object {
                    -not $query -or ([string]$_.title).IndexOf($query,[StringComparison]::OrdinalIgnoreCase) -ge 0 -or
                        ([string]$_.summary).IndexOf($query,[StringComparison]::OrdinalIgnoreCase) -ge 0
                })
                [pscustomobject]@{Matches=$matches;ActiveCount=$knowledge.Active.Count;ExcludedCount=$knowledge.Excluded.Count}
            }
        }
        'SaveCase' {
            $symptoms = (@($payload.Symptoms) -join '; ').Trim()
            $outcome = [string]$payload.CaseOutcome
            if ([string]::IsNullOrWhiteSpace($symptoms) -or [string]::IsNullOrWhiteSpace($outcome)) {
                throw 'Enter at least one symptom and a case outcome/status note.'
            }
            $tests = [string]$payload.CaseTests
            $status = [string]$payload.CaseStatus
            if ($status -eq 'Resolved' -and [string]::IsNullOrWhiteSpace($tests)) {
                throw 'Resolved cases require verification tests.'
            }
            $case = Add-WinFixCase -CasesPath (Join-Path $dataRoot 'Knowledge\cases.json') `
                -Symptoms $symptoms -Outcome $outcome -EvidenceSummary ([string]$payload.CaseEvidence) `
                -Hypotheses ([string]$payload.CaseHypotheses) -Tests $tests -Status $status `
                -WindowsVersion ([string](Get-WinFixSystemSummary).Version) `
                -WindowsBuild ([string](Get-WinFixSystemSummary).BuildNumber) `
                -Sources @($payload.CaseSources)
            [pscustomobject]@{Case=$case;Message='Case recorded locally. Review its content before sharing.'}
        }
        'ImportKnowledge' {
            $bundlePath = [string]$payload.KnowledgeBundlePath
            if (-not (Test-Path -LiteralPath $bundlePath -PathType Leaf)) {throw 'Selected knowledge bundle does not exist.'}
            $entries = @(Import-WinFixKnowledgeBundle -SourcePath $bundlePath -KnowledgePath $knowledgePath -ImportedBy 'Local technician')
            [pscustomobject]@{Imported=$entries;Count=$entries.Count;Status='Pending review'}
        }
        'ReviewKnowledge' {
            if ([string]::IsNullOrWhiteSpace([string]$payload.KnowledgeEntryId) -or
                [string]::IsNullOrWhiteSpace([string]$payload.ReviewNotes)) {
                throw 'A knowledge entry ID and review notes are required.'
            }
            $review = Set-WinFixKnowledgeReview -KnowledgePath $knowledgePath `
                -EntryId ([string]$payload.KnowledgeEntryId) -ReviewStatus Approved `
                -ReviewedBy 'Local technician' -ReviewNotes ([string]$payload.ReviewNotes)
            [pscustomobject]@{Entry=$review;Status='Approved'}
        }
        'Unknown' {
            Write-ProgressFrame 15 'Collecting fresh evidence for unknown symptoms'
            $context = Get-WinFixUniversalEvidence -Root $engineRoot
            $analysis = Invoke-WinFixUniversalAnalysis -Context $context `
                -RulesPath (Join-Path $engineRoot 'Rules\Issues.json') `
                -KnowledgePath $knowledgePath -PluginDirectory (Join-Path $engineRoot 'Plugins') `
                -Symptoms @($payload.Symptoms) -Baseline @($context.Baseline)
            [pscustomobject]@{Context=$context;Analysis=$analysis}
        }
        'Repair' {
            $repairId = [string]$payload.RepairId
            if ($repairId -notin @('SfcScan','DismRestore','FlushDns')) {throw 'Repair identifier is not allow-listed.'}
            Write-ProgressFrame 20 "Running confirmed repair $repairId"
            $repair = Invoke-WinFixRepair -RepairId $repairId -Confirmed -Simulation:$false
            [pscustomobject]@{Repair=$repair;PostRepairSummary=Get-WinFixSystemSummary}
        }
    }
    Write-ProgressFrame 95 'Serializing structured result'
    $json = ConvertTo-Json -InputObject $result -Depth 18 -Compress
    [Console]::Out.WriteLine((@{type='result';data=(ConvertFrom-Json -InputObject $json)} | ConvertTo-Json -Depth 18 -Compress))
    [Console]::Out.Flush()
    exit 0
}
catch {
    [Console]::Error.WriteLine($_.Exception.ToString())
    exit 1
}
