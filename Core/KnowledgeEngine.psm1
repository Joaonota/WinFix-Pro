function Read-WinFixJsonStore {
    param([Parameter(Mandatory)][string]$Path,[Parameter(Mandatory)][int]$SchemaVersion)
    if (-not (Test-Path -LiteralPath $Path)) {
        return [pscustomobject]@{schemaVersion=$SchemaVersion;entries=@()}
    }
    $store = Get-Content -LiteralPath $Path -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
    if ([int]$store.schemaVersion -ne $SchemaVersion) {
        throw "Unsupported schema version '$($store.schemaVersion)' in '$Path'; expected $SchemaVersion."
    }
    if ($null -eq $store.entries) {throw "The store '$Path' does not contain an entries array."}
    return $store
}

function Write-WinFixJsonStore {
    param([Parameter(Mandatory)][string]$Path,[Parameter(Mandatory)][object]$Store)
    $directory = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Path $directory -Force -ErrorAction Stop | Out-Null
    }
    $temporaryPath = "$Path.$([guid]::NewGuid().ToString('N')).tmp"
    try {
        $Store | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $temporaryPath -Encoding UTF8 -ErrorAction Stop
        Move-Item -LiteralPath $temporaryPath -Destination $Path -Force -ErrorAction Stop
    } finally {
        if (Test-Path -LiteralPath $temporaryPath) {
            Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
        }
    }
}

function Test-WinFixSourceUri {
    param([Parameter(Mandatory)][string]$Source,[switch]$RequireHttps)
    $uri = $null
    $valid = [uri]::TryCreate($Source,[System.UriKind]::Absolute,[ref]$uri)
    if (-not $valid -or $uri.Scheme -notin $(if ($RequireHttps) {@('https')} else {@('https','http')})) {return $false}
    return (-not $uri.UserInfo -and -not $uri.Query)
}

function Get-WinFixKnowledgeEntries {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$WindowsVersion,
        [Parameter(Mandatory)][string]$WindowsBuild,
        [datetime]$At = (Get-Date)
    )
    $store = Read-WinFixJsonStore -Path $Path -SchemaVersion 1
    $build = 0
    if (-not [int]::TryParse($WindowsBuild,[ref]$build)) {$build = 0}
    $active = New-Object 'System.Collections.Generic.List[object]'
    $expired = New-Object 'System.Collections.Generic.List[object]'
    foreach ($entry in @($store.entries)) {
        $expires = [datetime]::MaxValue
        if ($entry.expiresAt -and -not [datetime]::TryParse([string]$entry.expiresAt,[ref]$expires)) {
            throw "Knowledge entry '$($entry.id)' has an invalid expiresAt timestamp."
        }
        if ([string]$entry.reviewStatus -eq 'Approved') {
            if (-not $entry.expiresAt -or -not $entry.title -or -not $entry.summary -or -not $entry.verification -or @($entry.sources).Count -eq 0) {
                throw "Approved knowledge entry '$($entry.id)' is missing expiry, source, summary, title, or verification steps."
            }
            foreach ($source in @($entry.sources)) {
                if (-not (Test-WinFixSourceUri -Source ([string]$source) -RequireHttps)) {
                    throw "Approved knowledge entry '$($entry.id)' contains a non-HTTPS source."
                }
            }
        }
        $minimum = 0
        $maximum = 0
        if ($entry.minimumWindowsBuild -and -not [int]::TryParse([string]$entry.minimumWindowsBuild,[ref]$minimum)) {
            throw "Knowledge entry '$($entry.id)' has an invalid minimum Windows build."
        }
        if ($entry.maximumWindowsBuild -and -not [int]::TryParse([string]$entry.maximumWindowsBuild,[ref]$maximum)) {
            throw "Knowledge entry '$($entry.id)' has an invalid maximum Windows build."
        }
        if ($minimum -gt 0 -and $maximum -gt 0 -and $minimum -gt $maximum) {
            throw "Knowledge entry '$($entry.id)' has an inverted Windows build range."
        }
        $compatibleVersion = -not $entry.windowsVersion -or [string]$entry.windowsVersion -eq $WindowsVersion
        $compatibleBuild = $build -eq 0 -or (($minimum -eq 0 -or $build -ge $minimum) -and ($maximum -eq 0 -or $build -le $maximum))
        if ($expires -le $At -or -not $compatibleVersion -or -not $compatibleBuild -or [string]$entry.reviewStatus -ne 'Approved') {
            $expired.Add($entry)
        } else {
            $active.Add($entry)
        }
    }
    [pscustomobject]@{Active=$active.ToArray();Excluded=$expired.ToArray();SchemaVersion=[int]$store.schemaVersion}
}

