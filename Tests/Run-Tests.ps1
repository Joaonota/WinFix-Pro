$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $root 'Core\HealthScore.psm1') -Force
Import-Module (Join-Path $root 'Core\SystemInfo.psm1') -Force
Import-Module (Join-Path $root 'Core\UniversalDiagnostics.psm1') -Force
Import-Module (Join-Path $root 'Core\UniversalCollector.psm1') -Force
Import-Module (Join-Path $root 'Core\KnowledgeEngine.psm1') -Force
Import-Module (Join-Path $root 'Core\Reports.psm1') -Force
Import-Module (Join-Path $root 'Repairs\RepairEngine.psm1') -Force

$script:passed = 0
function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) {throw "FAIL: $Message"}
    $script:passed++
    Write-Host "PASS: $Message"
}

$score = Get-WinFixHealthScore -Findings @()
Assert-True ($score.Score -eq 100 -and $score.Rating -eq 'Excellent') 'Health score maps no findings to 100/Excellent'
$score = Get-WinFixHealthScore -Findings @(
    [pscustomobject]@{Severity='Critical'},[pscustomobject]@{Severity='High'}
)
Assert-True ($score.Score -eq 43 -and $score.Rating -eq 'Critical') 'Health score applies documented severity penalties and floor'
$score = Get-WinFixHealthScore -Findings @([pscustomobject]@{Severity='Unknown'})
Assert-True ($score.Score -eq 100) 'Unavailable/unrecognized severities do not penalize health'

$system = Get-WinFixSystemSummary
Assert-True (-not [string]::IsNullOrWhiteSpace($system.Caption) -and -not [string]::IsNullOrWhiteSpace($system.Version)) 'Windows version detection returns live OS information'
$hardware = Get-WinFixHardwareInventory
Assert-True ($null -ne $hardware.Bios -and $null -ne $hardware.ComputerSystem -and $null -ne $hardware.Disks) 'Hardware inventory returns BIOS, system and disk sections'

$rulesPath = Join-Path $root 'Rules\Issues.json'
$profile = Get-WinFixDiagnosticProfile -Path (Join-Path $root 'Profiles\default.json')
Assert-True ($profile.name -eq 'Default' -and @($profile.enabledAreas).Count -ge 5) 'Versioned diagnostic profile loads and validates configured areas'
$invalidProfilePath = Join-Path ([IO.Path]::GetTempPath()) ("WinFixProfile-" + [guid]::NewGuid().ToString('N') + '.json')
try {
    '{"schemaVersion":1,"name":"Invalid","enabledAreas":["UnknownArea"],"eventLookbackDays":7,"maxEventsPerLog":50}' |
        Set-Content -LiteralPath $invalidProfilePath -Encoding UTF8
    $profileRejected = $false
    try {Get-WinFixDiagnosticProfile -Path $invalidProfilePath}
    catch {$profileRejected=$true}
    Assert-True $profileRejected 'Diagnostic profiles reject unknown collection areas'
} finally {
    if (Test-Path -LiteralPath $invalidProfilePath) {Remove-Item -LiteralPath $invalidProfilePath -Force}
}
$rules = @(Get-WinFixRules -RulesPath $rulesPath -WindowsBuild '26300')
$ruleContext = [pscustomobject]@{
    Summary=[pscustomobject]@{BuildNumber='26300';Version='10.0.26300';DiskFreeGB=5;MemoryUsedPercent=40}
    Hardware=[pscustomobject]@{ProblemDevices=@()}
}
$matchedRules = @(Invoke-WinFixRules -Context $ruleContext -Rules $rules)
Assert-True (@($matchedRules | Where-Object {$_.Id -eq 'system-volume-low-space'}).Count -eq 1) 'Declarative rules detect a measured low-space condition'

