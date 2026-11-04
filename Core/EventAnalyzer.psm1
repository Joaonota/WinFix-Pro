function Get-WinFixRecentEvents {
    [CmdletBinding()]
    param(
        [ValidateSet('Application','System','Security')][string]$LogName = 'System',
        [ValidateRange(1,720)][int]$Hours = 24,
        [ValidateRange(1,500)][int]$Maximum = 100,
        [int]$EventId
    )
    if ($LogName -eq 'Security' -and -not (Test-WinFixAdmin)) {
        throw 'A leitura do registo Security requer privilégios de administrador.'
    }
    $filter = @{LogName=$LogName; StartTime=(Get-Date).AddHours(-$Hours); Level=1,2,3}
    if ($EventId -gt 0) {$filter.Id = $EventId}
    try {
        Get-WinEvent -FilterHashtable $filter -MaxEvents $Maximum -ErrorAction Stop |
            Select-Object TimeCreated, LogName, Id, LevelDisplayName, ProviderName,
                @{N='Message';E={if ($_.Message) {$_.Message -replace '\s+',' '} else {'(sem descrição)'}}}
    } catch {
        if ($_.FullyQualifiedErrorId -match 'NoMatchingEventsFound') {return @()}
        throw
    }
}

Export-ModuleMember -Function Get-WinFixRecentEvents
