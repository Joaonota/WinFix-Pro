function Get-WinFixObjectPathValue {
    param([Parameter(Mandatory)][object]$InputObject,[Parameter(Mandatory)][string]$Path)
    $value = $InputObject
    foreach ($part in $Path.Split('.')) {
        if ($part -eq 'Count') {
            $value = @($value).Count
        } elseif ($value -is [System.Collections.IDictionary] -and $value.Contains($part)) {
            $value = $value[$part]
        } elseif ($null -ne $value -and $value.PSObject.Properties[$part]) {
            $value = $value.PSObject.Properties[$part].Value
        } else {
            return $null
        }
    }
    return ,$value
}

function Test-WinFixRuleCondition {
    param([Parameter(Mandatory)][object]$Condition,[Parameter(Mandatory)][object]$Context)
    $actual = Get-WinFixObjectPathValue -InputObject $Context -Path ([string]$Condition.path)
    if ($null -eq $actual) {return $false}
    $expected = $Condition.value
    switch ([string]$Condition.operator) {
        'Equal' {return [string]$actual -eq [string]$expected}
        'NotEqual' {return [string]$actual -ne [string]$expected}
        'GreaterThan' {return [double]$actual -gt [double]$expected}
        'GreaterThanOrEqual' {return [double]$actual -ge [double]$expected}
        'LessThan' {return [double]$actual -lt [double]$expected}
        'Contains' {return ([string]$actual).IndexOf([string]$expected,[StringComparison]::OrdinalIgnoreCase) -ge 0}
        default {throw "Unsupported rule operator '$($Condition.operator)'."}
    }
}

function Get-WinFixRules {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$RulesPath,[Parameter(Mandatory)][string]$WindowsBuild)
    if (-not (Test-Path -LiteralPath $RulesPath)) {throw "Rules file not found: $RulesPath"}
    $catalog = Get-Content -LiteralPath $RulesPath -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
    if ([int]$catalog.schemaVersion -ne 1) {throw "Unsupported rules schema version '$($catalog.schemaVersion)'."}
    $knownIds = @{}
    foreach ($rule in @($catalog.rules)) {
        if ([string]$rule.id -notmatch '^[A-Za-z0-9._-]{1,80}$' -or
            [string]$rule.severity -notin @('Info','Low','Medium','High','Critical') -or
            @($rule.evidence).Count -eq 0) {
            throw 'Rules catalog contains an invalid rule id, severity, or empty evidence definition.'
        }
        if ($knownIds.ContainsKey([string]$rule.id)) {throw "Duplicate rule id '$($rule.id)'."}
        $knownIds[[string]$rule.id]=$true
        foreach ($condition in @($rule.evidence)) {
            if ([string]$condition.operator -notin @('Equal','NotEqual','GreaterThan','GreaterThanOrEqual','LessThan','Contains') -or
                [string]::IsNullOrWhiteSpace([string]$condition.path) -or $null -eq $condition.value) {
                throw "Rule '$($rule.id)' contains an unsupported or incomplete evidence condition."
            }
        }
    }
    $build = 0
    if (-not [int]::TryParse($WindowsBuild,[ref]$build)) {$build=0}
    @($catalog.rules | Where-Object {
        $minimum = [int]$_.minWindowsBuild
        $maximum = [int]$_.maxWindowsBuild
        if ($build -eq 0) {
            ($minimum -eq 0 -and $maximum -eq 0)
        } else {
            (($minimum -eq 0 -or $build -ge $minimum) -and ($maximum -eq 0 -or $build -le $maximum))
        }
    })
}