$tempIntel = Join-Path ([IO.Path]::GetTempPath()) ("WinFixIntel-" + [guid]::NewGuid().ToString('N'))
try {
    $rulesVersionPath = Join-Path $tempIntel 'bounded-rules.json'
    New-Item -ItemType Directory -Path $tempIntel -Force | Out-Null
    '{"schemaVersion":1,"rules":[{"id":"new-build-only","severity":"Low","minWindowsBuild":30000,"maxWindowsBuild":40000,"evidence":[{"path":"Summary.DiskFreeGB","operator":"LessThan","value":10}]}]}' |
        Set-Content -LiteralPath $rulesVersionPath -Encoding UTF8
    Assert-True (@(Get-WinFixRules -RulesPath $rulesVersionPath -WindowsBuild '26300').Count -eq 0) 'Version-bounded rules are excluded on incompatible Windows builds'

    $ruleWithContradiction = Get-WinFixRootCauseHypotheses -RuleFindings $matchedRules -ContradictoryEvidence @(
        [pscustomobject]@{HypothesisId='system-volume-low-space';Evidence='Independent measurement reports ample free space.'}
    )
    Assert-True ($ruleWithContradiction[0].ConfidenceLabel -eq 'Conflicting evidence' -and $ruleWithContradiction[0].Confidence -le 0.25) 'Contradictory evidence lowers confidence and marks the hypothesis inconclusive'

    $multi = @(Get-WinFixRootCauseHypotheses -Findings @(
        [pscustomobject]@{Area='Storage';Severity='High';Finding='Low space';Evidence='5 GB free'}
        [pscustomobject]@{Area='Services';Severity='Critical';Finding='Service unavailable';Evidence='Stopped'}
    ))
    Assert-True ($multi.Count -eq 2) 'Multiple independent findings remain separate hypotheses rather than a single asserted cause'

    $eventTime = Get-Date
    $correlations = @(Get-WinFixCorrelations -Events @(
        [pscustomobject]@{ProviderName='Example';Id=99;TimeCreated=$eventTime;LogName='System'}
        [pscustomobject]@{ProviderName='Example';Id=99;TimeCreated=$eventTime.AddMinutes(2);LogName='System'}
    ))
    Assert-True ($correlations.Count -eq 1 -and $correlations[0].Count -eq 2 -and $correlations[0].Claim -like '*causality is not proven*') 'Repeated event signatures are correlated without asserting causality'

    $baseline = @(
        [pscustomobject]@{Summary=[pscustomobject]@{CpuLoadPercent=20;MemoryUsedPercent=40;DiskFreeGB=100}}
        [pscustomobject]@{Summary=[pscustomobject]@{CpuLoadPercent=22;MemoryUsedPercent=41;DiskFreeGB=99}}
        [pscustomobject]@{Summary=[pscustomobject]@{CpuLoadPercent=19;MemoryUsedPercent=39;DiskFreeGB=101}}
    )
    $anomalies = @(Get-WinFixAnomalyFindings -CurrentSummary ([pscustomobject]@{CpuLoadPercent=80;MemoryUsedPercent=42;DiskFreeGB=100}) -Baseline $baseline)
    Assert-True ($anomalies.Count -eq 1 -and $anomalies[0].Metric -eq 'CpuLoadPercent') 'Behavior anomaly detection compares current metrics to a sufficient local baseline'
    Assert-True (@(Get-WinFixAnomalyFindings -CurrentSummary $baseline[0].Summary -Baseline @($baseline | Select-Object -First 2)).Count -eq 0) 'Anomaly detection declines to infer a baseline from insufficient samples'

    $knowledgePath = Join-Path $tempIntel 'knowledge.json'
    $casesPath = Join-Path $tempIntel 'cases.json'
    $unknownContext = [pscustomobject]@{
        Summary=[pscustomobject]@{Version='10.0.26300';BuildNumber='26300';DiskFreeGB=100;MemoryUsedPercent=40;CpuLoadPercent=20}
        Findings=@()
        Hardware=[pscustomobject]@{ProblemDevices=@()}
        Events=@()
        CollectionErrors=@()
    }
    $unknown = Invoke-WinFixUniversalAnalysis -Context $unknownContext -RulesPath $rulesPath -KnowledgePath $knowledgePath -PluginDirectory (Join-Path $root 'Plugins') -Symptoms @('System is slow without an error code')
    Assert-True ($unknown.Status -eq 'Inconclusive' -and $unknown.Hypotheses[0].Confidence -eq 0 -and $unknown.Hypotheses[0].Status -eq 'Inconclusive') 'Uncatalogued symptom with no evidence is explicitly inconclusive'
    Assert-True ($unknown.UnknownSymptoms.Count -eq 1 -and $unknown.KnowledgeMatches.Count -eq 0) 'Unmatched symptoms are surfaced rather than assigned a fabricated known cause'
    $unsupportedBuild = Invoke-WinFixUniversalAnalysis -Context $unknownContext -RulesPath $rulesVersionPath -KnowledgePath $knowledgePath -PluginDirectory (Join-Path $root 'Plugins') -Symptoms @('Unlisted issue on newer build')
    Assert-True ($unsupportedBuild.Status -eq 'Inconclusive' -and $unsupportedBuild.RuleFindings.Count -eq 0) 'No compatible rules on an unsupported future build produce an inconclusive analysis'

    $plan = @(New-WinFixTroubleshootingPlan -Hypotheses @([pscustomobject]@{Id='slow-system';RecommendedTests=@('Inspect storage wait times')}))
    $completed = Add-WinFixTestResult -Plan $plan -TestId $plan[0].Id -Result NotConfirmed -Evidence 'No storage latency observed during the reported interval.'
    $repeatBlocked = $false
    try {Add-WinFixTestResult -Plan $plan -TestId $plan[0].Id -Result NotConfirmed -Evidence 'Repeated test'}
    catch {$repeatBlocked = $true}
    Assert-True ($repeatBlocked -and $completed.Status -eq 'Completed' -and $completed.Result -eq 'NotConfirmed') 'Adaptive troubleshooting records outcomes and blocks repeating a completed test'
    Assert-True (@(New-WinFixTroubleshootingPlan -Hypotheses @([pscustomobject]@{Id='slow-system';RecommendedTests=@('Inspect storage wait times')}) -CompletedTestIds @($plan[0].Id)).Count -eq 0) 'Completed tests are excluded from the next troubleshooting plan'

    $plugins = @(Get-WinFixDiagnosticPlugins -PluginDirectory (Join-Path $root 'Plugins') -WindowsBuild '26300')
    Assert-True (@($plugins | Where-Object {$_.Name -eq 'ExampleDiagnostics' -and $_.Compatible}).Count -eq 1) 'Compatible local diagnostic plugin manifests are discovered'
    $incompatiblePlugins = @(Get-WinFixDiagnosticPlugins -PluginDirectory (Join-Path $root 'Plugins') -WindowsBuild '0')
    Assert-True (@($incompatiblePlugins | Where-Object {-not $_.Compatible}).Count -eq 0) 'Unbounded plugins remain eligible when the OS build is unknown'
    $pluginOutput = @(Invoke-WinFixDiagnosticPlugins -PluginDirectory (Join-Path $root 'Plugins') -WindowsBuild '26300' -Context ([pscustomobject]@{Summary=[pscustomobject]@{BuildNumber='26300'};Findings=@()}))
    Assert-True ($pluginOutput.Count -eq 1 -and $pluginOutput[0].Status -eq 'Completed') 'Opt-in plugin execution validates the plugin result contract'

    $case = Add-WinFixCase -CasesPath $casesPath -Symptoms "Slow behavior on $env:COMPUTERNAME for $env:USERNAME" -Outcome 'Resolved using a documented app configuration correction.' -EvidenceSummary 'No host identifiers' -Tests 'Verified with a repeat launch and event check.' -Status Resolved -WindowsVersion '10.0.26300' -WindowsBuild '26300' -Sources @('https://learn.microsoft.com/windows/')
    Assert-True ($case.symptoms -notmatch [regex]::Escape($env:COMPUTERNAME) -and $case.symptoms -notmatch [regex]::Escape($env:USERNAME)) 'Saved cases redact local computer and account identifiers'
    $candidate = ConvertTo-WinFixKnowledgeCandidate -CasesPath $casesPath -KnowledgePath $knowledgePath -CaseId $case.id -ReviewedBy 'QA role' -ExpiresAt (Get-Date).AddDays(30).ToString('o')
    Assert-True ($candidate.reviewStatus -eq 'PendingReview' -and (Get-WinFixKnowledgeEntries -Path $knowledgePath -WindowsVersion '10.0.26300' -WindowsBuild '26300').Active.Count -eq 0) 'New case knowledge remains excluded until reviewed'
    $approved = Set-WinFixKnowledgeReview -KnowledgePath $knowledgePath -EntryId $candidate.id -ReviewStatus Approved -ReviewedBy 'QA role' -ReviewNotes 'Source and reproduction verified.'
    $activeKnowledge = Get-WinFixKnowledgeEntries -Path $knowledgePath -WindowsVersion '10.0.26300' -WindowsBuild '26300'
    Assert-True ($approved.reviewStatus -eq 'Approved' -and $activeKnowledge.Active.Count -eq 1) 'Reviewed compatible knowledge can be activated'
    $knownContext = [pscustomobject]@{
        Summary=[pscustomobject]@{Version='10.0.26300';BuildNumber='26300';DiskFreeGB=100;MemoryUsedPercent=40;CpuLoadPercent=20}
        Findings=@();Hardware=[pscustomobject]@{ProblemDevices=@()};Events=@();CollectionErrors=@()
    }
    $knownAnalysis = Invoke-WinFixUniversalAnalysis -Context $knownContext -RulesPath $rulesPath -KnowledgePath $knowledgePath -PluginDirectory (Join-Path $root 'Plugins') -Symptoms @('Slow behavior on this system')
    Assert-True ($knownAnalysis.KnowledgeMatches.Count -eq 1) 'Approved, compatible knowledge can match a symptom without asserting causality'
    $expiredKnowledge = Get-WinFixKnowledgeEntries -Path $knowledgePath -WindowsVersion '10.0.26300' -WindowsBuild '26300' -At ([datetime]::Parse($candidate.expiresAt).AddSeconds(1))
    Assert-True ($expiredKnowledge.Active.Count -eq 0 -and $expiredKnowledge.Excluded.Count -eq 1) 'Expired knowledge entries are excluded from matching'
    Assert-True ((Get-WinFixKnowledgeEntries -Path $knowledgePath -WindowsVersion '10.0.99999' -WindowsBuild '99999').Active.Count -eq 0) 'Knowledge is excluded for an incompatible Windows version'

    $invalidSchemaPath = Join-Path $tempIntel 'invalid-schema.json'
    '{"schemaVersion":99,"entries":[]}' | Set-Content -LiteralPath $invalidSchemaPath -Encoding UTF8
    $schemaRejected = $false
    try {Get-WinFixKnowledgeEntries -Path $invalidSchemaPath -WindowsVersion '10.0' -WindowsBuild '1'}
    catch {$schemaRejected = $true}
    Assert-True $schemaRejected 'Unsupported knowledge schema versions fail explicitly'

    $bundlePath = Join-Path $tempIntel 'bundle.json'
    $bundleEntry = [pscustomobject]@{
        id='external-doc-1';expiresAt=(Get-Date).AddDays(20).ToString('o')
        windowsVersion='10.0.26300';minimumWindowsBuild=26000;maximumWindowsBuild=27000
        title='Documented test issue';summary='A documented corrective action.'
        verification='Repeat the documented test.';sources=@('https://learn.microsoft.com/windows/')
    }
    [pscustomobject]@{schemaVersion=1;entries=@($bundleEntry)} | ConvertTo-Json -Depth 6 |
        Set-Content -LiteralPath $bundlePath -Encoding UTF8
    $importedBundle = @(Import-WinFixKnowledgeBundle -SourcePath $bundlePath -KnowledgePath $knowledgePath -ImportedBy 'Test role')
    Assert-True ($importedBundle.Count -eq 1 -and $importedBundle[0].reviewStatus -eq 'PendingReview') 'Local versioned knowledge bundles import as untrusted pending-review candidates'
    Assert-True ((Get-WinFixKnowledgeEntries -Path $knowledgePath -WindowsVersion '10.0.26300' -WindowsBuild '26300').Active.Count -eq 1) 'Unreviewed imported knowledge is not activated'
    $importedApproved = Set-WinFixKnowledgeReview -KnowledgePath $knowledgePath -EntryId 'external-doc-1' -ReviewStatus Approved -ReviewedBy 'QA role' -ReviewNotes 'Verified source and reproduction.'
    Assert-True ($importedApproved.reviewStatus -eq 'Approved' -and (Get-WinFixKnowledgeEntries -Path $knowledgePath -WindowsVersion '10.0.26300' -WindowsBuild '26300').Active.Count -eq 2) 'Imported technical knowledge activates only after explicit review'

    $insecureBundlePath = Join-Path $tempIntel 'insecure-bundle.json'
    $bundleEntry.id='insecure-doc'
    $bundleEntry.sources=@('http://example.test/untrusted')
    [pscustomobject]@{schemaVersion=1;entries=@($bundleEntry)} | ConvertTo-Json -Depth 6 |
        Set-Content -LiteralPath $insecureBundlePath -Encoding UTF8
    $insecureRejected = $false
    try {Import-WinFixKnowledgeBundle -SourcePath $insecureBundlePath -KnowledgePath $knowledgePath -ImportedBy 'Test role'}
    catch {$insecureRejected=$true}
    Assert-True $insecureRejected 'Knowledge bundle import rejects non-HTTPS provenance'
    $querySourceRejected = $false
    try {Add-WinFixCase -CasesPath $casesPath -Symptoms 'Test symptom' -Outcome 'Test result' -Sources @('https://example.test/doc?token=secret')}
    catch {$querySourceRejected=$true}
    Assert-True $querySourceRejected 'Case source URLs with query strings are rejected to avoid storing URL tokens'
} finally {
    if (Test-Path -LiteralPath $tempIntel) {Remove-Item -LiteralPath $tempIntel -Recurse -Force}
}