function Add-WinFixCase {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$CasesPath,
        [Parameter(Mandatory)][string]$Symptoms,
        [Parameter(Mandatory)][string]$Outcome,
        [string]$EvidenceSummary = '',
        [string]$Hypotheses = '',
        [string]$Tests = '',
        [ValidateSet('Open','Investigating','Resolved','Inconclusive')][string]$Status = 'Open',
        [string]$WindowsVersion = '',
        [string]$WindowsBuild = '',
        [string[]]$Sources = @()
    )
    if ([string]::IsNullOrWhiteSpace($Symptoms) -or [string]::IsNullOrWhiteSpace($Outcome)) {
        throw 'Case symptoms and outcome/status notes are required.'
    }
    if ($Status -eq 'Resolved' -and [string]::IsNullOrWhiteSpace($Tests)) {
        throw 'A resolved case must include tests performed and verification results.'
    }
    foreach ($source in $Sources) {
        if (-not (Test-WinFixSourceUri -Source $source)) {
            throw "Invalid case source URI '$source'."
        }
    }
    $store = Read-WinFixJsonStore -Path $CasesPath -SchemaVersion 1
    $safeText = {
        param([string]$Text)
        $safe = $Text.Trim()
        foreach ($sensitive in @($env:USERPROFILE,$env:USERNAME,$env:COMPUTERNAME) | Where-Object {$_}) {
            $pattern = if ($sensitive -match '[\\/]') {
                [regex]::Escape($sensitive)
            } else {
                '(?<![\w-])' + [regex]::Escape($sensitive) + '(?![\w-])'
            }
            $safe = [regex]::Replace($safe,$pattern,'[REDACTED]',[System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
        }
        return $safe
    }
    $case = [pscustomobject]@{
        id = [guid]::NewGuid().ToString()
        createdAt = (Get-Date).ToString('o')
        updatedAt = (Get-Date).ToString('o')
        symptoms = & $safeText $Symptoms
        evidenceSummary = & $safeText $EvidenceSummary
        hypotheses = & $safeText $Hypotheses
        tests = & $safeText $Tests
        outcome = & $safeText $Outcome
        status = $Status
        windowsVersion = $WindowsVersion
        windowsBuild = $WindowsBuild
        sources = @($Sources)
        reviewStatus = 'Unreviewed'
    }
    $store.entries = @($store.entries) + @($case)
    Write-WinFixJsonStore -Path $CasesPath -Store $store
    return $case
}

function ConvertTo-WinFixKnowledgeCandidate {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$CasesPath,
        [Parameter(Mandatory)][string]$KnowledgePath,
        [Parameter(Mandatory)][string]$CaseId,
        [Parameter(Mandatory)][string]$ReviewedBy,
        [Parameter(Mandatory)][string]$ExpiresAt
    )
    if ([string]::IsNullOrWhiteSpace($ReviewedBy)) {throw 'A reviewer name/role is required.'}
    $expiry = [datetime]::MinValue
    if (-not [datetime]::TryParse($ExpiresAt,[ref]$expiry) -or $expiry -le (Get-Date)) {
        throw 'Knowledge expiry must be a valid future timestamp.'
    }
    $cases = Read-WinFixJsonStore -Path $CasesPath -SchemaVersion 1
    $case = @($cases.entries | Where-Object {$_.id -eq $CaseId -and $_.status -eq 'Resolved'}) | Select-Object -First 1
    if (-not $case) {throw 'Only a resolved case can be proposed as knowledge.'}
    if (-not $case.outcome -or -not $case.tests) {throw 'A resolved case must include its outcome and verification tests.'}
    $knowledge = Read-WinFixJsonStore -Path $KnowledgePath -SchemaVersion 1
    $candidate = [pscustomobject]@{
        id = "case-$($case.id)"
        createdAt = (Get-Date).ToString('o')
        expiresAt = $expiry.ToString('o')
        windowsVersion = $case.windowsVersion
        minimumWindowsBuild = 0
        maximumWindowsBuild = 0
        title = $case.symptoms
        summary = $case.outcome
        verification = $case.tests
        sources = @($case.sources)
        reviewStatus = 'PendingReview'
        reviewedBy = $ReviewedBy.Trim()
        confidence = 'Unvalidated'
    }
    $knowledge.entries = @($knowledge.entries | Where-Object {$_.id -ne $candidate.id}) + @($candidate)
    Write-WinFixJsonStore -Path $KnowledgePath -Store $knowledge
    return $candidate
}