function Invoke-WinFixRules {
    [CmdletBinding()]
    param([Parameter(Mandatory)][object]$Context,[Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Rules)
    $matched = New-Object 'System.Collections.Generic.List[object]'
    foreach ($rule in $Rules) {
        $checks = @($rule.evidence)
        if ($checks.Count -eq 0) {continue}
        $allMatch = $true
        foreach ($check in $checks) {
            if (-not (Test-WinFixRuleCondition -Condition $check -Context $Context)) {$allMatch=$false;break}
        }
        if ($allMatch) {
            $matched.Add([pscustomobject]@{
                Id=$rule.id; Version=$rule.version; Area=$rule.area; Title=$rule.title
                Description=$rule.description; Severity=$rule.severity
                Evidence=@($checks | ForEach-Object {"$($_.path) $($_.operator) $($_.value)"})
                Recommendation=$rule.recommendation; Source='Local versioned rule'
            })
        }
    }
    return $matched.ToArray()
}

function Get-WinFixAnomalyFindings {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object]$CurrentSummary,
        [AllowEmptyCollection()][object[]]$Baseline = @(),
        [ValidateRange(3,1000)][int]$MinimumSamples = 3
    )
    $findings = New-Object 'System.Collections.Generic.List[object]'
    foreach ($metric in @('CpuLoadPercent','MemoryUsedPercent','DiskFreeGB')) {
        $values = @($Baseline | ForEach-Object {
            if ($null -ne $_.Summary.$metric) {[double]$_.Summary.$metric}
        })
        if ($values.Count -lt $MinimumSamples -or $null -eq $CurrentSummary.$metric) {continue}
        $median = [double](($values | Sort-Object)[[int][math]::Floor($values.Count / 2)])
        $current = [double]$CurrentSummary.$metric
        $anomalous = if ($metric -eq 'DiskFreeGB') {$current -lt ($median * 0.8)} else {$current -gt ($median * 1.5)}
        if ($anomalous) {
            $findings.Add([pscustomobject]@{
                Area='Behavioral anomaly'; Metric=$metric; Current=$current; BaselineMedian=$median
                BaselineSamples=$values.Count; Evidence="Current value $current differs from local baseline median $median across $($values.Count) samples."
                Severity='Medium'; Confidence=0.65
            })
        }
    }
    return $findings.ToArray()
}

function Get-WinFixCorrelations {
    [CmdletBinding()]
    param([AllowEmptyCollection()][object[]]$Events = @(),[AllowEmptyCollection()][object[]]$Findings = @())
    $correlations = New-Object 'System.Collections.Generic.List[object]'
    $groups = @($Events | Where-Object {$_.ProviderName -or $_.Id} | Group-Object {
        if ($_.ProviderName) {"$($_.ProviderName)|$($_.Id)"} else {"Event|$($_.Id)"}
    })
    foreach ($group in $groups) {
        if ($group.Count -lt 2) {continue}
        $times = @($group.Group | Where-Object {$_.TimeCreated} | ForEach-Object {[datetime]$_.TimeCreated} | Sort-Object)
        $spanMinutes = if ($times.Count -gt 1) {($times[-1] - $times[0]).TotalMinutes} else {0}
        $correlations.Add([pscustomobject]@{
            Type='Repeated event signature'; Signature=$group.Name; Count=$group.Count
            SpanMinutes=[math]::Round($spanMinutes,1); Evidence=@($group.Group | Select-Object -First 10 TimeCreated,LogName,Id,LevelDisplayName,ProviderName)
            Confidence=$(if ($group.Count -ge 5) {0.8} else {0.6})
            Claim='Repeated events share a provider/event signature; causality is not proven.'
        })
    }
    $repeatedEvents = $correlations.ToArray()
    foreach ($eventGroup in $repeatedEvents) {
        foreach ($finding in $Findings) {
            if ($finding.Area -eq 'Services' -and $eventGroup.Signature -match 'Service Control Manager') {
                $correlations.Add([pscustomobject]@{
                    Type='Temporal/component overlap'; Signature=$eventGroup.Signature
                    RelatedFinding=$finding.Finding; Evidence='A repeated Service Control Manager event and a service finding were both observed.'
                    Confidence=0.45; Claim='Possible relationship only; event details and service identity require manual verification.'
                })
            }
        }
    }
    return $correlations.ToArray()
}

