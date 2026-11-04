function Get-WinFixHealthScore {
    [CmdletBinding()]
    param([Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Findings)
    $weights = @{Info=0; Low=5; Medium=12; High=22; Critical=35}
    $penalty = 0
    foreach ($finding in $Findings) {
        if ($weights.ContainsKey([string]$finding.Severity)) {
            $penalty += $weights[[string]$finding.Severity]
        }
    }
    $score = [math]::Max(0, [math]::Min(100, 100 - $penalty))
    $rating = if ($score -ge 90) {'Excellent'} elseif ($score -ge 75) {'Good'} elseif ($score -ge 50) {'Needs Attention'} else {'Critical'}
    [pscustomobject]@{Score=[int]$score; Rating=$rating; Factors=@($Findings | Where-Object {$_.Severity -ne 'Info'})}
}

Export-ModuleMember -Function Get-WinFixHealthScore