function Set-WinFixKnowledgeReview {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$KnowledgePath,
        [Parameter(Mandatory)][string]$EntryId,
        [Parameter(Mandatory)][ValidateSet('Approved','Rejected')][string]$ReviewStatus,
        [Parameter(Mandatory)][string]$ReviewedBy,
        [Parameter(Mandatory)][string]$ReviewNotes
    )
    if ([string]::IsNullOrWhiteSpace($ReviewedBy) -or [string]::IsNullOrWhiteSpace($ReviewNotes)) {
        throw 'Reviewer and review notes are required.'
    }
    $store = Read-WinFixJsonStore -Path $KnowledgePath -SchemaVersion 1
    $entry = @($store.entries | Where-Object {$_.id -eq $EntryId -and $_.reviewStatus -eq 'PendingReview'}) | Select-Object -First 1
    if (-not $entry) {throw "Pending knowledge candidate '$EntryId' was not found."}
    if ($ReviewStatus -eq 'Approved' -and @($entry.sources).Count -eq 0) {
        throw 'Knowledge cannot be approved without at least one source URI.'
    }
    if ($ReviewStatus -eq 'Approved') {
        foreach ($source in @($entry.sources)) {
            if (-not (Test-WinFixSourceUri -Source ([string]$source) -RequireHttps)) {
                throw 'Only absolute HTTPS source URLs can be approved as knowledge.'
            }
        }
    }
    if ($ReviewStatus -eq 'Approved' -and (-not $entry.verification -or -not $entry.summary)) {
        throw 'Knowledge cannot be approved without a resolution summary and verification steps.'
    }
    $entry.reviewStatus = $ReviewStatus
    $entry.reviewedBy = $ReviewedBy.Trim()
    $entry | Add-Member -NotePropertyName reviewNotes -NotePropertyValue $ReviewNotes.Trim() -Force
    $entry | Add-Member -NotePropertyName reviewedAt -NotePropertyValue (Get-Date).ToString('o') -Force
    if ($ReviewStatus -eq 'Approved') {$entry.confidence='Technician reviewed'}
    Write-WinFixJsonStore -Path $KnowledgePath -Store $store
    return $entry
}