function Get-WinFixRootCauseHypotheses {
    [CmdletBinding()]
    param(
        [AllowEmptyCollection()][object[]]$Findings = @(),
        [AllowEmptyCollection()][object[]]$Correlations = @(),
        [AllowEmptyCollection()][object[]]$Anomalies = @(),
        [AllowEmptyCollection()][object[]]$RuleFindings = @(),
        [AllowEmptyCollection()][object[]]$ContradictoryEvidence = @(),
        [string[]]$Symptoms = @()
    )
    $hypotheses = New-Object 'System.Collections.Generic.List[object]'
    foreach ($rule in $RuleFindings) {
        $hypotheses.Add([pscustomobject]@{
            Id="rule-$($rule.Id)"
            Title=$rule.Title
            Assessment='A condição declarativa corresponde às evidências recolhidas; relação causal com o sintoma não está estabelecida.'
            SupportingEvidence=@($rule.Evidence)
            ContradictoryEvidence=@()
            Alternatives=@('A condição observada pode ser incidental ao sintoma reportado.')
            Confidence=0.65
            ConfidenceLabel='Moderate'
            Status='Known condition observed; causality not established'
            RecommendedTests=@($rule.Recommendation)
        })
    }
    foreach ($finding in $Findings | Where-Object {$_.Severity -in @('Critical','High')}) {
        $hypotheses.Add([pscustomobject]@{
            Id="evidence-$($finding.Area)-$($hypotheses.Count)"
            Title=$finding.Finding
            Assessment='Observed condition; not necessarily the root cause.'
            SupportingEvidence=@($finding.Evidence)
            ContradictoryEvidence=@()
            Alternatives=@('The condition may be incidental to the reported symptom.')
            Confidence=0.5
            ConfidenceLabel='Limited'
            Status='Evidence observed; causality not established'
            RecommendedTests=@($finding.Recommendation)
        })
    }
    foreach ($anomaly in $Anomalies) {
        $hypotheses.Add([pscustomobject]@{
            Id="anomaly-$($anomaly.Metric)"
            Title="Change in $($anomaly.Metric) relative to local baseline"
            Assessment='Statistical deviation from this device local history; not a diagnosis.'
            SupportingEvidence=@($anomaly.Evidence)
            ContradictoryEvidence=@()
            Alternatives=@('Workload or sampling conditions may differ from the baseline period.')
            Confidence=[double]$anomaly.Confidence
            ConfidenceLabel='Moderate'
            Status='Anomaly detected; cause unknown'
            RecommendedTests=@('Repeat sampling during the reported symptom and compare with similar workload.')
        })
    }
    foreach ($correlation in $Correlations) {
        $hypotheses.Add([pscustomobject]@{
            Id="correlation-$($hypotheses.Count)"
            Title=[string]$correlation.Signature
            Assessment=[string]$correlation.Claim
            SupportingEvidence=@("Count: $($correlation.Count); span (minutes): $($correlation.SpanMinutes)")
            ContradictoryEvidence=@()
            Alternatives=@('Repeated signatures may be expected, unrelated, or caused by a common dependency.')
            Confidence=[double]$correlation.Confidence
            ConfidenceLabel='Limited'
            Status='Correlation only; causality not established'
            RecommendedTests=@('Inspect event details and align timestamps with the reported symptom and affected component.')
        })
    }
    if ($hypotheses.Count -eq 0) {
        $supporting = New-Object 'System.Collections.Generic.List[string]'
        foreach ($symptom in $Symptoms) {
            if ($symptom -match '(?i)slow|lent|latency|stutter|freeze|bloque|lento') {
                $supporting.Add("Reported symptom: $symptom")
            }
        }
        $hypotheses.Add([pscustomobject]@{
            Id='insufficient-evidence'
            Title='No root cause established from the available evidence'
            Assessment='Inconclusive. No measured evidence currently supports a specific cause.'
            SupportingEvidence=$supporting.ToArray()
            ContradictoryEvidence=@()
            Alternatives=@('Storage latency, driver delays, blocked services, power state, or an intermittent workload remain untested possibilities; none is confirmed.')
            Confidence=0.0
            ConfidenceLabel='Insufficient evidence'
            Status='Inconclusive'
            RecommendedTests=@('Collect a time-correlated performance trace during the symptom; inspect storage latency, process waits, driver events, power state and relevant service transitions.')
        })
    }
    foreach ($hypothesis in $hypotheses) {
        $contradictions = @($ContradictoryEvidence | Where-Object {
            $_.HypothesisId -eq $hypothesis.Id -or
            ($hypothesis.Id -like 'rule-*' -and $_.HypothesisId -eq $hypothesis.Id.Substring(5))
        })
        if ($contradictions.Count -gt 0) {
            $hypothesis.ContradictoryEvidence=@($contradictions | ForEach-Object {$_.Evidence})
            $hypothesis.Confidence=[math]::Min([double]$hypothesis.Confidence,0.25)
            $hypothesis.ConfidenceLabel='Conflicting evidence'
            $hypothesis.Status='Inconclusive; supporting and contradictory evidence present'
        }
    }
    return $hypotheses.ToArray()
}

function New-WinFixTroubleshootingPlan {
    [CmdletBinding()]
    param([Parameter(Mandatory)][object[]]$Hypotheses,[AllowEmptyCollection()][string[]]$CompletedTestIds = @())
    $completed = @{}
    foreach ($id in $CompletedTestIds) {$completed[$id]=$true}
    $candidates = New-Object 'System.Collections.Generic.List[object]'
    foreach ($hypothesis in $Hypotheses) {
        foreach ($recommendation in @($hypothesis.RecommendedTests)) {
            $key = ($recommendation -replace '[^a-zA-Z0-9]','').ToLowerInvariant()
            if ($completed.ContainsKey($key)) {continue}
            $candidates.Add([pscustomobject]@{Id=$key;HypothesisId=$hypothesis.Id;Test=$recommendation;Destructive=$false;Status='Recommended'})
        }
    }
    @($candidates | Group-Object Id | ForEach-Object {$_.Group | Select-Object -First 1})
}