$catalog = @(Get-WinFixRepairCatalog)
Assert-True ($catalog.Count -ge 3) 'Repair catalog is populated'
$invalidRejected = $false
try {Invoke-WinFixRepair -RepairId 'NotAllowed' -Confirmed -Simulation}
catch {$invalidRejected = $true}
Assert-True $invalidRejected 'Repair parameter validation rejects unlisted operations'
$rejected = $false
try {Invoke-WinFixRepair -RepairId 'FlushDns' -Confirmed:$false -Simulation}
catch {$rejected = $_.Exception.Message -like '*autorização explícita*'}
Assert-True $rejected 'Repair refuses execution without explicit confirmation'
$simulated = Invoke-WinFixRepair -RepairId 'FlushDns' -Confirmed -Simulation
Assert-True ($simulated.Status -eq 'Simulated' -and -not $simulated.Validated) 'Simulation never reports a repair as verified'

$temp = Join-Path ([IO.Path]::GetTempPath()) ("WinFixTests-" + [guid]::NewGuid().ToString('N'))
try {
    $data = [pscustomobject]@{Summary=[pscustomobject]@{Caption='Test Windows';Version='10.0'};Health=$score;Findings=@([pscustomobject]@{Area='Test';Severity='Info';Finding='A < B';Evidence='sample';Recommendation='none'})}
    foreach ($format in @('HTML','JSON','CSV','TXT')) {
        $reportPath = Export-WinFixReport -Data $data -Format $format -ReportsDirectory $temp
        Assert-True ((Test-Path -LiteralPath $reportPath) -and ((Get-Item -LiteralPath $reportPath).Length -gt 0)) "Report export creates nonempty $format file"
    }
} finally {
    if (Test-Path -LiteralPath $temp) {Remove-Item -LiteralPath $temp -Recurse -Force}
}

Write-Host "All $script:passed tests passed."
