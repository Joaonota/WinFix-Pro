function Get-WinFixInstalledApplications {
    $paths = @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*'
    )
    $applications = New-Object 'System.Collections.Generic.List[object]'
    foreach ($path in $paths) {
        foreach ($app in @(Get-ItemProperty -Path $path -ErrorAction SilentlyContinue)) {
            if ($app.DisplayName) {
                $applications.Add([pscustomobject]@{
                    Name=[string]$app.DisplayName
                    Version=[string]$app.DisplayVersion
                    Publisher=[string]$app.Publisher
                    InstallDate=[string]$app.InstallDate
                })
            }
        }
    }
    return @($applications | Sort-Object Name -Unique)
}

function Get-WinFixStartupInventory {
    $items = New-Object 'System.Collections.Generic.List[object]'
    foreach ($path in @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run',
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'
    )) {
        if (Test-Path -LiteralPath $path) {
            $values = Get-ItemProperty -LiteralPath $path -ErrorAction Stop
            foreach ($property in $values.PSObject.Properties | Where-Object {$_.Name -notmatch '^PS'}) {
                $items.Add([pscustomobject]@{Source=$path;Name=$property.Name;Command=[string]$property.Value})
            }
        }
    }
    return $items.ToArray()
}

function Get-WinFixUniversalBaseline {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$HistoryPath,[ValidateRange(3,365)][int]$MaximumSamples=60)
    if (-not (Test-Path -LiteralPath $HistoryPath)) {return @()}
    $samples = New-Object 'System.Collections.Generic.List[object]'
    foreach ($line in Get-Content -LiteralPath $HistoryPath -ErrorAction Stop) {
        if ([string]::IsNullOrWhiteSpace($line)) {continue}
        try {
            $sample = $line | ConvertFrom-Json -ErrorAction Stop
            if ($sample.Summary -and $sample.Summary.CollectedAt) {$samples.Add($sample)}
        } catch {
            throw "Could not read local diagnostic baseline from '$HistoryPath': $($_.Exception.Message)"
        }
    }
    return @($samples | Select-Object -Last $MaximumSamples)
}

function Get-WinFixDiagnosticProfile {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {throw "Diagnostic profile not found: $Path"}
    $profile = Get-Content -LiteralPath $Path -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
    if ([int]$profile.schemaVersion -ne 1 -or [string]::IsNullOrWhiteSpace([string]$profile.name)) {
        throw 'Unsupported or incomplete diagnostic profile schema.'
    }
    $allowedAreas = @('OperatingSystem','Hardware','Performance','Network','Applications','Security','Services','UserEnvironment')
    foreach ($area in @($profile.enabledAreas)) {
        if ($area -notin $allowedAreas) {throw "Unknown diagnostic area '$area' in profile '$($profile.name)'."}
    }
    if (@($profile.enabledAreas) -notcontains 'OperatingSystem') {throw 'Diagnostic profiles must include OperatingSystem for version and compatibility checks.'}
    $lookback = 0
    $maximum = 0
    if (-not [int]::TryParse([string]$profile.eventLookbackDays,[ref]$lookback) -or $lookback -lt 1 -or $lookback -gt 30) {
        throw "Profile '$($profile.name)' eventLookbackDays must be between 1 and 30."
    }
    if (-not [int]::TryParse([string]$profile.maxEventsPerLog,[ref]$maximum) -or $maximum -lt 10 -or $maximum -gt 1000) {
        throw "Profile '$($profile.name)' maxEventsPerLog must be between 10 and 1000."
    }
    return $profile
}