function Add-WinFixTestResult {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object[]]$Plan,
        [Parameter(Mandatory)][string]$TestId,
        [Parameter(Mandatory)][ValidateSet('Confirmed','NotConfirmed','Inconclusive')][string]$Result,
        [Parameter(Mandatory)][string]$Evidence
    )
    $test = @($Plan | Where-Object {$_.Id -eq $TestId}) | Select-Object -First 1
    if (-not $test) {throw "Test '$TestId' is not in the current troubleshooting plan."}
    if ($test.Status -ne 'Recommended') {throw "Test '$TestId' has already been completed; repeated tests are blocked."}
    $test.Status = 'Completed'
    $test | Add-Member -NotePropertyName Result -NotePropertyValue $Result -Force
    $test | Add-Member -NotePropertyName Evidence -NotePropertyValue $Evidence -Force
    $test | Add-Member -NotePropertyName CompletedAt -NotePropertyValue (Get-Date).ToString('o') -Force
    return $test
}

function Get-WinFixDiagnosticPlugins {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$PluginDirectory,[Parameter(Mandatory)][string]$WindowsBuild)
    if (-not (Test-Path -LiteralPath $PluginDirectory)) {return @()}
    $build = 0
    if (-not [int]::TryParse($WindowsBuild,[ref]$build)) {$build=0}
    $plugins = New-Object 'System.Collections.Generic.List[object]'
    foreach ($moduleFile in Get-ChildItem -LiteralPath $PluginDirectory -Filter '*.psm1' -File -ErrorAction Stop) {
        $manifestPath = [IO.Path]::ChangeExtension($moduleFile.FullName,'.json')
        if (-not (Test-Path -LiteralPath $manifestPath)) {throw "Plugin manifest missing: $manifestPath"}
        $manifest = Get-Content -LiteralPath $manifestPath -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
        if ([int]$manifest.schemaVersion -ne 1 -or $manifest.entryPoint -ne 'Invoke-WinFixDiagnosticPlugin') {
            throw "Plugin '$($moduleFile.Name)' has an unsupported manifest or entry point."
        }
        $minimum = [int]$manifest.minimumWindowsBuild
        $maximum = [int]$manifest.maximumWindowsBuild
        $compatible = if ($build -eq 0) {$minimum -eq 0 -and $maximum -eq 0} else {($minimum -eq 0 -or $build -ge $minimum) -and ($maximum -eq 0 -or $build -le $maximum)}
        $plugins.Add([pscustomobject]@{Name=[string]$manifest.name;Version=[string]$manifest.version;Path=$moduleFile.FullName;EntryPoint=[string]$manifest.entryPoint;Compatible=$compatible;MinimumWindowsBuild=$minimum;MaximumWindowsBuild=$maximum})
    }
    return $plugins.ToArray()
}

function Invoke-WinFixDiagnosticPlugins {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$PluginDirectory,[Parameter(Mandatory)][string]$WindowsBuild,[Parameter(Mandatory)][object]$Context)
    $results = New-Object 'System.Collections.Generic.List[object]'
    foreach ($plugin in Get-WinFixDiagnosticPlugins -PluginDirectory $PluginDirectory -WindowsBuild $WindowsBuild) {
        if (-not $plugin.Compatible) {
            $results.Add([pscustomobject]@{Plugin=$plugin.Name;Status='Incompatible';Error='Plugin build constraints do not match this Windows version.'})
            continue
        }
        $module = Import-Module -Name $plugin.Path -PassThru -Force -ErrorAction Stop
        try {
            $pluginResults = & $module {param($EntryPoint,$PluginContext) & $EntryPoint -Context $PluginContext} $plugin.EntryPoint $Context
            foreach ($item in @($pluginResults)) {
                foreach ($required in @('Area','Finding','Severity','Evidence','Recommendation')) {
                    if (-not $item.PSObject.Properties[$required]) {throw "Plugin '$($plugin.Name)' result is missing required field '$required'."}
                }
                $results.Add([pscustomobject]@{Plugin=$plugin.Name;Status='Completed';Result=$item})
            }
        } catch {
            $results.Add([pscustomobject]@{Plugin=$plugin.Name;Status='Failed';Error=$_.Exception.Message})
        } finally {
            Remove-Module -ModuleInfo $module -Force -ErrorAction SilentlyContinue
        }
    }
    return $results.ToArray()
}

