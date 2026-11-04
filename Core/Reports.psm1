function Export-WinFixReport {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object]$Data,
        [Parameter(Mandatory)][ValidateSet('HTML','JSON','CSV','TXT')][string]$Format,
        [string]$ReportsDirectory = (Join-Path $PSScriptRoot '..\Reports')
    )
    $dateFolder = Join-Path $ReportsDirectory (Get-Date -Format 'yyyy-MM-dd')
    if (-not (Test-Path -LiteralPath $dateFolder)) { New-Item -ItemType Directory -Path $dateFolder -Force -ErrorAction Stop | Out-Null }
    $path = Join-Path $dateFolder ("WinFix-{0}-{1}.{2}" -f $env:COMPUTERNAME,(Get-Date -Format 'yyyyMMdd-HHmmss'),$Format.ToLowerInvariant())
    switch ($Format) {
        'JSON' { $Data | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $path -Encoding UTF8 -ErrorAction Stop }
        'CSV' {
            if ($Data.Findings) {$Data.Findings | Export-Csv -LiteralPath $path -NoTypeInformation -Encoding UTF8 -ErrorAction Stop}
            else {$Data | Export-Csv -LiteralPath $path -NoTypeInformation -Encoding UTF8 -ErrorAction Stop}
        }
        'TXT' {
            $lines = @("WinFix Pro diagnostic report","Computer: $env:COMPUTERNAME","Generated: $((Get-Date).ToString('o'))")
            if ($Data.Summary) {$lines += "Windows: $($Data.Summary.Caption) $($Data.Summary.Version)"; $lines += "Health score: $($Data.Health.Score) ($($Data.Health.Rating))"}
            foreach ($finding in @($Data.Findings)) {$lines += "[$($finding.Severity)] $($finding.Area): $($finding.Finding) | Evidence: $($finding.Evidence) | Recommendation: $($finding.Recommendation)"}
            $lines | Set-Content -LiteralPath $path -Encoding UTF8 -ErrorAction Stop
        }
        'HTML' {
            $summary = if ($Data.Summary) {$Data.Summary.Caption} else {'Not available'}
            $rows = if ($Data.Findings) {$Data.Findings | ConvertTo-Html -Fragment} else {'<p>No findings in this report.</p>'}
            $html = "<!doctype html><html><head><meta charset='utf-8'><title>WinFix Pro report</title><style>body{font-family:Segoe UI,Arial;margin:32px;color:#172235}table{border-collapse:collapse;width:100%}td,th{border:1px solid #ccd5e0;padding:8px;text-align:left}th{background:#edf2f7}</style></head><body><h1>WinFix Pro report</h1><p>Computer: $([System.Net.WebUtility]::HtmlEncode($env:COMPUTERNAME)) | Generated: $((Get-Date).ToString('o')) | Windows: $([System.Net.WebUtility]::HtmlEncode($summary))</p>$rows</body></html>"
            Set-Content -LiteralPath $path -Value $html -Encoding UTF8 -ErrorAction Stop
        }
    }
    return $path
}

Export-ModuleMember -Function Export-WinFixReport