function Import-WinFixKnowledgeBundle {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$SourcePath,
        [Parameter(Mandatory)][string]$KnowledgePath,
        [Parameter(Mandatory)][string]$ImportedBy
    )
    if (-not (Test-Path -LiteralPath $SourcePath -PathType Leaf)) {throw "Knowledge bundle not found: $SourcePath"}
    if ((Get-Item -LiteralPath $SourcePath -ErrorAction Stop).Length -gt 2MB) {throw 'Knowledge bundles are limited to 2 MB.'}
    if ([string]::IsNullOrWhiteSpace($ImportedBy)) {throw 'Importer role is required.'}
    $bundle = Get-Content -LiteralPath $SourcePath -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
    if ([int]$bundle.schemaVersion -ne 1 -or $null -eq $bundle.entries) {throw 'Unsupported or incomplete knowledge bundle schema.'}
    $store = Read-WinFixJsonStore -Path $KnowledgePath -SchemaVersion 1
    $knownIds = @{}
    foreach ($entry in @($store.entries)) {$knownIds[[string]$entry.id]=$true}
    $imported = New-Object 'System.Collections.Generic.List[object]'
    foreach ($candidate in @($bundle.entries)) {
        $id = [string]$candidate.id
        $expires = [datetime]::MinValue
        if ($id -notmatch '^[A-Za-z0-9._-]{1,80}$' -or
            [string]::IsNullOrWhiteSpace([string]$candidate.title) -or
            [string]::IsNullOrWhiteSpace([string]$candidate.summary) -or
            [string]::IsNullOrWhiteSpace([string]$candidate.verification) -or
            -not [datetime]::TryParse([string]$candidate.expiresAt,[ref]$expires) -or $expires -le (Get-Date)) {
            throw "Knowledge bundle contains an invalid or expired entry '$id'."
        }
        if ($knownIds.ContainsKey($id)) {continue}
        $sources = @($candidate.sources)
        if ($sources.Count -eq 0) {throw "Knowledge entry '$id' has no source URI."}
        foreach ($source in $sources) {
            if (-not (Test-WinFixSourceUri -Source ([string]$source) -RequireHttps)) {
                throw "Knowledge entry '$id' has a non-HTTPS or invalid source URI."
            }
        }
        $minimum = 0
        $maximum = 0
        if ($candidate.minimumWindowsBuild -and -not [int]::TryParse([string]$candidate.minimumWindowsBuild,[ref]$minimum)) {
            throw "Knowledge entry '$id' has an invalid minimum Windows build."
        }
        if ($candidate.maximumWindowsBuild -and -not [int]::TryParse([string]$candidate.maximumWindowsBuild,[ref]$maximum)) {
            throw "Knowledge entry '$id' has an invalid maximum Windows build."
        }
        if ($minimum -gt 0 -and $maximum -gt 0 -and $minimum -gt $maximum) {
            throw "Knowledge entry '$id' has an inverted Windows build range."
        }
        $entry = [pscustomobject]@{
            id=$id
            createdAt=(Get-Date).ToString('o')
            expiresAt=$expires.ToString('o')
            windowsVersion=[string]$candidate.windowsVersion
            minimumWindowsBuild=$minimum
            maximumWindowsBuild=$maximum
            title=([string]$candidate.title).Trim()
            summary=([string]$candidate.summary).Trim()
            verification=([string]$candidate.verification).Trim()
            sources=$sources
            reviewStatus='PendingReview'
            reviewedBy=$ImportedBy.Trim()
            confidence='Unvalidated'
            importedFrom=[IO.Path]::GetFileName($SourcePath)
        }
        $imported.Add($entry)
        $knownIds[$id]=$true
    }
    if ($imported.Count -gt 0) {
        $store.entries=@($store.entries)+$imported.ToArray()
        Write-WinFixJsonStore -Path $KnowledgePath -Store $store
    }
    return $imported.ToArray()
}

Export-ModuleMember -Function Get-WinFixKnowledgeEntries,Add-WinFixCase,ConvertTo-WinFixKnowledgeCandidate,Set-WinFixKnowledgeReview,Import-WinFixKnowledgeBundle