function Invoke-WinFixUniversalAnalysis {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object]$Context,
        [Parameter(Mandatory)][string]$RulesPath,
        [Parameter(Mandatory)][string]$KnowledgePath,
        [Parameter(Mandatory)][string]$PluginDirectory,
        [string[]]$Symptoms = @(),
        [AllowEmptyCollection()][object[]]$ContradictoryEvidence = @(),
        [switch]$AllowPlugins,
        [AllowEmptyCollection()][object[]]$Baseline = @()
    )
    $summary = $Context.Summary
    $rules = @(Get-WinFixRules -RulesPath $RulesPath -WindowsBuild ([string]$summary.BuildNumber))
    $ruleFindings = @(Invoke-WinFixRules -Context $Context -Rules $rules)
    $knowledge = Get-WinFixKnowledgeEntries -Path $KnowledgePath -WindowsVersion ([string]$summary.Version) -WindowsBuild ([string]$summary.BuildNumber)
    $anomalies = @(Get-WinFixAnomalyFindings -CurrentSummary $summary -Baseline $Baseline)
    $events = @($Context.Events)
    $correlations = @(Get-WinFixCorrelations -Events $events -Findings @($Context.Findings))
    $hypotheses = @(Get-WinFixRootCauseHypotheses -Findings @($Context.Findings) -Correlations $correlations -Anomalies $anomalies -RuleFindings $ruleFindings -ContradictoryEvidence $ContradictoryEvidence -Symptoms $Symptoms)
    $pluginResults = @()
    if ($AllowPlugins) {
        $pluginResults = @(Invoke-WinFixDiagnosticPlugins -PluginDirectory $PluginDirectory -WindowsBuild ([string]$summary.BuildNumber) -Context $Context)
    }
    $knowledgeMatches = New-Object 'System.Collections.Generic.List[object]'
    foreach ($entry in $knowledge.Active) {
        $terms = @(([string]$entry.title -split '\s+' | Where-Object {$_.Length -ge 5}))
        $matches = @($Symptoms | Where-Object {
            $symptom = $_
            @($terms | Where-Object {$symptom.IndexOf($_,[StringComparison]::OrdinalIgnoreCase) -ge 0}).Count -gt 0
        })
        if ($matches.Count -gt 0) {$knowledgeMatches.Add([pscustomobject]@{Entry=$entry;MatchedSymptoms=$matches;Assessment='Compatibility and expiry checked; symptoms are text-matched, not proof of cause.'})}
    }
    $unknownSymptoms = @($Symptoms | Where-Object {
        $symptom = $_
        -not @($knowledgeMatches | Where-Object {$_.MatchedSymptoms -contains $symptom}).Count
    })
    $status = if ($hypotheses.Count -eq 1 -and $hypotheses[0].Status -eq 'Inconclusive') {'Inconclusive'} else {'Evidence available'}
    [pscustomobject]@{
        SchemaVersion=1
        CreatedAt=(Get-Date).ToString('o')
        Status=$status
        Symptoms=@($Symptoms)
        RuleFindings=$ruleFindings
        KnowledgeMatches=$knowledgeMatches.ToArray()
        ExcludedKnowledgeCount=@($knowledge.Excluded).Count
        Anomalies=$anomalies
        Correlations=$correlations
        Hypotheses=$hypotheses
        RecommendedTests=@(New-WinFixTroubleshootingPlan -Hypotheses $hypotheses)
        UnknownSymptoms=$unknownSymptoms
        PluginResults=$pluginResults
        CollectionErrors=@($Context.CollectionErrors)
        EvidenceSummary=[pscustomobject]@{
            FindingsCount=@($Context.Findings).Count
            EventsCount=$events.Count
            HardwareDevicesWithProblems=@($Context.Hardware.ProblemDevices).Count
            DataCompleteness=if (@($Context.CollectionErrors).Count -eq 0) {'No collection errors reported'} else {'Partial; review collection errors'}
        }
    }
}

Export-ModuleMember -Function Invoke-WinFixUniversalAnalysis,Get-WinFixRules,Invoke-WinFixRules,Get-WinFixAnomalyFindings,Get-WinFixCorrelations,Get-WinFixRootCauseHypotheses,New-WinFixTroubleshootingPlan,Add-WinFixTestResult,Get-WinFixDiagnosticPlugins,Invoke-WinFixDiagnosticPlugins