function Get-WinFixUniversalEvidence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Root,
        [ValidateRange(1,30)][int]$EventLookbackDays,
        [ValidateRange(10,1000)][int]$MaximumEventsPerLog
    )
    $profile = Get-WinFixDiagnosticProfile -Path (Join-Path $Root 'Profiles\default.json')
    if (-not $PSBoundParameters.ContainsKey('EventLookbackDays')) {$EventLookbackDays=[int]$profile.eventLookbackDays}
    if (-not $PSBoundParameters.ContainsKey('MaximumEventsPerLog')) {$MaximumEventsPerLog=[int]$profile.maxEventsPerLog}
    $errors = New-Object 'System.Collections.Generic.List[object]'
    $summary = $null
    $findings = @()
    $hardware = $null
    $events = @()
    $services = @()
    $processes = @()
    $network = @()
    $security = @()
    $applications = @()
    $startup = @()
    $drivers = @()
    $profiles = @()
    $tasks = @()
    $updates = @()
    $physicalDisks = @()
    $networkConfiguration = @()

    foreach ($check in @(
        [pscustomobject]@{Name='Summary';Area='OperatingSystem';Action={Get-WinFixSystemSummary}},
        [pscustomobject]@{Name='Full diagnostics';Area='OperatingSystem';Action={Invoke-WinFixDiagnostics}},
        [pscustomobject]@{Name='Hardware';Area='Hardware';Action={Get-WinFixHardwareInventory}},
        [pscustomobject]@{Name='Events';Area='OperatingSystem';Action={
            $all = New-Object 'System.Collections.Generic.List[object]'
            foreach ($log in @('System','Application')) {
                $records = @(Get-WinFixRecentEvents -LogName $log -Hours ($EventLookbackDays * 24) -Maximum $MaximumEventsPerLog -ErrorAction Stop)
                foreach ($record in $records) {$all.Add($record)}
            }
            $all.ToArray()
        }},
        [pscustomobject]@{Name='Services';Area='Services';Action={@(Get-WinFixServiceInventory)}},
        [pscustomobject]@{Name='Processes';Area='Performance';Action={@(Get-WinFixTopProcesses -Count 30)}},
        [pscustomobject]@{Name='Network tests';Area='Network';Action={@(Get-WinFixNetworkDiagnostic)}},
        [pscustomobject]@{Name='Security';Area='Security';Action={@(Get-WinFixSecurityStatus)}},
        [pscustomobject]@{Name='Applications';Area='Applications';Action={@(Get-WinFixInstalledApplications)}},
        [pscustomobject]@{Name='Startup';Area='Performance';Action={@(Get-WinFixStartupInventory)}},
        [pscustomobject]@{Name='Drivers';Area='Hardware';Action={@(Get-CimInstance Win32_PnPSignedDriver -ErrorAction Stop | Select-Object DeviceName,DriverVersion,DriverDate,Manufacturer,IsSigned,DeviceID)}},
        [pscustomobject]@{Name='User profiles';Area='UserEnvironment';Action={@(Get-CimInstance Win32_UserProfile -ErrorAction Stop | Select-Object LocalPath,Loaded,Special,LastUseTime)}},
        [pscustomobject]@{Name='Scheduled tasks';Area='Services';Action={@(Get-ScheduledTask -ErrorAction Stop | Select-Object TaskPath,TaskName,State)}},
        [pscustomobject]@{Name='Installed updates';Area='OperatingSystem';Action={@(Get-HotFix -ErrorAction Stop | Sort-Object InstalledOn -Descending | Select-Object -First 100 HotFixID,Description,InstalledOn)}},
        [pscustomobject]@{Name='Physical disks';Area='Hardware';Action={if (Get-Command Get-PhysicalDisk -ErrorAction SilentlyContinue) {@(Get-PhysicalDisk -ErrorAction Stop | Select-Object FriendlyName,MediaType,HealthStatus,OperationalStatus,Size)} else {@()} }},
        [pscustomobject]@{Name='Network configuration';Area='Network';Action={@(Get-NetIPConfiguration -ErrorAction Stop | Select-Object InterfaceAlias,InterfaceDescription,NetProfile,IPv4Address,IPv4DefaultGateway,DNSServer)}}
    )) {
        if (@($profile.enabledAreas) -notcontains $check.Area) {continue}
        try {
            $result = & $check.Action
            switch ($check.Name) {
                'Summary' {$summary=$result}
                'Full diagnostics' {$findings=@($result.Findings)}
                'Hardware' {$hardware=$result}
                'Events' {$events=@($result)}
                'Services' {$services=@($result)}
                'Processes' {$processes=@($result)}
                'Network tests' {$network=@($result)}
                'Security' {$security=@($result)}
                'Applications' {$applications=@($result)}
                'Startup' {$startup=@($result)}
                'Drivers' {$drivers=@($result)}
                'User profiles' {$profiles=@($result)}
                'Scheduled tasks' {$tasks=@($result)}
                'Installed updates' {$updates=@($result)}
                'Physical disks' {$physicalDisks=@($result)}
                'Network configuration' {$networkConfiguration=@($result)}
            }
        } catch {
            $errors.Add([pscustomobject]@{Check=$check.Name;Error=$_.Exception.Message;Time=(Get-Date).ToString('o')})
        }
    }
    if (-not $summary) {throw 'The required OS summary could not be collected; universal analysis cannot continue.'}
    $historyPath = Join-Path $env:LOCALAPPDATA 'WinFixPro\0.2\History\UniversalMetrics.jsonl'
    $baseline = Get-WinFixUniversalBaseline -HistoryPath $historyPath
    $sample = [pscustomobject]@{
        SchemaVersion=1
        Summary=$summary
        CreatedAt=(Get-Date).ToString('o')
    }
    $historyDirectory = Split-Path -Parent $historyPath
    if (-not (Test-Path -LiteralPath $historyDirectory)) {
        New-Item -ItemType Directory -Path $historyDirectory -Force -ErrorAction Stop | Out-Null
    }
    $sample | ConvertTo-Json -Depth 4 -Compress | Add-Content -LiteralPath $historyPath -Encoding UTF8 -ErrorAction Stop
    [pscustomobject]@{
        Summary=$summary
        Findings=$findings
        Hardware=$hardware
        Events=$events
        Services=$services
        Processes=$processes
        Network=$network
        NetworkConfiguration=$networkConfiguration
        Security=$security
        Applications=$applications
        Startup=$startup
        Drivers=$drivers
        UserProfiles=$profiles
        ScheduledTasks=$tasks
        InstalledUpdates=$updates
        PhysicalDisks=$physicalDisks
        CollectionErrors=$errors.ToArray()
        Baseline=$baseline
        Profile=$profile.name
        SchemaVersion=1
        CreatedAt=(Get-Date).ToString('o')
    }
}

Export-ModuleMember -Function Get-WinFixInstalledApplications,Get-WinFixStartupInventory,Get-WinFixUniversalBaseline,Get-WinFixDiagnosticProfile,Get-WinFixUniversalEvidence
