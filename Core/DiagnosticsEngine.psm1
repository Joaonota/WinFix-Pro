function New-WinFixFinding {
    param(
        [string]$Area,
        [ValidateSet('Info','Low','Medium','High','Critical')][string]$Severity,
        [string]$Finding,
        [string]$Evidence,
        [string]$Recommendation
    )
    [pscustomobject]@{
        Area = $Area
        Severity = $Severity
        Finding = $Finding
        Evidence = $Evidence
        Recommendation = $Recommendation
        CollectedAt = (Get-Date).ToString('o')
    }
}

function Invoke-WinFixDiagnostics {
    [CmdletBinding()]
    param()
    $summary = Get-WinFixSystemSummary
    $findings = New-Object 'System.Collections.Generic.List[object]'
    $findings.Add((New-WinFixFinding 'System' 'Info' $summary.Caption "$($summary.Version), build $($summary.BuildNumber), $($summary.Architecture)" 'Confirme que o Windows está dentro do ciclo de suporte aplicável.'))

    if ($null -ne $summary.CpuLoadPercent) {
        $severity = if ($summary.CpuLoadPercent -ge 95) {'High'} elseif ($summary.CpuLoadPercent -ge 85) {'Medium'} else {'Info'}
        $findings.Add((New-WinFixFinding 'Performance' $severity 'Utilização atual do processador' "$($summary.CpuLoadPercent)%" 'Consulte os processos com maior utilização antes de tomar medidas.'))
    }
    if ($null -ne $summary.MemoryUsedPercent) {
        $severity = if ($summary.MemoryUsedPercent -ge 95) {'High'} elseif ($summary.MemoryUsedPercent -ge 85) {'Medium'} else {'Info'}
        $findings.Add((New-WinFixFinding 'Performance' $severity 'Utilização de memória' "$($summary.MemoryUsedPercent)% de $($summary.TotalMemoryGB) GB" 'Analise os processos que mais consomem memória; não são terminados automaticamente.'))
    }
    if ($null -ne $summary.DiskFreeGB) {
        $severity = if ($summary.DiskFreeGB -lt 2) {'Critical'} elseif ($summary.DiskFreeGB -lt 10) {'High'} elseif ($summary.DiskFreeGB -lt 20) {'Medium'} else {'Info'}
        $findings.Add((New-WinFixFinding 'Storage' $severity "Espaço livre em $($summary.SystemDrive)" "$($summary.DiskFreeGB) GB livres de $($summary.DiskSizeGB) GB" 'Reveja a utilização do disco e remova apenas dados que tenha identificado como dispensáveis.'))
    }
    $rebootSeverity = if ($summary.PendingReboot) {'Medium'} else {'Info'}
    $rebootText = if ($summary.PendingReboot) {'Foi detetado um indicador de reinício pendente.'} else {'Não foram detetados indicadores comuns de reinício pendente.'}
    $findings.Add((New-WinFixFinding 'System' $rebootSeverity $rebootText 'Verificados indicadores conhecidos do Windows Update, Component Based Servicing e Session Manager.' 'Guarde o trabalho e reinicie quando adequado; a deteção não cobre todas as aplicações.'))

    foreach ($name in @('EventLog','Winmgmt','RpcSs')) {
        try {
            $service = Get-Service -Name $name -ErrorAction Stop
            $severity = if ($service.Status -eq 'Running') {'Info'} else {'High'}
            $findings.Add((New-WinFixFinding 'Services' $severity "Serviço $name" "Estado: $($service.Status)" 'Verifique o estado esperado para a edição e as políticas deste computador.'))
        } catch {
            $findings.Add((New-WinFixFinding 'Services' 'Low' "Não foi possível consultar o serviço $name" $_.Exception.Message 'Consulte os privilégios e a disponibilidade do serviço.'))
        }
    }

    try {
        $errors = @(Get-WinEvent -FilterHashtable @{LogName='System'; Level=1,2; StartTime=(Get-Date).AddHours(-24)} -MaxEvents 100 -ErrorAction Stop)
        $severity = if ($errors.Count -gt 20) {'High'} elseif ($errors.Count -gt 0) {'Medium'} else {'Info'}
        $findings.Add((New-WinFixFinding 'Events' $severity 'Eventos de erro/críticos nas últimas 24 horas' "$($errors.Count) eventos encontrados (limite consultado: 100)." 'Analise os eventos individualmente e correlacione-os com o horário e os sintomas reportados.'))
    } catch {
        if ($_.FullyQualifiedErrorId -notmatch 'NoMatchingEventsFound') {
            $findings.Add((New-WinFixFinding 'Events' 'Low' 'Não foi possível consultar o registo System' $_.Exception.Message 'Verifique as permissões e a disponibilidade do Windows Event Log.'))
        } else {
            $findings.Add((New-WinFixFinding 'Events' 'Info' 'Sem eventos de erro/críticos no período consultado' 'Registo System, últimas 24 horas.' 'Nenhuma ação imediata.'))
        }
    }
    $allFindings = $findings.ToArray()
    [pscustomobject]@{
        Summary = $summary
        Findings = $allFindings
        Health = Get-WinFixHealthScore -Findings $allFindings
        CreatedAt = (Get-Date).ToString('o')
    }
}

Export-ModuleMember -Function New-WinFixFinding,Invoke-WinFixDiagnostics
