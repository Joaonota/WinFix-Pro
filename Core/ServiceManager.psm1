function Get-WinFixServiceInventory {
    [CmdletBinding()]
    param()
    Get-CimInstance Win32_Service -ErrorAction Stop |
        Sort-Object DisplayName |
        Select-Object Name, DisplayName, State, StartMode, ProcessId, Description
}

function Set-WinFixServiceState {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][ValidateSet('Start','Stop','Restart')][string]$Action,
        [Parameter(Mandatory)][switch]$Confirmed
    )
    $protected = @('RpcSs','DcomLaunch','EventLog','PlugPlay','Power','Winmgmt','SamSs','Lsm','ProfSvc')
    if ($Name -in $protected) { throw "A alteração do serviço essencial '$Name' está bloqueada por segurança." }
    if (-not $Confirmed) { throw 'É necessária confirmação explícita para alterar um serviço.' }
    if (-not (Test-WinFixAdmin)) { throw 'A alteração de serviços requer privilégios de administrador.' }
    $service = Get-Service -Name $Name -ErrorAction Stop
    switch ($Action) {
        'Start' { Start-Service -Name $Name -ErrorAction Stop }
        'Stop' { Stop-Service -Name $Name -ErrorAction Stop }
        'Restart' { Restart-Service -Name $Name -ErrorAction Stop }
    }
    (Get-Service -Name $Name -ErrorAction Stop).WaitForStatus(
        $(if ($Action -eq 'Stop') {[System.ServiceProcess.ServiceControllerStatus]::Stopped} else {[System.ServiceProcess.ServiceControllerStatus]::Running}),
        [timespan]::FromSeconds(30)
    )
    [pscustomobject]@{Name=$service.Name; Action=$Action; FinalState=(Get-Service -Name $Name).Status; Verified=$true}
}

Export-ModuleMember -Function Get-WinFixServiceInventory,Set-WinFixServiceState
