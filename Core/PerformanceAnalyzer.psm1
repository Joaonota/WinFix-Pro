function Get-WinFixTopProcesses {
    [CmdletBinding()]
    param([ValidateRange(1,50)][int]$Count = 15)
    $cpu = @{}
    Get-Process -ErrorAction Stop | ForEach-Object {$cpu[$_.Id] = $_.CPU}
    Start-Sleep -Milliseconds 500
    Get-Process -ErrorAction Stop | ForEach-Object {
        $delta = if ($null -ne $cpu[$_.Id] -and $_.CPU -ge $cpu[$_.Id]) {$_.CPU - $cpu[$_.Id]} else {0}
        [pscustomobject]@{
            Name=$_.ProcessName
            Id=$_.Id
            CpuSeconds=[math]::Round([double]$_.CPU, 2)
            CpuPercentOfOneCore=[math]::Round(($delta / 0.5) * 100, 1)
            MemoryMB=[math]::Round($_.WorkingSet64 / 1MB, 1)
        }
    } | Sort-Object MemoryMB -Descending | Select-Object -First $Count
}

Export-ModuleMember -Function Get-WinFixTopProcesses
